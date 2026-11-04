# windows-reparse-links Specification

## Purpose
Define the behavior of junctions and file and directory symbolic links on RamDrive, including their metadata, Windows path resolution, and consistency under concurrent mutations.

## Requirements
### Requirement: Link reparse data is stored and reported
The filesystem SHALL support `IO_REPARSE_TAG_MOUNT_POINT` on directories and `IO_REPARSE_TAG_SYMLINK` on files and directories. A successful set SHALL preserve the reparse buffer, report the reparse attribute and tag, and be visible in directory and file metadata. Query and deletion through a direct reparse-point handle SHALL operate on the link itself.

#### Scenario: Round trip and deletion
- **WHEN** a caller sets valid link data on an empty directory or an empty file and queries the link directly
- **THEN** the stored tag and data are returned and the reparse attribute is reported
- **WHEN** the caller deletes the reparse point with its matching tag
- **THEN** the object remains and its reparse attribute and tag are cleared

#### Scenario: Invalid mutations
- **WHEN** a caller sets link data on a nonempty directory
- **THEN** the operation fails with directory-not-empty and the directory remains unchanged
- **WHEN** a caller sets link data on a nonempty file
- **THEN** the operation fails with invalid-reparse-data and the file remains unchanged
- **WHEN** data is malformed, the tag is unsupported or an existing tag does not match
- **THEN** the operation fails without changing the existing link

### Requirement: Windows resolves links in paths
Link traversal SHALL use WinFsp and Windows reparse resolution, including nested intermediate components, absolute and relative symbolic links, junctions and links to other volumes. A request to open the reparse point itself SHALL not follow the final link. File and directory operations through a link SHALL reach the target and use the target's access checks and cache identity.

#### Scenario: Traverse links
- **WHEN** a caller opens, reads, writes or renames a child through a junction or directory symbolic link
- **THEN** the operation applies to the target directory and its children
- **WHEN** a caller reads through a file symbolic link
- **THEN** it reads the target file

#### Scenario: Links to missing targets and loops
- **WHEN** a caller follows a dangling link
- **THEN** opening its missing target fails while directly opening and querying the link remains possible
- **WHEN** links form a cycle
- **THEN** resolution terminates with a Windows reparse resolution error

#### Scenario: Rename or delete a link
- **WHEN** a caller renames or deletes the link itself
- **THEN** only the link changes and the target remains intact

#### Scenario: Cross-volume routing
- **WHEN** a link targets a different volume or UNC share
- **THEN** Windows routes the request to that target and RamDrive does not interpret it as a local node

#### Scenario: Target access checks
- **WHEN** a target's access control denies a caller read access
- **THEN** reading the target through a file symlink, directory symlink or junction is also denied

#### Scenario: Target identity through links
- **WHEN** a caller opens a target directly and through links
- **THEN** the resulting handles report the same volume and file identity and observe the same contents

### Requirement: Reparse metadata remains coherent
Namespace changes and reparse mutations SHALL be synchronized so that setting a link on a directory with children and creating a child beneath a link cannot both succeed. Link attributes, tag and data SHALL remain consistent under concurrent access and kernel metadata caching.

#### Scenario: Cached metadata changes
- **WHEN** callers query metadata before and after setting or deleting a reparse point with kernel caching enabled
- **THEN** subsequent operations observe the current link state
