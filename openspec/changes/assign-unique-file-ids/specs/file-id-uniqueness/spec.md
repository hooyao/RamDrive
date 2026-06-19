## ADDED Requirements

### Requirement: Every node SHALL expose a unique, stable, non-zero file id

Each file and directory in `RamFileSystem` MUST have a file id that is unique across all live nodes,
stable for the lifetime of the node, and non-zero. The id is held on `FileNode.IndexNumber` and
surfaced to WinFsp as `FspFileInfo.IndexNumber` by `WinFspRamAdapter.MakeFileInfo`. Ids MUST be
assigned from a monotonic counter beginning at 1; the value 0 is reserved to mean "unknown" and MUST
NOT be assigned to a real node.

This mirrors NTFS file ids (inode numbers). The CRT/STL `std::filesystem::copy_file` and the Win32
same-volume copy fast paths compare the `(VolumeSerialNumber, file id)` pair of the source and the
destination to detect an attempt to copy a file onto itself. If distinct files share a file id, a
same-volume copy of two distinct files is wrongly rejected with `std::errc::file_exists`
(`generic:17`) — the dotTrace ETW-collector deploy failure.

`WinFspRamAdapter.Init` MUST also report a non-zero `VolumeSerialNumber` so that on mounts which
surface it (Mount Manager drive-letter mounts) the volume identity is well-formed.

#### Scenario: Distinct nodes have distinct non-zero file ids
- **WHEN** two distinct files (or a file and a directory) are created under the volume
- **THEN** each reports a non-zero `IndexNumber`
- **AND** the two `IndexNumber` values differ

#### Scenario: File id is stable across stat calls
- **WHEN** `GetFileInformation` is invoked twice on the same open file
- **THEN** both calls return the same non-zero `IndexNumber`

#### Scenario: Same-volume copy of a file into a fresh directory succeeds
- **WHEN** a source file on the mounted volume is copied (via `std::filesystem::copy_file` / Win32
  `CopyFile`) into a freshly-created directory on the **same** volume, where the destination file
  does not yet exist
- **THEN** the copy succeeds and the destination file contains the source bytes
- **AND** the copy is NOT rejected with `file_exists` / `STATUS_OBJECT_NAME_COLLISION` due to a
  collision of zero file ids
