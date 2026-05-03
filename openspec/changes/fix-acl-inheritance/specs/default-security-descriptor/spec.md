## ADDED Requirements

### Requirement: Volume root security descriptor SHALL grant inheritable access

The constant security descriptor applied to the volume root in `WinFspRamAdapter.Init` MUST be a SDDL string in which every ACE in the DACL carries both `OBJECT_INHERIT_ACE` (`OI`) and `CONTAINER_INHERIT_ACE` (`CI`) flags. WinFsp's kernel `FspCreateSecurityDescriptor` walks the parent DACL when computing the SD for a newly-created child and copies only ACEs whose flags say "inherit". With both flags present, the parent's `(A;OICI;FA;;;<sid>)` ACE produces a child ACE granting the same access (`FA` = `FILE_ALL_ACCESS`, including `DELETE`, `READ_CONTROL`, `SYNCHRONIZE`) to that SID on every newly-created file and directory.

The canonical form of the root SDDL MUST be:

```
O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;WD)
```

— `SY` = LocalSystem, `BA` = BUILTIN\Administrators, `WD` = Everyone.

The same SDDL constant MUST be used by the integration test fixture's `TestAdapter`, so that integration-test mounts behave identically to production mounts with respect to access checks on freshly-created objects.

#### Scenario: Newly-created file is reopenable by the calling principal
- **WHEN** a process holding `Generic Read/Write` on the volume root creates a file at any path under the root via `CreateFile(Disposition: Create, ...)`
- **AND** that process subsequently calls `CreateFile(Disposition: Open, DesiredAccess: Read Attributes | Delete | Synchronize, ShareMode: Read|Write|Delete)` on the same path
- **THEN** the second call returns `STATUS_SUCCESS`, never `STATUS_ACCESS_DENIED`
- **AND** the resulting handle can be used to mark the file for delete-on-close

#### Scenario: Newly-created directory is reopenable for enumeration
- **WHEN** a process creates a directory under the volume root
- **AND** subsequently opens it with `Read Data/List Directory` access
- **THEN** the open succeeds and the directory contents are enumerable

#### Scenario: Effective ACL on a fresh file grants FullControl to Everyone
- **WHEN** a file is created under the mounted volume from any process
- **THEN** `File.GetAccessControl(path)` returns an `AuthorizationRuleCollection` containing at least one rule that grants `FullControl` to `Everyone` (the `WD` SID), with `IsInherited = true`
- **AND** the rule's `InheritanceFlags` reflect that it propagated from the parent's `OICI` ACE

#### Scenario: SDDL string itself carries OI|CI on every ACE
- **WHEN** the canonical root SDDL constant is parsed via `RawSecurityDescriptor`
- **THEN** every ACE in the resulting `DiscretionaryAcl` has its `AceFlags` containing both `AceFlags.ObjectInherit` and `AceFlags.ContainerInherit`
- **AND** this property holds even after a future edit to the SDDL string — the unit test that asserts it MUST live alongside the constant so any drop of OI/CI fails CI rather than only failing against real applications

#### Scenario: Chrome-launched browser does not show "Profile error" dialog
- **WHEN** a Chromium-based browser is launched with `--user-data-dir=<volume-root>\Temp\<userdir>`
- **AND** the cache is configured per the production default (`EnableKernelCache=true`, `FileInfoTimeoutMs=1000`)
- **THEN** the browser does NOT display a "Profile error occurred — Something went wrong when opening your profile" dialog
- **AND** SQLite-backed components (top_sites, login_database, web_data) initialise without `Failed to initialize database` / `Could not create/open` errors in the browser's stderr

  *Note: this scenario captures end-user-observable behaviour. It is not directly automated as a regression test (Chromium launch is environment-sensitive), but the four scenarios above together imply this outcome and are automatable.*
