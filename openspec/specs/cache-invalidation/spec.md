# cache-invalidation

## Purpose

Path-mutating WinFsp callbacks must notify the WinFsp kernel cache so subsequent opens/reads see fresh state, even when `FileInfoTimeout` is large or `uint.MaxValue`. Covers the binding-level Notify API, the adapter-level notification matrix, and a worst-case-timeout regression test.

## Requirements


### Requirement: Adapter SHALL invalidate kernel FileInfo cache after path-mutating callbacks

When a user-mode callback in `WinFspRamAdapter` changes the existence, name, size, or attributes of a path, the adapter MUST send an `FspFileSystemNotify` for every affected path before returning success to WinFsp. The notification carries the path the kernel uses for cache lookup, so future opens and reads observe the post-mutation state regardless of `FileInfoTimeoutMs`.

The notification path MUST NOT cause the originating callback to fail. If `FspFileSystemNotify` returns a non-success NTSTATUS, the adapter MUST log at trace level and return success for the original IRP. The mutation has already taken effect in `RamFileSystem`; the cache is now allowed to be stale until `FileInfoTimeoutMs` elapses.

The notification MUST be sent outside any locks held by `RamFileSystem` (`_structureLock`) or `PagedFileContent` (`_lock`). Sending a kernel-synchronous notification under a user-mode lock that the kernel might re-enter is a reentrancy hazard; the adapter wires `Notify` calls after the inner-FS call returns.

#### Scenario: Rename emits old-name and new-name notifications
- **WHEN** the adapter's `MoveFile` callback is invoked with `oldFileName` and `newFileName` and the underlying `RamFileSystem.Move` returns success
- **THEN** the adapter sends `FspFileSystemNotify(ChangeFileName, ActionRenamedOldName, oldFileName)` followed by `FspFileSystemNotify(ChangeFileName, ActionRenamedNewName, newFileName)`
- **AND** subsequent `OpenFile(newFileName)` from a different handle observes the renamed node's size and attributes (not a stale "name not found" or zero-size record)

#### Scenario: Rename-replace invalidates the displaced target's cache
- **WHEN** the adapter's `MoveFile` is invoked with `replaceIfExists=true` and `newFileName` already exists
- **THEN** the adapter sends `FspFileSystemNotify(ChangeFileName, ActionRenamedNewName, newFileName)`, which the FSD interprets as "this name now refers to a different node — drop any cached state"
- **AND** subsequent reads on `newFileName` return the source node's content, not zero bytes from the displaced target's cached size

#### Scenario: Delete emits a removal notification
- **WHEN** the adapter's `Cleanup` is invoked with `CleanupFlags.Delete` and the path is a file
- **THEN** the adapter sends `FspFileSystemNotify(ChangeFileName, ActionRemoved, path)`
- **AND** any subsequent `OpenFile(path)` call returns `ObjectNameNotFound` rather than reusing a cached `FileInfo` from before the delete

#### Scenario: Directory delete uses the directory filter
- **WHEN** the adapter's `Cleanup` is invoked with `CleanupFlags.Delete` and the path is a directory
- **THEN** the adapter sends `FspFileSystemNotify(ChangeDirName, ActionRemoved, path)` instead of `ChangeFileName`

#### Scenario: Overwrite invalidates size and timestamp caches
- **WHEN** the adapter's `OverwriteFile` callback truncates the existing file to zero
- **THEN** the adapter sends `FspFileSystemNotify(ChangeSize | ChangeLastWrite, ActionModified, path)`
- **AND** the kernel-cached `FileInfo.FileSize` is invalidated, so a subsequent `GetFileInformation` call sees `FileSize=0` rather than the pre-overwrite value

#### Scenario: Create emits an addition notification to defeat negative caching
- **WHEN** the adapter's `CreateFile` callback creates a new regular file at `path`
- **THEN** the adapter sends `FspFileSystemNotify(ChangeFileName, ActionAdded, path)`
- **AND** a previous negative cache entry for `path` (a recently failed `OpenFile` returning `ObjectNameNotFound`) is invalidated, so the new file becomes immediately visible to other handles

#### Scenario: Directory create uses the directory filter
- **WHEN** the adapter's `CreateFile` callback creates a directory at `path`
- **THEN** the adapter sends `FspFileSystemNotify(ChangeDirName, ActionAdded, path)`

#### Scenario: SetFileSize invalidates size cache
- **WHEN** the adapter's `SetFileSize` callback runs with `setAllocationSize=false` and changes the logical length
- **THEN** the adapter sends `FspFileSystemNotify(ChangeSize | ChangeLastWrite, ActionModified, path)`

#### Scenario: SetFileAttributes invalidates attributes cache
- **WHEN** the adapter's `SetFileAttributes` callback changes attributes or any timestamp
- **THEN** the adapter sends `FspFileSystemNotify(ChangeAttributes | ChangeLastWrite, ActionModified, path)`

#### Scenario: Notification failure does not surface as IRP failure
- **WHEN** `FspFileSystemNotify` returns a non-success NTSTATUS from inside any of the above callbacks
- **THEN** the adapter logs the failure at trace level
- **AND** the adapter returns the success result for the original IRP (the user-mode mutation is the source of truth; cache staleness is bounded by `FileInfoTimeoutMs`)

### Requirement: WinFsp.Native binding SHALL expose a public Notify API for IFileSystem implementations

