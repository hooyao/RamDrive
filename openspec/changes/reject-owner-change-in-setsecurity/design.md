# Design: reject owner-change in SetFileSecurity

## Context

`SetFileSecurity` blindly merges any caller SD and always returns `STATUS_SUCCESS`. A non-privileged
caller can change a node's owner to an arbitrary SID. On real NTFS this is refused
(`ERROR_INVALID_OWNER`), and the refusal is atomic so the bundled DACL change is also dropped — which
keeps the object deletable by its creator. RamDrive's acceptance of the owner change is the root
cause of dotTrace's leaked, un-deletable `jetbrainsproc_<GUID>` temp dirs.

## Constraint: no caller token at SetSecurity

Verified against the winfsp source (`F:\MyProjects\winfsp`):

- `inc/winfsp/fsctl.h`: `Req.Create` carries `UINT64 AccessToken`; `Req.SetSecurity` does **not**.
- `src/sys/security.c` `FspFsvolSetSecurity`: packages the SD and posts it to user mode with no
  owner/AccessCheck.
- `src/dll/security.c` `FspSetSecurityDescriptor`: merges via `SetPrivateObjectSecurity(..., Token=0)`
  — token hardcoded NULL, so owner enforcement is skipped.
- `src/dll/fsop.c` `FspFileSystemOpEnter`: only takes the OpGuard lock; does **not** impersonate. So
  `OpenThreadToken` during the op would not yield the caller either.

Only a passthrough FS over a real handle (ntptfs `NtSetSecurityObject`) can produce the true NTFS
status. A pure in-memory FS cannot — so we approximate. (Recorded in memory
`winfsp-setsecurity-no-token`.) This is a structural limitation of winfsp's in-memory FS model, not a
winfsp bug; the official `memfs` has the same gap.

## Decision: reject any owner *change*

If `OWNER_SECURITY_INFORMATION` is set and the new owner SID is non-null and differs from the current
owner SID → return `STATUS_INVALID_OWNER` and apply nothing. Rationale: the NTFS-observable outcome we
must preserve is "creator keeps ownership → object stays deletable/recoverable". We cannot check
*whether the caller holds the SID*, but we can refuse to *move ownership at all*, which is a strict
superset of NTFS's refusals for the non-privileged case and never silently corrupts an object's
recoverability. The freshly-created node's owner is the creating principal (WinFsp kernel
`FspCreateSecurityDescriptor` computes it from the create-time token), so "owner unchanged" means
"still the creator".

### Edge cases (all match the NTFS oracle, all tested)

- **OWNER bit clear** → never reject; owner untouched (prior behaviour preserved).
- **New owner SID == current** → idempotent no-op, allowed. Many real callers resubmit OWNER info
  carrying the unchanged owner; these must not spuriously fail.
- **OWNER bit set but modification owner SID == null** (owner-clear) → allowed. Not the dotTrace
  vector; NTFS treats null-owner oddly. Kept permissive to avoid gratuitous divergence. If a future
  NTFS-oracle experiment shows it should be refused, tighten both copies together.
- **Current SD null** (defensive; the no-null-SD structural invariant should prevent this) → callers
  pass `node SD ?? root SD`, so the "current owner" is the root owner (`BA`). Setting owner to `BA` is
  then a permitted no-op; any other owner is correctly rejected. Strictly more consistent than the
  pre-fix code, which built the merge base from root SDDL on the null path but never compared owners.

## Decision: controlled duplication, not a shared project

`RamDrive.Diagnostics.MemfsReference` deliberately does not reference `RamDrive.Core` (it is an
isolated "canonical correct" oracle). A Core-side shared helper therefore cannot be called from
memfs, and adding a project reference would couple the oracle to production code for ~15 lines. We
mirror the existing precedent (`WinFspRamAdapter.RootSddl` vs `MemfsReferenceFs.DefaultRootSddl` —
already duplicated) and put a tiny `private static bool IsRejectedOwnerChange(...)` plus the
`OWNER_SECURITY_INFORMATION` / `STATUS_INVALID_OWNER` consts in each class, each carrying a
`// LOCKSTEP` comment pointing at the other.

## Lockstep contract with the differential checker

`DifferentialAdapter.SetFileSecurity` calls production then oracle and passes both statuses to
`Comparators.CompareStatus("SetFileSecurity")`, which throws on any divergence. Both adapters must
return the same status for the same input. Because `ChaosTests` never issues SetSecurity, the
fuzzer would not catch a divergence here — so a dedicated unit test
(`MemfsReference_OwnerChange_ReturnsSameStatus_AsProduction`) pins it directly.

## STATUS_INVALID_OWNER constant

`WinFsp.Native`'s `NtStatus` (a compiled package type) does not define it. Defined locally as
`unchecked((int)0xC000005A)` in each adapter; value cross-checked against winfsp's own .NET binding
(`src/dotnet/FileSystemBase+Const.cs:319`).

## Atomicity

The guard runs **before** any mutation and returns early, so on rejection the node's
`SecurityDescriptor` is never touched — the DACL/GROUP/SACL carried in the same request are not
applied. The unit test asserts byte-equality of the SD before/after a rejected call.
