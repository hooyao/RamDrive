# Design: assign unique file ids (IndexNumber)

## Context

`WinFspRamAdapter.MakeFileInfo` left `FspFileInfo.IndexNumber` at its default 0 for every node, and
`Init` left `VolumeSerialNumber` at 0. So `GetFileInformationByHandle` reported
`(VolumeSerialNumber, nFileIndex) = (0, 0)` for every file. The MSVC STL `std::filesystem::copy_file`
uses that pair to decide whether the source and destination paths are the same file (to refuse
copying a file onto itself). On a same-volume copy of two distinct files, `(0,0) == (0,0)` →
`std::errc::file_exists`. That is the dotTrace ETW-collector deploy failure.

## Why it was intermittent / hard to see

- Cross-volume copies (source on NTFS, destination on RamDrive) have different volume serials, so
  `copy_file` skips the file-id comparison entirely and just copies bytes. The same dotTrace run
  succeeded or failed purely depending on whether `%TEMP%` put source and destination on the same
  volume.
- The differential checker's `Comparators.CompareFileInfo` intentionally skips `IndexNumber` (the
  production adapter and the memfs oracle use independent counters, so their ids legitimately
  differ). That skip also meant the checker never asserted production emits *unique, non-zero* ids,
  so the bug was invisible to the existing test suite.

## Decision: monotonic per-node counter, mirror memfs

`MemfsReferenceFs` (the winfsp reference port) already assigns `IndexNumber` from
`Interlocked.Increment(ref _nextIndexNumber)` starting at 1. We do the same on `FileNode`:

- A `private static long _nextIndexNumber` on `FileNode`, incremented in the constructor, exposed as
  `public ulong IndexNumber { get; }`. Static is correct here: ids must be unique across the whole
  tree, not per-directory, and the field never needs to reset for the life of the process. Starting
  at 1 keeps 0 reserved for "unknown".
- `MakeFileInfo` sets `IndexNumber = node.IndexNumber`.

This is the smallest change that matches NTFS semantics and the existing oracle. Alternatives
considered and rejected:

- **Hash of the path** — not stable across rename, and collisions are possible. File ids must be
  stable and unique; a counter guarantees both.
- **Pointer / GC handle** — not stable under GC compaction and not meaningful as a 64-bit id.

## Volume serial

`Init` sets `host.VolumeSerialNumber = 0x52414D44` ("RAMD"). `copy_file` checks the volume serial
first; a non-zero, consistent serial is the well-formed state. Note: WinFsp surfaces this through
`GetFileInformationByHandle` on Mount Manager drive-letter mounts (production), but a UNC-prefix
mount (the integration-test fixture) may report 0 — so the integration test asserts only that the
serial is *consistent across files on the same mount* and that *file ids are unique and non-zero*,
which is the property `copy_file` actually depends on for the same-volume case.

## Scope

- This is independent of the `reject-owner-change-in-setsecurity` change. Both were found while
  investigating the same dotTrace symptom, but the owner-change fix addresses a separate, real
  SetSecurity bug; the unique-file-id fix is the one that actually resolves the dotTrace
  `copy: file exists` failure.
- No TLA+ change: the model abstracts file content as pool/page state and data tags, not inode
  numbers.
