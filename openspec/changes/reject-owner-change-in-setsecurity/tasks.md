## 1. Core fix — owner-change rejection in `WinFspRamAdapter`

- [x] 1.1 Add `using System.Security.Principal;` to `src/RamDrive.Core/FileSystem/WinFspRamAdapter.cs`.
- [x] 1.2 Add consts `OWNER_SECURITY_INFORMATION = 0x1` and `STATUS_INVALID_OWNER = unchecked((int)0xC000005A)` and the `private static bool IsRejectedOwnerChange(uint securityInformation, byte[] currentSd, RawSecurityDescriptor modification)` helper (near `FallbackSdFor`), with a `// LOCKSTEP` comment pointing at `MemfsReferenceFs`.
- [x] 1.3 Rewrite `SetFileSecurity`: compute `currentSd = node.SecurityDescriptor ?? _rootSecurityDescriptorBytes`; if `IsRejectedOwnerChange(...)` return `STATUS_INVALID_OWNER` **before** any mutation (atomic); otherwise merge as before from `currentSd`. Add `FsTracer.Trace` points for both the reject and success paths.

## 2. Oracle lockstep — `MemfsReferenceFs`

- [x] 2.1 Add `using System.Security.Principal;` to `src/RamDrive.Diagnostics.MemfsReference/MemfsReferenceFs.cs`.
- [x] 2.2 Cache the root SD bytes once in the ctor (`_defaultRootSdBytes = sdBytes;`); add the field.
- [x] 2.3 Add the identical consts + `IsRejectedOwnerChange` helper (with a `// LOCKSTEP` comment) and rewrite `SetFileSecurity` to reject owner change with `STATUS_INVALID_OWNER` using `currentSd = n.FileSecurity ?? _defaultRootSdBytes`.

## 3. Unit tests — `tests/RamDrive.Core.Tests`

- [x] 3.1 New `WinFspRamAdapterSetSecurityTests.cs`. Setup mirrors `WinFspRamAdapterSecurityTests` (PagePool + RamFileSystem + adapter ctor); node via `_fs.CreateDirectory`, passed as `FileOperationInfo { Context = node }`; `Sd(sddl)` helper for self-relative bytes.
- [x] 3.2 `OwnerChangeToDifferentSid_ReturnsInvalidOwner_AndLeavesSdUnchanged` — owner WD≠BA + DACL, `OWNER|DACL` → `0xC000005A` and SD byte-equal to pre-call clone (atomic).
- [x] 3.3 `DaclOnlyChange_OwnerUntouched_ReturnsSuccess_AndAppliesDacl` — `DACL` only → `Success`, owner unchanged, WD ACE present.
- [x] 3.4 `OwnerInfoSetButSameOwner_ReturnsSuccess_AndAppliesDacl` — `O:BA…` + DACL, `OWNER|DACL` → `Success`, owner stays BA, DACL applied.
- [x] 3.5 `NonexistentNode_ReturnsObjectNameNotFound` — `Context=null` → `ObjectNameNotFound`.
- [x] 3.6 `MemfsReference_OwnerChange_ReturnsSameStatus_AsProduction` — drive both adapters' `CreateFile` then `SetFileSecurity` with an owner change; assert both return `0xC000005A`. Pins the lockstep the ChaosTests fuzzer cannot (it never issues SetSecurity).

## 4. Integration test — `tests/RamDrive.IntegrationTests`

- [x] 4.1 New `OwnerChangeRejectionTests.cs` in the `[Collection("RamDrive")]` fixture (UNC mount via `RamDriveFixture` — never a drive letter). `LibraryImport` interop for `CreateFileW` (BACKUP_SEMANTICS to open a dir handle), `SetKernelObjectSecurity`, `GetFileSecurityW`.
- [x] 4.2 `OwnerChangeToDifferentSid_IsRefused_AndDirStaysDeletableAndAclReadable` — create dir; read current owner; pick a target SID guaranteed to differ; open with WRITE_OWNER; `SetKernelObjectSecurity(OWNER, O:<target>)` → expect `false` + `ERROR_INVALID_OWNER` (1307); assert owner unchanged and `Directory.Delete` does not throw.
- [x] 4.3 `OwnerUnchanged_DaclTighten_Succeeds` — `DirectoryInfo.SetAccessControl` tightening the DACL without touching the owner → does not throw (guards against over-rejection).

## 5. OpenSpec change

- [x] 5.1 Create `openspec/changes/reject-owner-change-in-setsecurity/` with `proposal.md`, `design.md`, `tasks.md`, and `specs/security-owner-enforcement/spec.md` (new capability `security-owner-enforcement`).

## 6. Verify and finalise

- [x] 6.1 `dotnet build` Debug — succeeds; only the pre-existing warnings remain (no new ones from this change).
- [x] 6.2 `dotnet test tests/RamDrive.Core.Tests` — all green (53/53, incl. the 5 new SetSecurity tests).
- [x] 6.3 `dotnet test tests/RamDrive.IntegrationTests --filter OwnerChangeRejectionTests` — both new tests green on the UNC mount; confirms WinFsp maps `STATUS_INVALID_OWNER` → `ERROR_INVALID_OWNER` (1307).
- [x] 6.4 `dotnet test tests/RamDrive.IntegrationTests` full suite — 45/45 green (`AclInheritanceTests` and the rest unaffected); chaos 45s with `RAMDRIVE_DIFF=1` finished with zero `DifferentialMismatchException` (production/oracle lockstep holds).
- [x] 6.5 `openspec validate reject-owner-change-in-setsecurity` → "Change 'reject-owner-change-in-setsecurity' is valid".
- [ ] 6.6 Manual acceptance: re-mount RamDrive, run dotTrace profiling against `Z:\Temp` twice; confirm `jetbrainsproc_<GUID>` dirs are deletable across runs (no `copy_file` EEXIST). The leaked `f93`/`ce5` dirs from the original failure need admin or a remount to clear.
