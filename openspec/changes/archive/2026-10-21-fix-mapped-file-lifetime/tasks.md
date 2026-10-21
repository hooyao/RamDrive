## 1. Ownership implementation

- [x] 1.1 Add structure-lock-protected create/open references, identity unlink, retained-node final release and deterministic shutdown.
- [x] 1.2 Update adapter create/open/cleanup/close ownership and unexpected-exception logging; preserve replacement and case-only rename identity.

## 2. Regression coverage

- [x] 2.1 Add core/callback regressions for late paging writes, multiple contexts, stale cleanup, rename replacement, open/delete races and shutdown.
- [x] 2.2 Add mounted native mapping regressions for file-handle-before-view close, name reuse and supported cached-destination replacement.

## 3. Formal verification

- [x] 3.1 Extend RamDiskSystem with linked/live/open-reference states and a bounded lifecycle configuration.
- [x] 3.2 Add focused namespace/mapping/last-close model and a buggy eager-disposal counterexample configuration.
- [x] 3.3 Document lifetime/action mappings, invariants, liveness assumptions and verification commands/results.

## 4. Acceptance and review

- [x] 4.1 Build and run core/integration suites, including the differential checker cases.
- [x] 4.2 Run bounded TLC fixed configurations and confirm the old-behavior configuration fails the intended invariant; record incomplete larger runs separately.
- [x] 4.3 Repeat native-client and real-drive VS acceptance with EnableKernelCache on/off; save evidence and clean all mounts.
- [x] 4.4 Validate OpenSpec and verify implementation/scenario coverage against the completed change artifacts.

## 5. PR review follow-ups

- [x] 5.1 Review Copilot's callback-ordering concern against pinned WinFsp/WinFsp.Native source and document the external contract, dispatcher shutdown and regression evidence in `tla/FILE_OBJECT_LIFETIME.md`.
