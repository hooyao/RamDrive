## 1. SDDL fix

- [x] 1.1 In `src/RamDrive.Cli/WinFspRamAdapter.cs` (line 47), change the `RootSddl` constant from `O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)` to `O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;WD)`. Add an inline comment explaining that `OICI` = `OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE` and that without those flags `FspCreateSecurityDescriptor` does not propagate the ACE to children.
- [x] 1.2 Apply the same edit to `tests/RamDrive.IntegrationTests/RamDriveFixture.cs` (line 100). Both constants must remain identical so the integration suite exercises the same access-check behaviour as production.

## 2. Regression tests

- [x] 2.1 Add a unit test (e.g. `tests/RamDrive.Core.Tests/RootSddlTests.cs` or in IntegrationTests if the constant stays internal). The test parses the canonical SDDL via `RawSecurityDescriptor` and asserts: `DiscretionaryAcl` is non-empty; every `CommonAce` in it has `AceFlags` containing **both** `AceFlags.ObjectInherit` and `AceFlags.ContainerInherit`. This guards against future SDDL edits that silently drop the flags.
- [x] 2.2 Add an integration test (e.g. `tests/RamDrive.IntegrationTests/AclInheritanceTests.cs`) that:
  - Creates a directory under the fixture's mount root
  - Creates a file inside it via `File.WriteAllBytes` (so the path goes through the regular WinFsp Create path, not via `securityDescriptor` argument)
  - Closes the handle
  - Reopens the file with `FileMode.Open, FileAccess.Read, FileShare.None` — must succeed
  - Reopens with `FileSystemRights.Delete` (via `FileStream` constructor) — must succeed
  - Calls `File.GetAccessControl(path)` and asserts the returned `AuthorizationRuleCollection` contains at least one `FileSystemAccessRule` granting `FullControl` to a well-known principal (`Everyone` is the surest match across CI environments) with `IsInherited == true`
- [x] 2.3 Run the full integration suite locally; verify all 28 prior tests still pass plus the 1 new ACL test.

## 3. Documentation

- [x] 3.1 Update the `WinFsp Notes` section in repo-root `CLAUDE.md` where the root SDDL is mentioned (line 85). Replace the SDDL string and add a short note: "ACE flags `OICI` are required so newly-created files and directories inherit the same access — without them new objects get an empty DACL and reopen fails ACCESS_DENIED."
- [x] 3.2 Add a one-line note to `docs/leveldb-cache-coherency-postmortem.md` §9.1 cross-referencing this change as the fix for the "Profile error occurred" dialog symptom that surfaced during bug-2 diagnosis.

## 4. Ship

- [x] 4.1 Run `openspec validate fix-acl-inheritance --strict`.
- [x] 4.2 Create feature branch `fix/acl-inheritance` from `origin/main`. Move the local working-tree edits onto the branch (the SDDL change in two files is already done locally; commit it together with the new tests and the spec/proposal/design/tasks files).
- [ ] 4.3 Push branch, open PR with title `Fix ACL inheritance: newly-created files reopenable by creator`. PR body links to the procmon-evidence rows in `docs/leveldb-cache-coherency-postmortem.md` once that doc note is added.
- [ ] 4.4 After CI green and merge, archive the change with `/opsx:archive fix-acl-inheritance` and sync the new `default-security-descriptor` spec into `openspec/specs/`.
