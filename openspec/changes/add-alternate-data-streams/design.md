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

- No change to ordinary (non-stream) file/directory behaviour.
- At most a stream on the volume root, on a file, or on a named directory; a
  stream may not itself be a directory (`CreateDirectory` rejects any `:`).
- No TLA+ change: the model abstracts file content as pool/page state and data
  tags, not the namespace attachment of streams.
