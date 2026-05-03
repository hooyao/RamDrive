## Context

The bug captured in the proposal is a stale-cache problem: WinFsp's kernel keeps `FileInfo` (size, attributes, existence) cached for `FileInfoTimeout` milliseconds. When that timeout is `uint.MaxValue`, the cache effectively never expires, and the user-mode FS is solely responsible for invalidating it after every mutation. The current `WinFspRamAdapter` invalidates nothing, so any `Open(name) → NotFound` answer becomes permanent — even after a subsequent `Rename(other → name)` makes that name valid.

WinFsp provides one mechanism for this: the kernel-side equivalent of `NtNotifyChangeDirectoryFile`, exposed in the user-mode SDK as `FspFileSystemNotify(FileSystem, NotifyInfo, Size)`. It takes a buffer of `FSP_FSCTL_NOTIFY_INFO` records (a 12-byte header `{Size, Filter, Action}` plus an inline UTF-16 file name relative to the mount root). The reference user (winfsp's `fuse.c::fsp_fuse_notify`) pushes one record per call; case-insensitive mounts must `CharUpperBuffW` the path first because the FSD normalizes by upper-casing internally and `Notify` paths must match the normalized form.

The `WinFsp.Native` binding currently exposes only `FspFileSystemNotifyBegin/End` (the rename-blocking framing helpers) but not `FspFileSystemNotify` itself or the `FSP_FSCTL_NOTIFY_INFO` struct, so the adapter cannot send any invalidations even if it wanted to.

## Goals / Non-Goals

**Goals:**
- Adapter sends a `FspFileSystemNotify` for every path whose existence, content, or attributes changed in a callback the kernel may have cached.
- After the fix, `LevelDbReproTests` passes with the test fixture pinned at `FileInfoTimeout = uint.MaxValue` — i.e. notifications alone (without any timeout-based fallback) keep leveldb correct.
- A new `FileInfoTimeoutMs` option lets operators bound the cache lifetime as defence in depth. Default `1000` ms.
- Steady-state cache hit rate for sequential read/write workloads stays within noise of the current behaviour (verified via existing `RamDrive.Benchmarks`).
- Notification path is allocation-free on the hot path — it is invoked from inside FS callbacks, which the binding's `CLAUDE.md` declares zero-alloc.

**Non-Goals:**
- Rewriting the binding to wrap full `NotifyBegin/End` rename-batching (single-shot `Notify` is enough for this bug).
- Implementing notifications for callbacks the kernel doesn't cache (e.g. `Read`, `Write` content) or for IRP types we don't generate (`STREAM_*`, `EA_*`).
- Eliminating the cache by setting `FileInfoTimeout=0` — that would regress throughput by ~3×, and it is the operator's choice via the new option.
- Touching `RamFileSystem.Move` / `Delete` semantics. Notifications are added at the adapter layer where path strings are already in hand.

## Decisions

### 1. Add only `FspFileSystemNotify` (single-shot), not `Begin/End` framing

`NotifyBegin/End` exist to atomically batch many notifications relative to concurrent renames. Our mutators each emit at most 2 notifications (rename emits two: `REMOVED(old)`, `ADDED(new)`), and we never need to atomically observe a multi-event view. The `fuse.c` reference implementation in winfsp itself uses single-shot `Notify` without `Begin/End` for exactly this reason. Adding the framing API is dead weight today; can be added later as a non-breaking extension.

**Alternative considered:** expose all three (`Begin`, `Notify`, `End`) and require callers to wrap. Rejected — it would force every caller to write boilerplate for the common case, and the framing semantics (rename blocking, `STATUS_CANT_WAIT` retry loop) are easy to misuse.

### 2. Public surface: a single overload `FileSystemHost.Notify(uint filter, uint action, string fileName)`

The native struct is variable-length (header + inline UTF-16). We hide that by accepting a managed string and building the buffer on a stack-allocated `Span<byte>` inside `Notify`. No managed heap allocation; one memcpy of the path bytes. The path arg is the same shape as what `IFileSystem` callbacks receive (backslash-rooted, e.g. `\GCM Store\CURRENT`), so callers can pass `fileName` parameters straight through.

```csharp
public int Notify(uint filter, uint action, string fileName);
```

`filter` and `action` are exposed as `uint` constants on a new `FileNotify` static class (e.g. `FileNotify.ChangeFileName`, `FileNotify.ActionRemoved`). We keep them as raw `uint` rather than enums to match WinFsp's bit-or style and avoid a managed enum-marshalling layer.

**Alternative considered:** `NotifyBatch(IEnumerable<...>)`. Rejected for now — only adds value once we need the rename-blocking framing, and it would force callers into IEnumerable allocations.

### 3. Case-insensitive normalization is the binding's job, not the caller's

`fuse.c` upper-cases the path before calling `FspFileSystemNotify` because the FSD upper-cases internally for case-insensitive volumes, and the cache lookup happens on the upper-cased form. We replicate this inside `FileSystemHost.Notify`: if `CaseSensitiveSearch == false` (the property the host already exposes), upper-case in place inside the stack buffer before the P/Invoke. Callers always pass the user-supplied case.

**Why in the binding, not the adapter:** the adapter doesn't (and shouldn't) know what the kernel does to canonicalize. Keeping this in the binding means every adapter automatically benefits and we won't see the same bug in a different consumer.

### 4. Notification matrix for adapter callbacks

Each callback that can change cached state emits exactly the notifications below. The matrix maps the user-visible IRP outcome to `(Filter, Action, Path)` tuples:

