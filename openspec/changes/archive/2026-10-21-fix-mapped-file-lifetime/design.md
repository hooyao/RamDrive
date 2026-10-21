## Context

The real-drive VS reproduction leaves about 1.8 GiB of Modified pages after process exit. A native mapping client isolates the failure: Cleanup(Delete) calls RamFileSystem.Delete, which disposes content and its ReaderWriterLockSlim; subsequent constrained paging writes throw ObjectDisposedException. A diagnostic unlink/reference-count implementation allows the same late writes to complete and removes the Modified residue. FlushAndPurgeOnCleanup alone did not fix the real workload or the native reproducer.

## Goals / Non-Goals

**Goals:**
- Keep backing content alive for every adapter context until Close, independently of pathname visibility.
- Acquire and release ownership atomically with namespace changes, including replacement and same-name reuse.
- Preserve existing page-pool accounting, data integrity and synchronous allocation-free Read/Write callbacks.
- Check lifecycle safety alongside the existing memory protocol and demonstrate the old behavior with a TLC counterexample.

**Non-Goals:**
- Change Windows cache defaults, introduce forced purge, or claim all native bytes disappear immediately when a user process exits. Clean kernel cache references can legitimately delay Close.
- Change the independent MEMFS oracle's reference-count semantics or the page allocation protocol.

## Decisions

### Structure-lock ownership

RamFileSystem owns the lifecycle under its existing structure lock. Each node has namespace membership plus an open-context reference count. Create with an acquired reference and OpenNode obtain ownership before releasing that lock. The adapter stores the retained FileNode directly in FileOperationInfo.Context; no additional per-I/O allocation or lock is added to Read/Write. Independent raw FindNode is a namespace lookup, not an owning handle.

Delete removes a node's namespace ownership. Nodes with open references enter a retained-unlinked set, remain charged to the pool, and keep their content usable. Final Close removes the last open reference; an unlinked node is disposed exactly once at that point. Filesystem shutdown runs after the host stops callbacks and disposes the linked tree and retained-unlinked set deterministically. Node disposal and filesystem disposal are idempotent.

Keeping a reference count solely in the adapter was rejected: create/open would race with structural deletion, rename replacement would bypass ownership, and unlinked nodes would escape filesystem shutdown.

### Identity-based cleanup and replacement

Cleanup uses the FileNode identity from the context rather than looking up a potentially reused pathname. A second cleanup of an old unlinked node cannot delete a newly created node at that name. Rename replacement unlinks the destination object, retaining its existing contexts while the source acquires its name. Same-object/case-only rename preserves source identity and never releases its backing content.

The configured WinFsp kernel may reject replacement of a still-user-open destination before reaching the adapter. This change does not enable additional rename flags: callback-level tests cover retained destination contexts, and mounted tests cover the supported closed-handle/cached-destination path.

### Observability

The adapter records unexpected callback exceptions via its injected logger and returns STATUS_UNEXPECTED_IO_ERROR. Logging is confined to the failure path; valid paging I/O is not silently acknowledged or discarded.

### Formal model scope

RamDiskSystem distinguishes linked namespace entries, live backing objects and bounded open-context counts. Unlink can overlap write phases when a context retains the object; final Close can destroy it only after those phases complete. Existing configurations set the open-reference bound to zero to preserve the original exhaustive memory-protocol state space. A new lifecycle configuration enables retained references.

A focused FileObjectLifetime model tracks names separately from object identities, contexts, mappings, dirty pages, paging writes and final Close. It covers same-name recreation and rename replacement. Its fixed configuration checks safety and cache-drain/close liveness under fair completion of client and kernel operations. A buggy eager-disposal configuration must produce a failed paging-write counterexample. These models check abstractions of the implementation, not Windows internals or wall-clock cache reclamation deadlines.

## Risks / Trade-offs

- Retained clean kernel contexts keep native pages and capacity charged until Close → document this ownership rule, test final release and deterministic shutdown, and do not change cache policy in this fix.
- The shared integration volume previously relied on eager deletion returning capacity between tests → reset to a fresh UNC volume before each test instance, keeping the existing 512 MiB capacity and in-test pressure assertions. Mappings and cached contexts from one case cannot consume another case's budget.
- A time-only mixed workload can exceed the pool while kernel contexts retain deleted files; MEMFS enforces a per-file size limit rather than the same shared-pool limit → retain 100 workers and all operation types, but bound their iteration counts to at most 385 MiB of native pages even if every deleted object stays cached. Keep capacity-pressure and pool-limit tests separate; do not suppress differential status mismatches.
- Multi-context release or name reuse can dispose the wrong node → mutate counts and namespace membership under one lock and exercise close-order, replacement and stale-cleanup regressions.
- Expanded formal state space → retain the original bounded memory configurations and run a separate small lifecycle configuration plus the focused namespace model.
- UNC integration alone missed the original workload → use callback-level deterministic regressions, native mapped integration tests, and repeat the authorized real-drive VS acceptance with cache on/off.

## Migration Plan

Ship the ownership fix with unchanged configuration. Validate OpenSpec, build/unit/integration suites, TLC positive/negative configurations and real-drive acceptance before considering this change complete. Reverting the code restores the old behavior but reintroduces issue #24.

## Open Questions

None blocking implementation. Cache-pressure/early-purge policy is a separate follow-up, not part of this lifecycle fix.