The `WinFsp.Native` binding MUST expose a `FileSystemHost.Notify(uint filter, uint action, string fileName)` method that wraps `FspFileSystemNotify`. The method packages the path into the variable-length `FSP_FSCTL_NOTIFY_INFO` buffer (12-byte header + inline UTF-16 file name) using a stack-allocated buffer, with no managed heap allocation on success.

The binding MUST publish well-known constants for the `Filter` and `Action` parameters under a `FileNotify` static class so callers do not have to define them themselves. Constants exposed: `ChangeFileName`, `ChangeDirName`, `ChangeAttributes`, `ChangeSize`, `ChangeLastWrite`, `ChangeLastAccess`, `ChangeCreation`, `ChangeSecurity`, `ActionAdded`, `ActionRemoved`, `ActionModified`, `ActionRenamedOldName`, `ActionRenamedNewName`.

The binding MUST normalize the file name to upper case in place (inside the stack buffer, after copying) when the host's `CaseSensitiveSearch` flag is `false`, because the WinFsp driver's cache is keyed on the upper-cased form for case-insensitive volumes. Callers SHALL pass the user-supplied case unchanged.

The `Notify` method MUST return the underlying NTSTATUS so callers can decide how to handle errors (the adapter, per the requirement above, treats this as non-fatal). It MUST NOT throw managed exceptions for normal NTSTATUS error returns.

#### Scenario: Notify packs a single notification record and forwards it
- **WHEN** a caller invokes `host.Notify(FileNotify.ChangeFileName, FileNotify.ActionAdded, @"\GCM Store\CURRENT")`
- **THEN** the binding builds a `FSP_FSCTL_NOTIFY_INFO` buffer containing one record `{Size = 12 + 2*pathLen, Filter = ChangeFileName, Action = ActionAdded, FileNameBuf = "\\GCM Store\\CURRENT"}`
- **AND** invokes `FspFileSystemNotify` with that buffer and returns its NTSTATUS

#### Scenario: Case-insensitive mounts upper-case the path before notifying
- **WHEN** the host has `CaseSensitiveSearch == false` and the caller invokes `host.Notify(..., @"\Temp\CURRENT")`
- **THEN** the buffer passed to `FspFileSystemNotify` contains the path `\TEMP\CURRENT` (upper-cased), matching the FSD's internal cache key
- **AND** the kernel cache entry for that upper-cased path is invalidated

#### Scenario: Case-sensitive mounts pass the path through unchanged
- **WHEN** the host has `CaseSensitiveSearch == true` and the caller invokes `host.Notify(..., @"\Temp\Current")`
- **THEN** the buffer passed to `FspFileSystemNotify` contains the path `\Temp\Current` byte-for-byte

#### Scenario: Notify is allocation-free on the hot path
- **WHEN** `host.Notify(...)` is called repeatedly inside a tight loop with paths that fit in the stack-allocated buffer (<= 1024 chars)
- **THEN** no managed heap allocation occurs in the binding (verified by `GC.GetAllocatedBytesForCurrentThread()` delta == 0 across the loop, excluding the marshalled string parameter itself)

#### Scenario: Notify returns NTSTATUS, does not throw
- **WHEN** `FspFileSystemNotify` returns a non-success NTSTATUS (e.g. because the volume is being torn down)
- **THEN** `host.Notify` returns that NTSTATUS to the caller without raising a managed exception

### Requirement: Cache-invalidation behavior is regression-tested at the worst-case timeout

The integration test fixture (`RamDriveFixture`) MUST be configured with `FileInfoTimeout = uint.MaxValue` so that cache invalidation depends entirely on the notification matrix — never on time-based expiration. A regression in the matrix (a missed callback or wrong filter/action) will then produce a stale-cache failure in CI rather than appearing only against real Chromium with the production default.

The test suite MUST include a `LevelDbReproTests` class that exercises the exact Win32 sequence captured from Chromium's leveldb env when opening a fresh database:

1. `CreateFile(dbtmp, GENERIC_WRITE, CREATE_ALWAYS)` (default cached I/O — no `FILE_FLAG_NO_BUFFERING` or `FILE_FLAG_WRITE_THROUGH`)
2. `WriteFile(dbtmp, "MANIFEST-000001\n", 16)`
3. `FlushFileBuffers(dbtmp)` and `CloseHandle(dbtmp)`
4. `MoveFileEx(dbtmp, CURRENT, MOVEFILE_REPLACE_EXISTING)`
5. `CreateFile(CURRENT, GENERIC_READ, OPEN_EXISTING)` and `ReadFile(CURRENT, 8192)`

Step 5 MUST return all 16 bytes of the original payload.

#### Scenario: Win32 leveldb sequence reads back the renamed content
- **WHEN** the test runs the five-step Win32 sequence above against the integration fixture (which is mounted with `FileInfoTimeout = uint.MaxValue`)
- **THEN** `ReadFile` after the rename returns 16 bytes equal to `"MANIFEST-000001\n"`
- **AND** `ReadFile` does NOT return `END_OF_FILE` or 0 bytes

#### Scenario: Removing a notification call fails the regression test
- **WHEN** the implementation of `MoveFile` in the adapter is modified to skip the post-rename `Notify` calls
- **THEN** `LevelDbReproTests.Win32_LevelDb_Sequence_Cached` fails with `readBytes == 0` or content mismatch

#### Scenario: Mixed-case paths still invalidate correctly
- **WHEN** the test sequence uses a path with mixed case (e.g. `\Temp\dbtmp` rename to `\Temp\CURRENT`)
- **THEN** the post-rename read returns the full payload regardless of how the upper-cased FSD cache key compares to the user-supplied path