| Adapter callback | Notification(s) |
|---|---|
| `MoveFile(old, new, replace=false)` | `(ChangeFileName, ActionRenamedOldName, old)` + `(ChangeFileName, ActionRenamedNewName, new)` |
| `MoveFile(old, new, replace=true)` and target existed | same as above; the implicit removal of `existing` is covered by `ActionRenamedNewName` semantics (FSD invalidates target cache on `RENAMED_NEW_NAME`) |
| `Cleanup(_, Delete)` for a file | `(ChangeFileName, ActionRemoved, path)` |
| `Cleanup(_, Delete)` for a directory | `(ChangeDirName, ActionRemoved, path)` |
| `OverwriteFile` | `(ChangeSize \| ChangeLastWrite, ActionModified, path)` — overwrites truncate to 0 then re-grow; size-cache stale is the failure mode |
| `CreateFile(_, FileDirectoryFile)` | `(ChangeDirName, ActionAdded, path)` — only when the FSD might have negatively cached the name (`FileInfoTimeout > 0`); always cheaper to send than diagnose later |
| `CreateFile(...)` for a regular file | `(ChangeFileName, ActionAdded, path)` |
| `SetFileSize` (non-allocation) | `(ChangeSize \| ChangeLastWrite, ActionModified, path)` |
| `SetFileAttributes` | `(ChangeAttributes \| ChangeLastWrite, ActionModified, path)` |
| `Write` | none — content cache is invalidated by the kernel via the page cache; size growth is handled because every `Write` callback returns the new `FspFileInfo` with the updated size, which the FSD already uses |
| `Read` | none |

The asymmetry vs the proposal (which only mentioned Move/Delete/Overwrite) is intentional: the leveldb bug is one symptom of a pattern (negative-cache pollution), and `CreateFile` for a never-before-seen name is the other half of that pattern. Adding `Add` notifications is one extra IPC per create; cheap and bug-resistant.

### 5. Notification failures are non-fatal

`Notify` can return `STATUS_INVALID_DEVICE_REQUEST` if the volume is being torn down, or other transient errors. The adapter must not fail the originating IRP because a notification failed — the user-mode mutation already succeeded. We log at `Trace` level (debug builds) and continue. The worst case is reverting to the pre-fix behaviour (stale cache) for the affected path, bounded by `FileInfoTimeoutMs`.

### 6. `FileInfoTimeoutMs` default = 1000

- 0 disables the cache entirely (~3× throughput regression — only useful for diagnostics).
- 1000 is long enough that hot-path `OpenFile`/`GetFileInfo` for the same file inside a single application operation hits cache.
- Short enough that any path missed by the notification matrix recovers within 1 second instead of mount lifetime.
- `uint.MaxValue` remains a legal value; documented as "trust the notification matrix completely". CI runs the integration tests at `uint.MaxValue` so a missed notification is caught before merge.

**Alternative considered:** default 0. Rejected — the throughput hit is large enough that users will turn it back on without understanding the trade-off.

## Risks / Trade-offs

- **Risk**: We miss a callback in the notification matrix and a different application hits the same negative-cache bug. → **Mitigation**: integration test fixture is pinned at `uint.MaxValue` so any miss for a tested workflow fails CI; matrix is documented in the spec for future reviewers.
- **Risk**: Case-insensitive normalization regresses to a stale-cache bug if the binding's `CaseSensitiveSearch` property doesn't match what the FSD actually does. → **Mitigation**: cross-check with `fuse.c` reference, and the integration test exercises mixed-case paths (`Z:\TEMP\...` vs `Z:\Temp\...`).
- **Risk**: `Notify` happens inside the FS callback, which holds adapter locks. If the kernel synchronously processes the notification (which it does — `FspFsctlNotify` is a synchronous IOCTL), we could deadlock if the kernel re-enters our adapter. → **Mitigation**: review the matrix to ensure no `Notify` is called from a callback that holds `_structureLock`. In practice, `MoveFile` returns to the adapter after `_fs.Move` releases the lock, then we send `Notify` — so we are outside the lock. Encode this as a comment on the adapter wrapper.
- **Trade-off**: One extra P/Invoke + small memcpy per mutation. For mutation-heavy workloads (chaos test ~10K ops/sec) this is negligible; for read-heavy workloads it's zero overhead.
- **Trade-off**: `FileInfoTimeoutMs=1000` is operator-visible behaviour — a mount that previously cached `FileInfo` for the lifetime of the mount now refreshes every second. Steady-state throughput in benchmarks should not regress because `OpenFile`/`GetFileInfo` from the kernel are still synchronous-completed `ValueTask`s with no allocation.

## Migration Plan

1. **`winfsp-native` repo**: add `Notify` API on a feature branch. Bump version to a pre-release suffix (e.g. `0.1.2-pre.1`). Publish as a local project ref initially; `RamDrive.Cli` consumes via local path while iterating, then switches to the pinned NuGet version once the binding change is reviewed and tagged.
2. **`RamDrive` repo**: implement the matrix and config option in one PR that depends on the new binding version. Merge order: binding tag → bump RamDrive's `<PackageReference>` → adapter changes.
3. **Rollback**: setting `FileInfoTimeoutMs=0` in `appsettings.jsonc` restores pre-fix correctness without rebuilding (notifications become no-ops because there is no cache to invalidate).
4. **No data migration**: the change is mount-time only. Existing on-disk data (there is none — it's a RAM disk) is unaffected.

## Open Questions

- Should `WriteFile` send `(ChangeSize, ActionModified)` for the case where size grew? Returning `FspFileInfo` from `WriteFile` may already cover this, but I have not verified it under `FileInfoTimeout=MAX`. Will write a focused integration test as part of `tasks` and decide.
- Should we expose `NotifyBegin/End` framing in the binding for completeness? Defer until a real consumer needs it; YAGNI for this change.
