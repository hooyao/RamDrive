## ADDED Requirements

### Requirement: Named streams SHALL behave like NTFS Alternate Data Streams

A file, a directory, or the volume root MUST be able to carry one or more named
streams, addressed by a `:` in the last path component (`file.txt:meta`) and
normalised so that an explicit `:$DATA` type suffix is equivalent to the plain
name. Streams MUST round-trip their bytes independently of the base object's main
stream, and MUST follow their base object on delete and move.

#### Scenario: A named stream round-trips and does not disturb the main stream
- **WHEN** a file is written with main content and a named stream is written with
  different content
- **THEN** reading the named stream returns its own content
- **AND** reading the base file still returns the main content

#### Scenario: A named stream reports its own size
- **WHEN** a file and each of its named streams have different contents
- **THEN** the base file's length and each stream's length are reported independently

#### Scenario: An explicit `:$DATA` suffix addresses the same stream
- **WHEN** a stream is written as `file.txt:meta` and read back as
  `file.txt:meta:$DATA` (or as `file.txt::$DATA` for the main stream)
- **THEN** the two addresses refer to the same stream

#### Scenario: A directory and the volume root can carry named streams
- **WHEN** a named stream is attached to a directory, or directly to the volume
  root (`\:name`)
- **THEN** it round-trips and is visible to stream enumeration

#### Scenario: Stream names are case-insensitive
- **WHEN** a stream is written as `file.txt:MixedCase` and read back as
  `file.txt:mixedcase`
- **THEN** the same stream is addressed

### Requirement: Named streams SHALL enumerate through the standard stream APIs

Named streams MUST be enumerable through `FindFirstStreamW`/`FindNextStreamW`
(the WinFsp `GetStreamInfo` callback), which reports the main (unnamed) stream
first for non-directories and then every named stream, each with its size.
Named streams MUST NOT appear as ordinary directory entries and MUST NOT make a
directory look non-empty.

#### Scenario: Stream enumeration lists the main and named streams
- **WHEN** a file has main content plus two named streams and its streams are
  enumerated
- **THEN** the main stream and both named streams are reported, each with its size

#### Scenario: A plain file reports only the main stream
- **WHEN** a file with no named streams is enumerated
- **THEN** exactly one entry (the main stream) is reported and the call succeeds
  (this is what `ReplaceFileW` requires while `NamedStreams` is enabled)

#### Scenario: Named streams are hidden from directory enumeration
- **WHEN** a directory contains a file that has named streams
- **THEN** directory enumeration lists only the base object, not its streams

### Requirement: Streams SHALL follow their base object

Deleting a base object MUST delete all of its named streams. Moving or renaming a
base object MUST carry its streams; replacing a destination MUST leave the
destination with the source object's streams. Moving an individual stream MUST be
rejected, and a stream address MUST NOT resolve to a directory.

#### Scenario: Deleting the base removes its streams
- **WHEN** a file with named streams is deleted
- **THEN** the base and all of its named streams no longer exist

#### Scenario: Deleting one stream keeps the base and the others
- **WHEN** one of several named streams is deleted
- **THEN** the base object and the remaining streams are unchanged

#### Scenario: Moving the base moves its streams
- **WHEN** a base object with named streams is renamed or moved
- **THEN** its streams are reachable under the new name and not under the old one

#### Scenario: Replacing the destination discards its streams
- **WHEN** a source object is moved onto an existing destination that has streams
- **THEN** the destination has the source object's content and no longer has its
  own former streams

#### Scenario: Moving a lone stream is rejected
- **WHEN** a rename is requested for an existing stream path rather than its base
- **THEN** the operation is rejected

### Requirement: Addressing a stream of a missing object SHALL create the base

When a named stream of an object that does not exist is addressed for creation,
the base object MUST be created implicitly as a zero-length main stream alongside
the named stream, matching NTFS.

#### Scenario: A stream of a missing base creates the base
- **WHEN** a named stream is written at a path whose base object does not exist
- **THEN** the base object comes into existence with zero length
- **AND** the named stream holds the written content

### Requirement: A bare stream path SHALL be legal only on the volume root

A path whose last component is a bare stream name (an empty base name, shape
`\:name`) MUST be accepted only when its parent is the volume root. Inside any
subdirectory the same shape MUST be rejected and MUST NOT create an object,
matching NTFS (`ERROR_INVALID_NAME`). Streams on named objects — `dir:name` and
`file.txt:name` — MUST remain permitted anywhere.

#### Scenario: A bare stream in a subdirectory is rejected
- **WHEN** a create is requested for a bare stream inside a subdirectory
  (`dir\:name`)
- **THEN** the operation fails
- **AND** no base object and no stream are created

#### Scenario: A bare stream on the volume root is allowed
- **WHEN** a bare stream is created directly on the volume root (`\:name`)
- **THEN** it succeeds and is visible to stream enumeration

#### Scenario: A stream on a named subdirectory is allowed
- **WHEN** a stream is created on a subdirectory using the directory's own name
  before the colon (`dir:name`), inside another directory
- **THEN** it succeeds and round-trips

#### Scenario: A second colon in the leaf is rejected by the platform
- **WHEN** a path with a second `:` in the last component (`file.txt:a:b`) is used
- **THEN** the platform rejects it before it reaches the filesystem
