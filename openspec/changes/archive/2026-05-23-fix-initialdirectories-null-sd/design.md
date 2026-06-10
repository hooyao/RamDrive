## Context

`RamFileSystem` (in `src/RamDrive.Core/FileSystem/RamFileSystem.cs`) is a pure in-memory directory tree that does not understand security descriptors as a concept — they are opaque `byte[]` blobs stored on `FileNode`. Population happens through three callsites:

1. **WinFsp callback** (`WinFspRamAdapter.CreateFile` → `RamFileSystem.CreateFile`/`CreateDirectory`): WinFsp's kernel computes the new SD by walking the parent's DACL (via `FspCreateSecurityDescriptor`) using the OICI inheritance flags fixed in commit `e5bc2e1` / spec `default-security-descriptor`. The resulting bytes are passed to our `CreateDirectory(path, sd)` overload's second parameter and stored on the node.
2. **Bootstrap callsite — `WinFspHostedService.CreateDirectoriesRecursive`** (in `src/RamDrive.Cli`): runs after mount to materialise `appsettings.jsonc`'s `InitialDirectories` configuration. Calls `_fs.CreateDirectory(path)` — the **single-argument overload**, leaving the optional `securityDescriptor` parameter at its default of `null`. So `FileNode.SecurityDescriptor` is stored as `null`.
3. **Bootstrap callsite — `DiagHostedService.CreateRecursive`** (in `src/RamDrive.Cli.Diag`, added recently for this debug session): identical pattern.

Trace evidence captured in `F:\MyProjects\RamDrive\diag-trace.log` shows the exact failure chain for path `\Temp`:
- Line 2: `InitialDir-Create \Temp | sd=NULL (no SD passed)`
- Lines 3–223: every `GetFileSecurityByName \Temp` returns `sd=NULL`. No `SetFileSecurity` call ever appears. Four user processes (Chrome, Edge, VS Code, Claude Code) interact with `\Temp` during the trace window; none mutate its SD.

The visible user-mode symptom is `Get-Acl R:\Temp` / `icacls R:\Temp` returning `error 1338 ERROR_INVALID_SECURITY_DESCR`, and MSIX AppContainer apps (Windows365.exe) failing to launch when their `TEMP` is set to such a directory — observed via `Microsoft-Windows-AppModel-Runtime/Admin` repeatedly logging Create-process / Destroy-container pairs at sub-minute intervals.

Constraints:
- **Hot-path zero-alloc**: `RamFileSystem.CreateDirectory`/`CreateFile` are on the WinFsp callback path. The fix must not allocate or copy SD bytes.
- **TLA+ model**: `RamDiskSystem.tla` abstracts SD bytes away. The fix sits below the model's abstraction boundary; the model does not need to change.
- **Backwards behaviour**: every existing test passes today with `sd == null` allowed. Changing the "null is stored as null" semantics could theoretically affect tests that assert `node.SecurityDescriptor == null` — none exist (verified by grep).

Stakeholders: end users who set `InitialDirectories` and configure `%TEMP%` to point inside the RAM disk (the most common motivating use case — `RamDrive` exists precisely so apps can do this).

## Goals / Non-Goals

**Goals:**
- Make "no `FileNode` ever has `SecurityDescriptor == null`" a structural invariant of `RamFileSystem`, enforced where nodes are inserted into the tree.
- Defence-in-depth at the read boundary: `WinFspRamAdapter.GetFileSecurityByName` and `GetFileSecurity` MUST NOT return `(NtStatus.Success, null)`.
- Ship a regression test that boots the mount with `InitialDirectories={ "Temp": {} }` and verifies `Get-Acl`-style SD retrieval returns a valid self-relative SD.
- Keep the AOT/zero-alloc guarantees of the hot path.

**Non-Goals:**
- Adding any new public API on `RamFileSystem` (e.g. a `GetRootSecurityDescriptor()` accessor — not needed for the chosen design).
- Changing the canonical root SDDL constant.
- Re-running TLA+ verification — SD bytes are below the abstraction line.
- Per-principal SDs at create time (we still inherit a single byte[] reference from parent; we never compute a fresh SD from user identity).
- Addressing distinct AppContainer-specific ACE coverage gaps. The root SD already grants `FullControl` to `Everyone (WD)`, which an AppContainer token's group SIDs include via the `Everyone` membership granted by the LSA. If a future requirement demands distinct AppContainer ACEs, that's a separate change.

## Decisions

### Decision 1: Inherit at create time (in `RamFileSystem`), not at read time (in `WinFspRamAdapter`)

**Choice**: When `RamFileSystem.CreateFile(path, sd)` / `CreateDirectory(path, sd)` is invoked with `sd == null`, assign `node.SecurityDescriptor = parent.SecurityDescriptor` (a reference copy of the same byte[]). Do not allocate.

**Alternatives considered:**

- **(A) Read-time fallback only** — leave `node.SecurityDescriptor = null`, but make `WinFspRamAdapter.GetFileSecurityByName` substitute root SD when it encounters null. *Rejected*: leaks "null SD" as a representable state through the entire tree. Subdirectories created underneath `\Temp` would inherit (via WinFsp kernel `FspCreateSecurityDescriptor`) from a kernel-side computation seeded by our adapter's substituted root SD — which works, but the persisted node state is still null, so any new code path reading `node.SecurityDescriptor` directly (e.g. our own unit tests, future audit code) sees the corruption. Read-time fallback alone fixes the symptom while preserving the bug.

- **(B) Force every callsite to pass an SD explicitly** — make the SD parameter required, update `WinFspHostedService`/`DiagHostedService` to pass `_fs.GetRootSecurityDescriptor()`. *Rejected*: requires a new public API on `RamFileSystem`, and is exactly the kind of "easy to forget" interface that produced this bug. The "no null SD nodes" invariant should be enforced by the data structure, not by convention.

