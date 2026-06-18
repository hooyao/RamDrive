## 1. Core fix — unique file id on `FileNode`

- [x] 1.1 Add `public ulong IndexNumber { get; }` to `FileNode`, assigned in the constructor from a
      `private static long _nextIndexNumber` via `Interlocked.Increment` (starts at 1; 0 reserved).
- [x] 1.2 Surface it in `WinFspRamAdapter.MakeFileInfo` as `IndexNumber = node.IndexNumber`.
- [x] 1.3 Set a non-zero `host.VolumeSerialNumber` (`0x52414D44`) in `WinFspRamAdapter.Init`.

## 2. Unit tests — `tests/RamDrive.Core.Tests`

- [x] 2.1 New `FileIdUniquenessTests.cs`. `FileNode_IndexNumber_IsNonZeroAndUnique`: two files + a
      dir all have non-zero, pairwise-distinct `IndexNumber`.
- [x] 2.2 `GetFileInformation_ReturnsUniqueNonZeroIndexNumber_ForDistinctFiles`: two distinct files
      opened through the adapter report distinct non-zero `FspFileInfo.IndexNumber`.
- [x] 2.3 `GetFileInformation_IndexNumber_IsStableAcrossCalls`: the id is stable across repeated stats.

## 3. Integration test — `tests/RamDrive.IntegrationTests`

- [x] 3.1 New `FileIdUniquenessTests.cs` in the `[Collection("RamDrive")]` fixture (UNC mount — never
      a drive letter). `DistinctFiles_HaveDistinctNonZeroFileIds`: via `GetFileInformationByHandle`,
      two files report distinct non-zero file ids and share the volume serial.
- [x] 3.2 `SameVolumeCopy_OfFileIntoFreshDirectory_Succeeds`: a same-volume `File.Copy` of a file into
      a freshly-created directory succeeds (the exact dotTrace deploy step that previously threw
      `file_exists`).

## 4. Verify and finalise

- [x] 4.1 `dotnet build` Debug + Release — no new warnings.
- [x] 4.2 `dotnet test tests/RamDrive.Core.Tests` — all green incl. the 3 new file-id tests.
- [x] 4.3 `dotnet test tests/RamDrive.IntegrationTests` — all green incl. the 2 new file-id tests.
- [x] 4.4 Manual acceptance: with `%TEMP%` on the RamDrive, dotTrace profiling starts successfully
      (no `copy: file exists ... jetbrainsproc_<GUID> (generic:17)`). Confirmed across multiple runs.
- [ ] 4.5 `openspec validate assign-unique-file-ids`.
