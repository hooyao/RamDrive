## 1. Core model — `StreamPath` and `RamFileSystem`

- [x] 1.1 New `src/RamDrive.Core/FileSystem/StreamPath.cs`: `Normalize`, `HasStream`,
      `SeparatorIndex`/`SplitLeaf`, `GetBasePath`; splits on the FIRST `:` in the leaf
      and strips an explicit `:$DATA` suffix.
- [x] 1.2 `RamFileSystem`: stream-aware create/read/write; a named stream is a sibling
      `FileNode` keyed `baseName + ":" + streamName`.
- [x] 1.3 `GetNamedStreams(basePath)` returns the named streams of an object.
- [x] 1.4 Cascade delete: deleting a base object removes all of its named streams.
- [x] 1.5 Cascade move: moving a base object carries its streams; replacing a
      destination discards the destination's streams.
- [x] 1.6 Implicit base creation: addressing a stream of a missing object creates the
      base (zero-length) plus the stream.

## 2. Adapter — `WinFspRamAdapter`

- [x] 2.1 Enable `host.NamedStreams = true` in `Init`.
- [x] 2.2 Implement `GetStreamInfo` (main stream first for non-directories, then named
      streams, terminated by `EndStreamInfo`; directories report only named streams).
- [x] 2.3 Filter stream nodes out of `ReadDirectory` results and out of emptiness checks.
- [x] 2.4 Reject moving an individual stream (`MoveFile` guard); keep `CanDelete` correct
      for stream nodes.

## 3. NTFS-matching legality of a bare stream (regression fix)

- [x] 3.1 `RamFileSystem.CreateFile`: accept an EMPTY base name (path shape `\:name`)
      only when the parent is the volume root; otherwise return `null`. Streams on
      named objects (`dir:name`, `file:name`) unchanged.
- [x] 3.2 Confirm no other creation path needs the guard (`CreateDirectory` already
      rejects any `:`; `Open`/`Read`/`Write` do not create nodes).

## 4. Deferred: exact status code for the rejection

- [ ] 4.1 Once real applications are observed against the rejection, decide whether
      the plain failure (`STATUS_OBJECT_PATH_NOT_FOUND`) is sufficient for callers.
- [ ] 4.2 If the exact NTFS code is required, surface `STATUS_OBJECT_NAME_INVALID`
      (0xC0000033) for an empty base name whose parent is not the volume root, and
      tighten `A_bare_stream_inside_a_subdirectory_is_rejected` to assert the code.

## 5. Tests — `tests/RamDrive.IntegrationTests/AlternateDataStreamsTests.cs`

- [x] 5.1 Round-trip a named stream; the main stream is untouched.
- [x] 5.2 A named stream reports its own size (base and stream sizes independent).
- [x] 5.3 Creating a stream on a missing base creates the base file (zero length).
- [x] 5.4 Named streams are hidden from `Directory.GetFiles`/`GetFileSystemEntries`.
- [x] 5.5 `FindFirstStreamW`/`FindNextStreamW` enumerate main + named streams.
- [x] 5.6 A plain file reports only the main stream (what `ReplaceFileW` needs).
- [x] 5.7 Deleting one stream keeps the base and the others.
- [x] 5.8 Deleting the base removes all its streams.
- [x] 5.9 Moving the base moves its streams.
- [x] 5.10 Replacing the base discards the destination's streams.
- [x] 5.11 A directory can carry named streams; they do not surface as entries.
- [x] 5.12 Deleting a directory removes its streams.
- [x] 5.13 The volume root can carry named streams, visible via `FindFirstStreamW`.
- [x] 5.14 Stream names are case-insensitive.
- [x] 5.15 A second `:` in the leaf is rejected by the platform.
- [x] 5.16 A bare stream inside a subdirectory is rejected (the reported bug).
- [x] 5.17 A stream on a named subdirectory is allowed (positive control).

## 6. Verify and finalise

- [x] 6.1 `dotnet build` Release — no new warnings.
- [x] 6.2 `RamDrive.Core.Tests` — all green.
- [x] 6.3 `RamDrive.IntegrationTests` — all green (AlternateDataStreamsTests 17/17).
- [x] 6.4 `RamDrive.IntegrationTests` with the differential checker enabled — all green
      (the oracle agrees on the new rejection).
- [ ] 6.5 Manual acceptance against a mounted volume: `dir\:ads` rejected, `\:ads`
      allowed and listable by stream enumeration, `dir:ads` allowed.
- [ ] 6.6 `openspec validate add-alternate-data-streams`.