- **(C) Compute fresh SD per node at create time from scratch** (e.g. always re-parse the root SDDL and apply OICI inheritance manually in user mode). *Rejected*: duplicates the work WinFsp kernel does in `FspCreateSecurityDescriptor`, adds bytes to copy on the hot path, and our SDDL parsing already happens once at adapter construction.

**Choice rationale**: Option 1 (inherit by reference) is O(1), allocates nothing (shares the byte[] reference with parent), and makes the invariant structural. The same code path covers all three callsites: WinFsp callback that did pass an SD continues to use the explicit SD; bootstrap callsites that pass null now inherit. New future callers can't accidentally create a null-SD node — the code path that writes the field defends the invariant.

### Decision 2: Apply both belts and suspenders

**Choice**: Apply both Decision 1 (write-time inheritance) AND a read-time defensive fallback in `WinFspRamAdapter.GetFileSecurityByName`/`GetFileSecurity`. If a node's SD is `null` at read time (post-fix this should be unreachable), substitute the root SD before returning success.

**Rationale**: The cost of the read-time check is one null comparison per security read. The benefit: any future regression that re-introduces a null-SD node fails closed at the user-mode boundary instead of failing open into the kernel access check. The `default-security-descriptor` spec is now safety-critical (AppContainer integration depends on it), so we want layered defence.

**Alternative considered**: Decision 1 alone. Rejected because regressions in `RamFileSystem` mutators are easy to introduce and hard to test exhaustively — the read-time check costs nothing and catches them.

### Decision 3: No change to `RamFileSystem.SetRootSecurityDescriptor` semantics

**Choice**: The existing `SetRootSecurityDescriptor(byte[])` API stays; it is called exactly once from `WinFspRamAdapter` constructor before any child is created. The invariant "root SD is non-null before any other node is created" is preserved by construction order, not by adding null-checks.

**Rationale**: Adding "root SD must be non-null" enforcement adds throw paths and complicates the existing simple setter. The construction order in `WinFspRamAdapter` ctor (`SetRootSecurityDescriptor` → DI registers `RamFileSystem` → host service starts → `InitialDirectories` creation) makes this a non-issue. A unit test pins the invariant.

### Decision 4: Skip TLA+ updates

**Choice**: Do not modify `tla/RamDiskSystem.tla`.

**Rationale**: The model treats SD bytes as outside its abstraction (it tracks page data tags `{0, 'f'}`, not SD content). The new invariant is about node-field bookkeeping, not concurrency. Adding it would not catch new bugs the model is designed to catch (pool/page consistency, write linearisability, deadlock).

## Risks / Trade-offs

- **[Risk]** `parent.SecurityDescriptor` could theoretically be null if a future bug skips root SD initialisation. → **Mitigation**: a unit test asserts that after `new RamFileSystem(pool).SetRootSecurityDescriptor(bytes)`, all subsequently created nodes (with or without explicit SD) have non-null SD. A second test asserts root SD non-null after `WinFspRamAdapter` ctor completes.

- **[Risk]** Shared byte[] reference between parent and child means a mutation to one is visible to the other. → **Mitigation**: nothing in the codebase mutates SD byte arrays in place. `SetFileSecurity` already allocates a fresh byte[] (`var result = new byte[existing.BinaryLength]; existing.GetBinaryForm(...)`) and assigns it to `node.SecurityDescriptor`, breaking sharing on first explicit change. This is the standard copy-on-write pattern; document with a one-line comment at the assignment.

- **[Risk]** Defensive read-time fallback masks future bugs that re-introduce null SDs — symptoms hidden, root cause harder to find. → **Mitigation**: when the read-time fallback fires, emit one log entry at `Warning` level (rate-limited or one-shot per node) so regressions are noisy in production logs. Acceptable trade-off vs. user-facing AppContainer crashes.

- **[Risk]** Integration test of the full MSIX AppContainer launch is environment-sensitive (`Windows365.exe` may or may not be installed on CI runners). → **Mitigation**: the AppContainer scenario stays as a manual acceptance check in the spec. The automatable surrogate is: open the directory via `Get-Acl` from a process running with a restricted token (`STARTUPINFOEX` with `CreateRestrictedToken`); that's reproducible on any Windows runner with admin and exercises the same kernel access check path.

- **[Trade-off]** Sharing root SD bytes by reference means every node in a freshly-mounted `InitialDirectories`-only tree points at the same array. Reading SD via `GetFileSecurityByName` is therefore O(1) and zero-alloc, which is desired. Cost is one extra reference assignment per CreateFile/CreateDirectory call — sub-nanosecond, irrelevant on any benchmark.

## Migration Plan

No data migration. Behavioural change is "more permissive" — directories that previously had null SD now have a valid one. Rollback strategy: revert the commit; existing data (if any in-memory state) is lost on unmount anyway, so no on-disk schema concern.

Deployment order:
1. Land the change with unit tests.
2. Run integration test suite (especially the new restricted-token reopen test).
3. Run chaos fuzzer for 60s to ensure no concurrency regressions in `CreateDirectory`/`CreateFile` paths.
4. Bump version, publish installer; users with `InitialDirectories` set get the fix automatically on next mount.

## Open Questions

- Should the defensive read-time fallback in `GetFileSecurityByName` log at `Warning` every time, or only the first time per process lifetime? — Lean towards once-per-node (cheap, won't spam logs in regression scenarios).
- Should we also harden `RamFileSystem.SetRootSecurityDescriptor` to reject null? — Defer; current callsite is single and well-behaved. Worth a separate hygiene PR if we ever expose `RamFileSystem` as a library.
