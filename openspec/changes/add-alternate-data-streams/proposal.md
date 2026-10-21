## Why

RamDrive is often mounted as a scratch/temp volume, and Windows applications
routinely attach metadata to files as NTFS **Alternate Data Streams** (ADS): the
Mark-of-the-Web `Zone.Identifier`, archiver metadata, editor recovery data, some
installers. A RAM volume that ignores such streams (or rejects the operations that
touch them) breaks those tools. `host.NamedStreams` was left disabled and stream
paths were not modelled at all.

## What Changes

- New `StreamPath` helper (`src/RamDrive.Core/FileSystem/StreamPath.cs`): splits a
  path into its base object and named stream, normalises an explicit `:$DATA`
  suffix, and identifies stream paths.
- `RamFileSystem` models each named stream as its own `FileNode`, keyed by the full
  path including `:` and stored as a sibling of its base object (the WinFsp memfs
  model). Supports create/read/write, `GetNamedStreams`, cascade delete, and
  cascade move of a base object's streams.
- `WinFspRamAdapter`: enables `host.NamedStreams`; adds the `GetStreamInfo`
  callback (mandatory once named streams are enabled); filters stream entries out
  of ordinary directory enumeration and emptiness checks; rejects moving an
  individual stream; creates the base object implicitly when a stream of a missing
  object is addressed (NTFS semantics).
- A **bare stream path** (`\:name`, empty base name) is accepted **only directly on
  the volume root**; inside a subdirectory it is rejected, matching NTFS
  (`ERROR_INVALID_NAME`). Streams on named objects (`dir:name`, `file.txt:name`)
  are accepted anywhere.
- New integration tests (`tests/RamDrive.IntegrationTests/AlternateDataStreamsTests.cs`):
  round-trip, size, enumeration via Win32 `FindFirstStreamW`/`FindNextStreamW`,
  hidden-from-enumeration, delete/move cascade, streams on directories and the
  volume root, `:$DATA` normalisation, second-colon rejection, and the
  subdirectory-bare-stream rejection.

## Capabilities

### New Capabilities

- `named-streams`: NTFS Alternate Data Streams on files, directories and the volume
  root — create/read/write, enumerate via `FindFirstStreamW`, keep streams out of
  ordinary directory enumeration, cascade with their base object on move/delete,
  create the base implicitly, and match NTFS on which path shapes are legal.

### Modified Capabilities

None.

## Impact

- **Code**: `src/RamDrive.Core/FileSystem/StreamPath.cs` (new),
  `src/RamDrive.Core/FileSystem/RamFileSystem.cs`,
  `src/RamDrive.Core/FileSystem/WinFspRamAdapter.cs`.
- **Tests**: new `tests/RamDrive.IntegrationTests/AlternateDataStreamsTests.cs`
  (UNC mount via the existing `RamDrive` collection fixture — never a drive letter).
- **Specs**: new capability `openspec/specs/named-streams/spec.md` (created on
  archive).
- **Behavioural impact for users**: tools that store metadata in named streams
  (Mark-of-the-Web, archivers, editors) work with data on the RamDrive.
- **Known limitation (deferred)**: the rejected subdirectory-bare-stream case is
  surfaced as `STATUS_OBJECT_PATH_NOT_FOUND` rather than the exact NTFS
  `STATUS_OBJECT_NAME_INVALID` (0xC0000033). The observable contract (operation
  fails, no object is created) is correct and tested; only the status code differs.
  Tightening it is tracked in `tasks.md` (section 4) and `design.md`, pending a
  check of how real applications react to a plain failure.
- **TLA+ model**: no change. Named streams are a namespace/attach concern below the
  model's data-tag/page-state abstraction.
