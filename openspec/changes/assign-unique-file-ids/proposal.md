## Why

JetBrains dotTrace fails to start profiling when `%TEMP%` points at the RamDrive, with
`copy: file exists: "...JetBrains.Etw.Collector.exe", "...jetbrainsproc_<GUID>" (generic:17)`.
Root-caused against the winfsp source and confirmed with a C++ repro:

- `WinFspRamAdapter.MakeFileInfo` never set `FspFileInfo.IndexNumber`, and `Init` never set a
  `VolumeSerialNumber`, so **every file reported `(VolumeSerialNumber, file id) = (0, 0)`**.
- The MSVC STL `std::filesystem::copy_file` (and Win32 same-volume copy fast paths) compare that
  pair of the source and destination to detect "copying a file onto itself". With every node
  reporting id `0`, a **same-volume** copy of two *distinct* files is rejected with
  `std::errc::file_exists` (`generic:17`).
- dotTrace's ETW-collector deploy copies `JetBrains.Etw.Collector.exe` into a freshly-created
  `jetbrainsproc_<GUID>` working directory. When `%TEMP%` is on the RamDrive, source and
  destination are on the **same** volume, the `(0,0)` ids collide, and the deploy throws.

This is why the failure was intermittent on the same drive: when `%TEMP%` was on a *different*
NTFS volume, the collector source and the `jetbrainsproc` destination were on different volumes,
the volume serials differed, `copy_file` skipped the file-id check, copied the bytes, and
succeeded. The same workload on real NTFS always works because NTFS reports unique file ids.

Confirmed by direct repro: a same-volume `std::filesystem::copy` of a file into a fresh directory
threw `ec: 17 file exists` on the pre-fix RamDrive and returned `OK` after the fix; two distinct
files reported file id `0`/`0` before and unique ids after.

This was missed because the differential checker's `Comparators.CompareFileInfo` deliberately skips
`IndexNumber` ("per-instance index counter, never matches") — so it never verified that production
emits unique, non-zero ids at all.

## What Changes

- `FileNode` gains a unique, stable, non-zero `IndexNumber`, assigned from a monotonic counter
  (starting at 1) at construction. This is the NTFS-style file id / inode number.
- `WinFspRamAdapter.MakeFileInfo` surfaces it as `FspFileInfo.IndexNumber = node.IndexNumber`.
- `WinFspRamAdapter.Init` sets a non-zero `host.VolumeSerialNumber` (`0x52414D44`, "RAMD"), matching
  what real volumes report.
- New unit tests (`tests/RamDrive.Core.Tests/FileIdUniquenessTests.cs`): file ids are non-zero,
  unique across distinct nodes, surfaced through `GetFileInformation`, and stable across calls.
- New integration test (`tests/RamDrive.IntegrationTests/FileIdUniquenessTests.cs`, UNC mount):
  distinct files report distinct non-zero file ids; a same-volume copy of a file into a fresh
  directory succeeds (the exact dotTrace deploy step).

## Capabilities

### New Capabilities

- `file-id-uniqueness`: every file/directory exposes a unique, stable, non-zero NTFS-style file id
  (`FspFileInfo.IndexNumber`), and the volume reports a non-zero serial, so that same-volume copy
  operations that compare `(volume serial, file id)` to detect self-copies behave like NTFS.

### Modified Capabilities

None.

## Impact

- **Code**: `src/RamDrive.Core/FileSystem/FileNode.cs` (unique `IndexNumber`),
  `src/RamDrive.Core/FileSystem/WinFspRamAdapter.cs` (`MakeFileInfo` + `Init` volume serial).
- **Tests**: new unit-test file in `tests/RamDrive.Core.Tests`; new integration-test file in
  `tests/RamDrive.IntegrationTests` (UNC mount via the existing `RamDrive` collection fixture — never
  a drive letter).
- **Specs**: new capability `openspec/specs/file-id-uniqueness/spec.md` (created on archive).
- **Behavioural impact for users**: JetBrains dotTrace (and any tool that deploys via a same-volume
  `std::filesystem::copy_file`, e.g. self-extracting helpers) works with `%TEMP%` on the RamDrive.
- **Differential oracle**: `MemfsReferenceFs` already assigns a unique `IndexNumber`, so production
  now matches the oracle here. `Comparators.CompareFileInfo` still skips the field (the two adapters
  use independent counters and will never produce identical ids); uniqueness is pinned by the new
  unit tests instead.
- **TLA+ model**: no change. File identity is below the model's abstraction (it tracks page/pool
  state and data tags, not inode numbers).
- **Performance**: one `Interlocked.Increment` per node creation; zero hot-path (Read/Write) impact.
