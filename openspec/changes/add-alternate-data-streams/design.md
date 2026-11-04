# Design: named streams (NTFS Alternate Data Streams)

## Context

WinFsp exposes alternate data streams through `host.NamedStreams` plus a
`GetStreamInfo` callback. WinFsp's own `memfs` reference implements streams by
treating a stream as an ordinary file whose name contains `:` — `wcschr(FileName,
L':')` finds the stream separator and the whole `base:stream` string is one entry
in the parent directory. RamDrive follows that model because it is the model the
differential oracle (`MemfsReferenceFs`) implements, so the two stay in lockstep.

## Decisions

### One `FileNode` per stream, keyed by the full path

A named stream is stored as a **sibling** of its base object, with the key
`baseName + ":" + streamName`. `\dir\file.txt:meta` is the node `file.txt:meta`
inside `\dir`. This mirrors memfs exactly and makes the existing tree code
(parent/child lookup, delete, move) mostly reusable.

### Stream separator is the FIRST `:` in the leaf; `:$DATA` is normalised

Windows file names may not contain `:`, so any `:` in the last component starts a
stream name (this is also what memfs does). A trailing `:$DATA` type suffix is
equivalent to the plain name (`file.txt::$DATA` == `file.txt`,
`file.txt:s:$DATA` == `file.txt:s`), so `StreamPath` strips it. A *second* `:` in
the leaf is rejected by Win32 before it ever reaches the filesystem
(`ERROR_INVALID_NAME`), so splitting on the first `:` is exactly the input the
filesystem can receive.

### Streams follow their base object

- **Delete** of a base object cascades to all of its named streams.
- **Move/rename** of a base object carries its streams; replacing a destination
  discards the destination's streams (the source object's streams win).
- **Moving an individual stream** is rejected: a stream is attached data, not an
  independently relocatable object.

### Implicit base creation

Addressing the stream of an object that does not exist creates that object
implicitly — a zero-length main stream plus the named stream — matching NTFS
(verified: `File.WriteAllText(@"dir\missing.txt:s", …)` succeeds and leaves a
zero-length `missing.txt`).

### `GetStreamInfo` is mandatory once `host.NamedStreams` is enabled

If the callback is missing or fails, callers that enumerate streams —
`FindFirstStreamW`/`FindNextStreamW`, and the stream-preservation logic inside
`ReplaceFileW` — fail with `STATUS_INVALID_DEVICE_REQUEST` (0xC0000010). The
callback reports the main (unnamed) stream first for non-directories, then every
named stream, terminated by `WinFspFileSystem.EndStreamInfo`.

### A stream shares its base's security descriptor and common metadata

NTFS keeps one MFT record per file: every stream of a file reports the file's
security descriptor, attributes, creation time and file id, while content and
size stay stream-specific. A stream node therefore carries a `BaseNode`
reference (set at creation), and the adapter routes security and common-metadata
reads and writes through `FileNode.MetadataOwner` (`BaseNode ?? this`):

- `GetFileSecurity(ByName)` / `GetFileSecurity` report the base's SD;
  `SetFileSecurity` merges into the **base's** SD, so a change through
  `file:meta` is visible through `file` (and vice versa).
- `MakeFileInfo` (attributes, timestamps, `IndexNumber`) and `SetFileAttributes`
  read/write the base; `FileSize`/`AllocationSize` remain stream-specific.
- I/O timestamp writes (`WriteFile`, `ReadFile`, `SetFileSize`, `OverwriteFile`,
  `Cleanup`) update the base's times, as a write to any stream updates the file
  record.
- When a stream's shared metadata is changed, the kernel cache for the **base**
  name is invalidated as well (the callback was addressed by the stream name).

The differential oracle (`MemfsReferenceFs`) mirrors this with a persistent
`MemfsNode.BaseNode` reference captured at creation (a root stream's base is
the root), so the routing survives the base being unlinked — exactly like
production's `MetadataOwner`.

### Overwriting the main stream removes the file's closed streams

