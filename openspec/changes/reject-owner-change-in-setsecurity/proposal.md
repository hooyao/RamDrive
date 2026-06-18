## Why

JetBrains dotTrace fails to start profiling when `%TEMP%` points at the RamDrive, with
`copy: file exists ... jetbrainsproc_<GUID> (generic:17)` (C++ `std::errc::file_exists`). Root cause:
`WinFspRamAdapter.SetFileSecurity` blindly merges any caller-supplied security descriptor and
**always returns `STATUS_SUCCESS`** — it never validates owner assignment. dotTrace's ETW collector
creates `jetbrainsproc_<GUID>` temp directories and applies an SD that **changes the owner away from
the creator**. On real NTFS that owner reassignment is refused with `ERROR_INVALID_OWNER` (1307,
`STATUS_INVALID_OWNER` 0xC000005A) because the caller lacks `SeRestore`/`SeTakeOwnership`, and the
whole SetSecurity fails **atomically** — so the directory keeps its creator-owner and stays
deletable. On RamDrive the owner-change succeeds, the creator loses the owner-implied
`READ_CONTROL`/`WRITE_DAC`, the directory becomes un-deletable and its ACL un-readable, it leaks, and
the next run's `std::filesystem::copy_file(collector.exe, jetbrainsproc_<GUID>)` hits `EEXIST`.
(Confirmed live: three leaked `jetbrainsproc_*` dirs under `Z:\Temp`, two with `Get-Acl` →
ACCESS_DENIED, all undeletable by their creator.)

This is **not** a RamDrive-specific bug: winfsp's official `memfs` uses `FspSetSecurityDescriptor` →
`SetPrivateObjectSecurity(..., Token=0)`, which also skips owner enforcement. RamDrive's differential
oracle (`MemfsReferenceFs`) reproduces the identical blind merge, so the differential checker
compared two equally-wrong implementations and stayed green — the real oracle is **real NTFS**.
WinFsp structurally cannot pass the caller token to the SetSecurity callback (the kernel
`Req.SetSecurity` request has no `AccessToken` field, unlike `Req.Create`; and `FspFileSystemOpEnter`
does not impersonate), so a true privilege check is impossible. The pragmatic, correct-enough fix is
to reject any owner **change**.

## What Changes

- `WinFspRamAdapter.SetFileSecurity`: when the request carries `OWNER_SECURITY_INFORMATION` and the
  modification's owner SID is non-null and differs from the node's current owner SID, return
  `STATUS_INVALID_OWNER` (0xC000005A) and apply **nothing** — neither owner, group, nor the DACL/SACL
  in the same request (atomic failure, matching NTFS). When the owner is unchanged (bit clear, same
  SID, or no SID supplied), behave exactly as before.
- `MemfsReferenceFs.SetFileSecurity`: implement the **identical** rule (lockstep), so
  `DifferentialAdapter` — which compares the two NTSTATUS results via
  `Comparators.CompareStatus("SetFileSecurity")` — sees no divergence. The shared rule is a tiny
  private static helper duplicated in both classes (the two projects are deliberately decoupled;
  `MemfsReference` does not reference `Core`), mirroring the existing `RootSddl`/`DefaultRootSddl`
  duplication.
- `STATUS_INVALID_OWNER` constant: defined locally in each adapter (WinFsp.Native's `NtStatus` does
  not include it). Value confirmed by winfsp's own .NET binding (`FileSystemBase+Const.cs:319`).
- New unit tests (`tests/RamDrive.Core.Tests/WinFspRamAdapterSetSecurityTests.cs`): owner-change
  rejected + atomic, DACL-only change succeeds, same-owner re-apply succeeds, nonexistent node, and a
  production-vs-oracle lockstep status-match test (ChaosTests does not exercise SetSecurity, so the
  differential fuzzer alone would not catch a divergence).
- New integration test (`tests/RamDrive.IntegrationTests/OwnerChangeRejectionTests.cs`): on the UNC
  mount, a non-privileged owner-change attempt via `SetKernelObjectSecurity` is refused with
  `ERROR_INVALID_OWNER`, the directory stays deletable and its ACL readable; a DACL-only change still
  succeeds.

## Capabilities

### New Capabilities

- `security-owner-enforcement`: one requirement — `SetFileSecurity` SHALL reject owner reassignment
  by a non-privileged caller, atomically, with `STATUS_INVALID_OWNER`, and the same rule SHALL be
  implemented by the differential oracle. Four scenarios: owner-change-to-different-SID refused
  atomically; DACL-only change succeeds; same-owner re-apply is a permitted no-op; mounted directory
  stays deletable + ACL readable after a refused owner change (the dotTrace regression).

### Modified Capabilities

None. (`default-security-descriptor` owns creation-time SD inheritance and reads; owner-reassignment
enforcement on `SetFileSecurity` is a distinct mutation/authorization guarantee, so it gets its own
capability.)

## Impact

- **Code**: `src/RamDrive.Core/FileSystem/WinFspRamAdapter.cs` (SetFileSecurity owner-change guard +
  consts/helper); `src/RamDrive.Diagnostics.MemfsReference/MemfsReferenceFs.cs` (identical lockstep
  rule + cached root SD bytes). `DifferentialAdapter`/`Comparators` unchanged — they enforce the
  lockstep contract.
- **Tests**: new unit-test file in `tests/RamDrive.Core.Tests`; new integration-test file in
  `tests/RamDrive.IntegrationTests` (UNC mount via the existing `RamDrive` collection fixture — never
  a drive letter).
- **Specs**: new capability `openspec/specs/security-owner-enforcement/spec.md` (created on archive).
- **Behavioural impact for users**: JetBrains dotTrace (and any tool that locks its temp dirs by
  reassigning owner) works with `%TEMP%` on the RamDrive; refused owner changes no longer leak
  un-deletable directories. No breaking change to existing callers — re-applying the unchanged owner,
  group-only, and DACL/SACL-only changes are unaffected.
- **TLA+ model**: no change required. The model abstracts away SD bytes; owner SID identity is below
  the abstraction line.
- **Performance**: one owner-SID comparison on the cold `SetFileSecurity` path. No hot-path
  (Read/Write) impact; zero managed-heap allocation on Read/Write unchanged.
