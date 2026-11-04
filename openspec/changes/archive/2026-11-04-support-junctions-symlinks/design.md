## Context

WinFsp.Native v0.1.3-pre.1 forwards handle-based reparse operations but leaves the resolve callback unset. RamDrive has no reparse data and its namespace lookup does not report intermediate link components. Returning an NTFS volume name does not implement these operations.

## Decisions

- Extend the current WinFsp.Native mainline with a synchronous by-name reparse query and wire the native `FspFileSystemFindReparsePoint` and `FspFileSystemResolveReparsePoints` helpers. A failed name lookup scans for an intermediate link; the host returns the native path-component index to WinFsp. Native resolution returns `STATUS_REPARSE` to the I/O manager, which owns relative/absolute paths, external targets, and loop detection. Do not follow links privately in RamDrive.
- Windows' mount-point handler cannot follow a UNC junction substitute. For resolution only, synthesize an equivalent symbolic-link buffer; direct queries retain the original tag and data. An absolute junction or symbolic link targeting this UNC or drive-letter volume is made volume-relative so the native helper can detect cycles; other UNC targets remain absolute and Windows' remote symlink-evaluation policy applies.
- Preserve UTF-16 path code units when synthesizing resolution data, including unpaired surrogates. For a symbolic-link target followed by another path component, the binding trims target-ending separators in its temporary by-name response; this prevents WinFsp's native splice from producing a double separator while leaving handle-based queries unchanged.
- Validate third-party WinFsp.Native reparse buffers using the GUID header length rather than the Microsoft-tag header length.
- Add an opt-in prefix-only mount to WinFsp.Native and use it in both projects' tests. Passing a null mount point to WinFsp normally auto-assigns a drive letter, even when a UNC prefix is present; omitting the native mount-point call avoids drive-letter collisions and zombie mounts while retaining the existing default behavior for consumers.
- Store immutable reparse buffers on nodes and serialize link mutations and namespace operations with the existing structure lock. Copy each caller buffer before validation so the bytes checked and stored are identical even if the caller mutates its input. For files, acquire the content lock inside the structure lock through the empty check and metadata publication. Report the tag and reparse attribute consistently. Validate accepted mount-point and symlink buffers, existing tag replacement/deletion, and empty directories/files. Expose linked-node by-name lookup as well as handle-based access so dangling links remain manageable.
- Notify WinFsp after metadata changes and keep the standard kernel reparse path. Integration tests use the permanent metadata cache and UNC mounts, including operations on links and their targets.
- Reference the published WinFsp.Native NuGet package through centrally managed package versions, so clean builds and CI do not depend on a sibling checkout, a local feed, or a vendored source snapshot.
- The empty-file check acquires the existing file content lock but does not mutate pool accounting or the page table, and it does not change the three-phase write protocol; the existing TLA+ page/pool model is unaffected. Reparse metadata is protected by the tree lock.

## Risks and verification

Path handling, user/kernel ABI layouts, access checks, non-empty directory races, tag validation, notifications and cache aliasing require real mounted tests. Compare Windows error mapping with NTFS and test malformed input, direct link handles, relative links, nested/cyclic paths, external volumes and rename/delete semantics. Only advertise the junction and symlink tags; cloud placeholder semantics are out of scope.