Opening the main stream of an existing file for a truncate (TRUNCATE_EXISTING —
what `File.WriteAllText` does) removes the file's other data streams on NTFS; a
stream still open by another handle survives the truncate, but its deletion is
**deferred to its close** (delete-on-close).

The NTFS contract splits across the WinFsp kernel driver and the user-mode file
system, and both halves are needed:

- **Open streams — the driver's half.** During a Create with truncate the driver
  runs `FspFileNodeOverwriteStreams` (winfsp `sys_create.c`/`sys_file.c`), which
  marks every named stream still open by a handle (`HandleCount > 0`) as
  `DeletePending`. When the last handle of a `DeletePending` node is cleaned,
  the driver's `FspFileNodeCleanup` sends the `Cleanup` transact with
  `CleanupFlags.Delete`, and `WinFspRamAdapter.Cleanup` removes the node. The
  user-mode file system does not model `DeletePending` itself; it simply honors
  the Delete flag the driver sends.
- **Closed streams — the file system's half.** Streams with no open handle are
  not marked by the driver, so `WinFspRamAdapter.OverwriteFile` calls
  `RamFileSystem.DeleteClosedNamedStreams` (removes the base's stream nodes with
  no retained open context) after truncating the MAIN stream, and notifies the
  kernel to drop the removed stream names from its FileInfo cache (unbounded
  staleness otherwise with a large `FileInfoTimeout`). A stream-handle overwrite
  (`file:s` with truncate) truncates only that stream — the full documented NTFS
  "all streams except the one being opened" disposition for named-stream opens
  is deliberately out of scope. The reference mirrors the deletion in
  `MemfsReferenceFs.OverwriteFile` (closed = `RefCount <= 1`).

**Open references are released in `Cleanup`, or in `Close` when the driver
never sends one.** The volume runs with `PostCleanupWhenModifiedOnly`, so the
driver posts the Cleanup transact only for handles that modified the file
(`sys_cleanup.c` — delete, set-allocation, archive bit, write/change time); a
read-only handle delivers no Cleanup at all. The reference is therefore
released on the FIRST of Cleanup/Close that arrives (a per-handle flag in the
operation Context): IRP_MJ_CLEANUP is delivered with the handle close, while
the Close transact is a best-effort async work item
(`FspIopPostWorkRequestBestEffort`, `sys_close.c`) that lags arbitrarily and
can be dropped — a reference released only in Close would keep long-closed
streams looking open when a later overwrite ran, and
`DeleteClosedNamedStreams` skipped them. Both `WinFspRamAdapter` and the
`MemfsReferenceFs` oracle (RefCount: one per namespace entry plus one per open
context; "closed" = `RefCount <= 1`) release it exactly once per handle.

**The operation `Context` and the node's backing pages are finalized in
`Close`, not in `Cleanup`.** With the kernel cache (Cc) enabled, the driver
keeps using the section-owner's `FileOperationInfo` for Cc page read/write
transacts AFTER the handle's Cleanup (write-behind page reads arrive on the
dispatcher after the Cleanup transact); and memory-mapped I/O on a deleted
file is legal after Cleanup — the WinFsp FSD "preserves data and allows a
deleted file to have memory-mapped I/O done on it after the CLEANUP completes"
(`FlushAndPurgeOnCleanup == FALSE`, the default). Clearing the Context or
reclaiming the pages in Cleanup made those late transacts fail with
`STATUS_OBJECT_NAME_NOT_FOUND`, surfacing as a `FileNotFoundException` on the
user's `Write` and a failed `FlushViewOfFile` on a deleted mapping.

The driver posts one Close transact per FILE OBJECT (`FspFsvolClose`,
`sys_close.c`) — there is no node-level gating: a memory mapping (Cc section)
pins its owning file object, so only the SECTION OWNER'S Close is deferred
until the mapping is gone, while every other handle's Close arrives as it
closes, with the section still alive. Reclaiming an unlinked node at its
FIRST Close would therefore free pages a live mapping can still read (stale
reads, cross-file corruption through re-rented pool pages). A node's pages
are reclaimed only in `Close`, and only when every Close has arrived and no
reference remains (`RamFileSystem.ReleaseUnlinkedNode`:
`!IsLinked && OpenReferences == 0 && CloseCount >= CleanupCount`).

