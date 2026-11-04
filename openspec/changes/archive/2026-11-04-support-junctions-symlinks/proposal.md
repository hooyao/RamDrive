## Why

RamDrive cannot host junctions or symbolic links, breaking developer tools and applications that create or traverse them. Windows must resolve links through the normal reparse mechanism so target security, cache identity, and cross-volume routing remain correct.

## What Changes

- Store, query, replace and delete junction and file/directory symbolic-link reparse data and report their attributes and tags.
- Resolve intermediate and final link components through WinFsp and the Windows I/O manager, including relative, absolute, cross-volume, dangling and cyclic links.
- Extend WinFsp.Native's high-level host with by-name queries and native reparse resolution, preserving its AOT-compatible ABI.
- Add unit and real UNC-mounted integration tests and document the supported link types and dependency integration.

## Capabilities

### New Capabilities

- `windows-reparse-links`: Windows-compatible junction and symbolic-link storage, metadata, traversal and errors.

### Modified Capabilities

None.

## Impact

RamDrive core, its diagnostics and integration tests, and WinFsp.Native host/interoperability code change. RamDrive references the published updated binding package. Reparse metadata does not change the page-pool allocation protocol.
