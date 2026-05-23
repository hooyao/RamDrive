## MODIFIED Requirements

### Requirement: Volume root security descriptor SHALL grant inheritable access

The constant security descriptor applied to the volume root in `WinFspRamAdapter.Init` MUST be a SDDL string in which every ACE in the DACL carries both `OBJECT_INHERIT_ACE` (`OI`) and `CONTAINER_INHERIT_ACE` (`CI`) flags. WinFsp's kernel `FspCreateSecurityDescriptor` walks the parent DACL when computing the SD for a newly-created child and copies only ACEs whose flags say "inherit". With both flags present, the parent's `(A;OICI;FA;;;<sid>)` ACE produces a child ACE granting the same access (`FA` = `FILE_ALL_ACCESS`, including `DELETE`, `READ_CONTROL`, `SYNCHRONIZE`) to that SID on every newly-created file and directory.

The canonical form of the root SDDL MUST be:

```
O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;WD)
```

— `SY` = LocalSystem, `BA` = BUILTIN\Administrators, `WD` = Everyone.

The same SDDL constant MUST be used by the integration test fixture's `TestAdapter`, so that integration-test mounts behave identically to production mounts with respect to access checks on freshly-created objects.

**Structural invariant**: no `FileNode` in `RamFileSystem` may have `SecurityDescriptor == null`. This invariant MUST hold regardless of whether the node was created through the WinFsp callback path (`WinFspRamAdapter.CreateFile`) or through any internal bootstrap path that calls `RamFileSystem.CreateFile` / `RamFileSystem.CreateDirectory` directly (notably `WinFspHostedService.CreateDirectoriesRecursive` and `DiagHostedService.CreateRecursive`, both used to materialise `InitialDirectories` after mount). When a caller of `RamFileSystem.CreateFile` or `RamFileSystem.CreateDirectory` does not supply a security descriptor, the new node MUST inherit its parent directory's `SecurityDescriptor` reference rather than store `null`. Since the root is set up with the canonical SDDL above before any child is created, this guarantees every node has a valid self-relative SD at all times.

**Defence in depth**: `WinFspRamAdapter.GetFileSecurityByName` and `WinFspRamAdapter.GetFileSecurity` MUST NOT return `NtStatus.Success` together with a `null` (or zero-length) `securityDescriptor`. If a node's stored SD is somehow `null` at read time (a regression of the structural invariant above), the adapter MUST substitute the root SD before returning success. The kernel side `FspCreateSecurityDescriptor`, AppContainer access checks, and user-mode callers (`GetFileSecurity`/`Get-Acl`/`icacls`) all assume a non-null SD on success, and several treat null as "structure invalid" (`error 1338 ERROR_INVALID_SECURITY_DESCR`) rather than as "default access".

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

#### Scenario: InitialDirectories-created directory carries a valid SD
- **WHEN** the mount is started with configuration `RamDrive:InitialDirectories = { "Temp": {} }`
- **AND** the host service's `CreateDirectoriesRecursive` calls `RamFileSystem.CreateDirectory("\\Temp")` without supplying a security descriptor
- **THEN** the resulting `FileNode.SecurityDescriptor` is byte-equal to the root's `SecurityDescriptor`
- **AND** `WinFspRamAdapter.GetFileSecurityByName("\\Temp", ...)` returns the same SD bytes with `NtStatus.Success`
- **AND** `Get-Acl <mount>\Temp` from PowerShell succeeds (no `error 1338 ERROR_INVALID_SECURITY_DESCR`)
- **AND** the SD passes `IsValidSecurityDescriptor` and has `SE_SELF_RELATIVE | SE_DACL_PRESENT` in its control field

#### Scenario: Nested InitialDirectories produce non-null SDs at every level
- **WHEN** the mount is started with configuration `RamDrive:InitialDirectories = { "Cache": { "App1": {}, "App2": {} } }`
- **AND** all three directories (`\Cache`, `\Cache\App1`, `\Cache\App2`) are created via the host service's recursive bootstrap
- **THEN** none of the three resulting `FileNode.SecurityDescriptor` values is `null` or zero-length
- **AND** each inherits its parent's `SecurityDescriptor` (ultimately the root's)

#### Scenario: Subdirectory created under an InitialDirectories folder by a user-mode process is reopenable by AppContainer-style access checks
- **WHEN** an `InitialDirectories`-created folder such as `\Temp` exists
- **AND** a user-mode process (running with a restricted token whose only well-known SID is `Everyone`/`WD`) calls `CreateDirectory(<mount>\Temp\Sub)`
- **AND** the same process subsequently calls `CreateFile(<mount>\Temp\Sub, Disposition: Open, DesiredAccess: GENERIC_READ)`
- **THEN** the second call returns `STATUS_SUCCESS`
- **AND** `GetFileSecurity(<mount>\Temp\Sub)` returns a valid self-relative SD whose DACL contains the `WD` (Everyone) `FullControl` ACE inherited from the root

#### Scenario: GetFileSecurityByName never returns success with null SD
- **WHEN** `WinFspRamAdapter.GetFileSecurityByName` is invoked for any existing path on the mount
- **THEN** the returned `securityDescriptor` is non-null and non-empty whenever the return value is `NtStatus.Success`
- **AND** the response can be round-tripped through `RawSecurityDescriptor(bytes, 0)` without throwing

#### Scenario: GetFileSecurity never returns success with null SD
- **WHEN** `WinFspRamAdapter.GetFileSecurity` is invoked for any open file or directory handle on the mount
- **THEN** the returned `securityDescriptor` is non-null and non-empty whenever the return value is `NtStatus.Success`
- **AND** the response can be round-tripped through `RawSecurityDescriptor(bytes, 0)` without throwing

#### Scenario: MSIX AppContainer app launches successfully with TEMP pointing at an InitialDirectories-created folder
- **WHEN** the user sets the `TEMP` environment variable to `<mount>\Temp` where `\Temp` was created via `InitialDirectories`
- **AND** launches an MSIX-packaged application (e.g. `Windows365.exe` from `MicrosoftCorporationII.Windows365_*`) that runs under an AppContainer token
- **THEN** the application starts and remains running long enough to display its main window — it does NOT exit in less than five seconds with a launch-time fault
- **AND** the `Microsoft-Windows-AppModel-Runtime/Admin` log does NOT show repeated `201`/`217` create/destroy pairs at sub-minute intervals for that package

  *Note: this scenario captures end-user-observable behaviour for the bug that motivated this change. It is environment-sensitive (requires the MSIX package installed) and serves as the manual acceptance check; the scenarios above are automatable and together imply this outcome.*