**Removed streams are reclaimed only when it is safe.** When the sweep (or a
base delete) unlinks a stream whose handles are all closed,
`RamFileSystem.RetireNode` frees its pages immediately only if the node is
fully closed — no references left and every delivered `Cleanup` already
matched by a delivered `Close` (the adapter counts both,
`FileNode.CleanupCount` / `CloseCount`). A stream whose section owner's
`Close` is still pending (the live mapping pins that file object) is retained
in `_unlinkedNodes` and reclaimed when that Close arrives; freeing it at
overwrite time would drop pages a live mapping can still read. The reference
oracle is deliberately more conservative: it frees data only in `Close`, and
only after every Close has arrived, so a node removed after its Close already
fired keeps its data until GC (a tiny bounded, diagnostics-only leak with no
observable difference).

**Known limitation — stale cache entries after a base delete.** The overwrite
sweep notifies each removed stream name, but a base's DELETE cascades to its
streams without per-stream notifications: a previously cached positive
FileInfo entry for `file:stream` survives the base's deletion until
`FileInfoTimeout`, while NTFS drops it with the file. Follow-up candidate.

**Known limitation — the close/overwrite race.** "Is this stream closed?" is
approximated in user mode from the observed transacts: a handle whose Close
has not been delivered yet still counts as open, so a stream whose last
handle closed moments before the overwrite can survive it (its late Close
then arrives on a still-LINKED node and unlinks nothing). The kernel's own
handle count — the basis of the driver's DeletePending marking — has no
user-mode counterpart. The window requires the Close work item (posted at
handle close) to lag behind the overwrite IRP; in practice it is processed
first.

### Where a bare stream is legal mirrors NTFS (the bug this change fixes)

NTFS accepts a bare stream path — an **empty base name**, shape `\:name` — only
directly on the **volume root** (`C:\:ads`). Inside any subdirectory the same
syntax is refused with `ERROR_INVALID_NAME` (`C:\dir\:ads`). Verified against real
NTFS:

| Path | NTFS result |
|------|-------------|
| `C:\sub\:ads` (empty base, subdirectory) | rejected — `ERROR_INVALID_NAME` (123) |
| `C:\sub:ads` (directory name before the colon) | allowed |
| `C:\:rootads` (empty base, volume root) | allowed, round-trips |
| `C:\sub\f.txt:ads` (stream on a file) | allowed |

An earlier revision of this change accepted the bare form everywhere, so an editor
could "save" a stream inside a subdirectory that NTFS would never create.
`RamFileSystem.CreateFile` now accepts an empty base name **only when the parent is
the volume root**; every named-object form is unchanged.

### Deferred: exact status code for the rejection

The rejected case currently returns `null` from `RamFileSystem.CreateFile`; the
existing adapter mapping turns that into `STATUS_OBJECT_PATH_NOT_FOUND`. NTFS
returns `STATUS_OBJECT_NAME_INVALID` (0xC0000033). The observable contract — the
operation fails and no object is created — is correct. The status code itself is
left as is for now, because the first question is whether real applications behave
correctly with a plain failure (they do their own early validation). If they need
the exact NTFS code, add a small pre-check in `WinFspRamAdapter.CreateFile` (or a
richer `CreateFile` result) that returns `STATUS_OBJECT_NAME_INVALID` for an empty
base name whose parent is not the root.
Tracked in `tasks.md` section 4.

## Scope

- Ordinary (non-stream) file/directory behaviour is unchanged, except that
  overwriting the main stream of a file that carries named streams now removes
  those streams (NTFS TRUNCATE_EXISTING semantics).
- At most a stream on the volume root, on a file, or on a named directory; a
  stream may not itself be a directory (`CreateDirectory` rejects any `:`).
- No TLA+ change: the model abstracts file content as pool/page state and data
  tags, not the namespace attachment of streams.
