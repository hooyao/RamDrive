# Verification: fix-mapped-file-lifetime

2026-10-21, Windows 11 build 26300, WinFsp 2.1.25156.ddca7bd,
Visual Studio Enterprise 2026 18.10.4. Local commands selected .NET SDK 10.0.402
from the ignored diagnostic directory because the repository's pinned 10.0.103
SDK is not installed; global.json was not changed.

## Summary

| Dimension | Result |
| --- | --- |
| Completeness | All 11 implementation/verification tasks completed |
| Correctness | All 6 requirements mapped to implementation and regression/formal evidence below |
| Coherence | Structure-lock ownership, identity unlink and last-reference disposal follow design; no default cache-policy change |

## Implementation and scenario coverage

| Requirement | Implementation | Evidence |
| --- | --- | --- |
| Atomic open-context ownership | Create-with-reference and OpenNode obtain references under RamFileSystem's structure lock | Open/delete race; multi-context close-order core tests; existing create/open integration suite |
| Unlink and delayed paging I/O | Cleanup unlinks the context's exact node; retained data and locks remain valid | Late paging-write/metadata/flush core test; native mapped integration tests; real-drive acceptance |
| Identity under name reuse/replacement | Unlink checks identity; Move releases only the replaced target's namespace ownership and handles self/case rename | Stale cleanup, retained destination and case-only rename core tests; mapped name-reuse and cached-destination integration tests |
| Final release and shutdown | CloseNode disposes only an unlinked node's last reference; retained set and idempotent disposal cover host shutdown | Native page/reservation counts after final close; repeated Close/shutdown test |
| Observable callback errors | ExceptionHandler logs and returns STATUS_UNEXPECTED_IO_ERROR | Recording-logger core test; production acceptance logger found no callback error |
| Formal lifecycle coverage | Linked/alive/open-reference memory states plus separate names/context/mapping model | Two completed positive configurations and required negative counterexample |

The WinFsp kernel rejects replacement of a still-user-open destination in the
current mount configuration before calling the adapter. Callback tests verify
that such retained destination contexts are safe when replacement is allowed;
the mounted replacement test uses a cached, closed target. No rename flags or
access-policy behavior were broadened.

## Build and tests

- `dotnet build RamDrive.slnx -c Release`: passed. Existing benchmark platform
  warnings remain; no build error.
- Core suite: **65 passed**, including **9 new lifetime regressions**.
- Full integration suite: **51 passed**, including **3 new mapped/namespace regressions**.
- Differential integration suite, excluding ChaosTests as in CI: **50 passed**.
- Integration fixtures reset to independent UNC volumes before each test instance,
  retaining the 512 MiB capacity and in-test pressure assertions. This prevents
  legitimate kernel-held contexts from consuming subsequent tests' capacity.
- `git diff --check`: passed.
- OpenSpec strict validation: passed.
- CI workflow YAML and formal-change filters parsed successfully. The new model
  job uses the same PowerShell verification script and uploads logs/results.

## Completed model checks

Executed `tla/Verify-Lifetime.ps1` using Microsoft JDK 11.0.32.1 and TLC JAR
downloaded from the pinned v1.8.0 release URL. Repeated the script with the CI
PowerShell exit-code forwarding check: the expected negative run does not make
the overall script fail.

| Configuration | Exact bound | Completed result |
| --- | --- | --- |
| FileObjectLifetime_Minimal | 2 names, 3 object identities, 2 contexts | **270,403 distinct states**, safety and liveness passed |
| RamDiskSystem_Lifecycle | 1 shared page, 2 slots, up to 2 open references per slot | **228,204 distinct states**, all configured invariants and WriteTerminates passed |
| FileObjectLifetime_BugRepro | 1 name/object/context; dispose-at-unlink enabled | Expected exit **12**, **PagingWritesSucceed** violation; trace includes Cleanup destroying an object still held by a dirty mapping, then failing paging write |

Two larger exploratory runs were **stopped incomplete and are not counted as
passes**: the existing 3-page/2-slot Minimal run reached about 9.38 million
distinct states; the 2-page/2-slot/2-reference lifecycle exploration reached about
8.18 million. Their partial logs/status were preserved locally. Standard,
Medium, Stress and the larger lifecycle configuration have not received complete
new exhaustive verification in this change. Bounds and fairness assumptions are
documented in `tla/FILE_OBJECT_LIFETIME.md`.

## Real-drive acceptance

The user explicitly authorized real drive letters, checked for occupancy from R:.
The diagnostic host checks logical drives, DOS-device aliases and persistent
network mappings, then rechecks immediately before mounting. R: was free for the
first group; the second group skipped occupied R: and used free S:.

Both groups use the **production adapter**, 8 GiB capacity, 64 KiB pages and
FlushAndPurgeOnCleanup=false. Fresh identical C++ projects contain 8 source files,
12,000 generated template types per file. Actual VS/vcpkgsrv runs opened all
files, held for 180 seconds and exited through DTE.Quit. TEMP/TMP were changed
only for those processes. The mapped-file native reproducer was also executed.

| EnableKernelCache | Drive | Actual FileInfoTimeout | Earlier post-exit snapshot | Snapshot started 120 s after all VS processes exited |
| --- | --- | ---: | ---: | ---: |
| true | R: | 1000 ms | 884.17 MiB Modified while writeback was pending | **0 MiB Modified** |
| false | S: | 0 ms | 0 MiB Modified | **0 MiB Modified** |

Both production exception logs were absent: no unexpected callback exception was
recorded. This removes the persistent 1850.46 / 1893.33 MiB Modified residues of
the pre-fix comparison. The amount parsed is filtered to each group's drive and
edg files, not system-wide Modified counters or RAMMap's own snapshot writes.
Snapshot timestamps denote RAMMap start; serialization takes additional time.

Clean cached file-object references can still delay final Close and retain native
backing capacity. A zero Modified result is not a claim of immediate zero process
RSS or immediate release of every cached native page. Shutdown and final-release
reclamation are covered separately. This change preserves that lifetime contract
and does not force mapped data loss through a new cache default.

Raw snapshots, callback/handle/process records and machine-readable acceptance
results are local under `artifacts/issue24/run-fixed-cache-{on,off}-20261021/`,
`fixed-acceptance-20261021.json` and `fix-verification/`. All test mounts and
processes were cleaned up.

## Review assessment

No missing requirement, incomplete implementation task or design contradiction
found. The documented limits are bounded formal verification and ordinary clean
kernel-cache retention; no incomplete large run is presented as a pass. Change
was ready for review/archive at the end of local verification. The completed
change was subsequently synced to `openspec/specs/file-object-lifetime/spec.md`
and archived before opening the PR, so the specification and implementation are
reviewed together. GitHub review and release results are recorded in the PR.

Copilot round 1 reported no concrete defect but requested closer review of the
Windows callback-ordering dependency. Source review confirmed the Cleanup/Close
contract, binding handle-state release and dispatcher joins; pinned source links
and the limits of the model assumptions are recorded in
`tla/FILE_OBJECT_LIFETIME.md`. No runtime change was needed for that concern.
