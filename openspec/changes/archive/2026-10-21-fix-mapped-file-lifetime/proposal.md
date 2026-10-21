## Why

Issue #24 reproduces when a delete-on-close file still has writable memory mappings: Cleanup deletes and disposes its PagedFileContent before Windows finishes paging I/O, so late writes throw ObjectDisposedException and leave Modified pages behind. Cleanup must unlink the name while the backing object survives until its final open context closes; the current TLA+ model does not represent this intermediate state.

## What Changes

- Acquire file-object references atomically with namespace create/open, retain unlinked or replaced nodes while references exist, and release content only after the final reference closes.
- Unlink Cleanup targets by node identity, preserving newly created files that reuse an old name and existing contexts across rename/replace.
- Reclaim linked and retained unlinked nodes deterministically at filesystem shutdown and log unexpected callback exceptions.
- Add core and mounted mapping regressions, plus a real-drive Visual Studio acceptance run using the already authorized drive-occupancy checks.
- Extend the full-system TLA+ memory model with link/open/last-close lifetime states; add a focused namespace/mapping model with a counterexample configuration for the old eager-disposal behavior.

## Capabilities

### New Capabilities

- `file-object-lifetime`: namespace deletion and replacement preserve backing content, file identity, and paging I/O until the final open-context reference is released; formal models cover the same lifetime boundary.

### Modified Capabilities

None. FileInfoTimeout and FlushAndPurgeOnCleanup settings keep their existing behavior.

## Impact

- Core: FileNode, RamFileSystem and WinFspRamAdapter ownership/lifetime paths; synchronous Read/Write retain their existing allocation and locking behavior.
- Tests: callback-level ownership/concurrency tests and native mapped-file integration regressions.
- Formal verification: RamDiskSystem model/configuration, a focused lifetime model and verification documentation.
- No new runtime dependency or default cache-policy change. Native pages of an unlinked file remain charged to the pool while the kernel still owns an open context, and are returned on final Close or filesystem shutdown.
