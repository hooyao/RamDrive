## Why

Directories created via the `InitialDirectories` configuration option (e.g. `{ "Temp": {} }`) are born with a NULL security descriptor — they bypass the WinFsp callback path that `default-security-descriptor` covers, going straight to `RamFileSystem.CreateDirectory(path)` without passing an SD. `WinFspRamAdapter.GetFileSecurityByName` then returns this NULL SD to the kernel as `STATUS_SUCCESS`, which strict access-check clients (MSIX AppContainer, Windows365.exe, AppX-packaged tools, anything calling `GetFileSecurity` and validating the result) interpret as "no SD" → `error 1338 ERROR_INVALID_SECURITY_DESCR` from `icacls`/`Get-Acl`, and access denied from kernel `SeAccessCheck`. End-user symptom: setting `TEMP=R:\Temp` (or any `InitialDirectories`-created folder) makes Windows365.exe crash on launch. Trace captured in `diag-trace.log` shows the NULL SD is present from the moment of creation — no external process is corrupting it; the bug is purely in our bootstrap path.

The fix also has to handle subdirectories created underneath an `InitialDirectories` folder: if `\Temp` itself has NULL SD, WinFsp's kernel inheritance computation reads that NULL and propagates the corruption downward. Surface fixes only at the `InitialDirectories` callsite are not enough — the invariant we need is "no node in the tree ever has NULL SD".

## What Changes

- `RamFileSystem.CreateDirectory(path, sd)` and `RamFileSystem.CreateFile(path, sd)`: when `sd == null`, inherit the parent directory's `SecurityDescriptor` instead of storing NULL. This makes "no NULL SD nodes exist in the tree" a structural invariant of `RamFileSystem`.
- `WinFspRamAdapter.GetFileSecurityByName` and `GetFileSecurity`: defence in depth — if a node's `SecurityDescriptor` is somehow still NULL at read time, fall back to the root SD rather than returning NULL with success. (Should be unreachable after the structural fix, but guards against future regressions.)
- `WinFspHostedService.CreateDirectoriesRecursive` / `DiagHostedService.CreateRecursive`: no functional change — they continue calling `_fs.CreateDirectory(path)` without an SD argument; the inheritance now Just Works.
- New unit tests in `RamDrive.Core.Tests` covering: (a) `CreateDirectory` with `sd=null` inherits parent SD; (b) nested `InitialDirectories` recursion produces non-NULL SDs at every level; (c) `GetFileSecurityByName` never returns `(NtStatus.Success, sd=null)`.
- New integration test in `RamDrive.IntegrationTests` that boots the mount with `InitialDirectories={ "Temp": {} }` and asserts `Get-Acl` / `GetFileSecurity` on `\Temp` returns a valid self-relative SD with `SE_DACL_PRESENT|SE_SELF_RELATIVE` and the same DACL as the root.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `default-security-descriptor`: extend its single requirement so that the inheritance guarantee holds for ALL paths that create nodes in `RamFileSystem`, not just the WinFsp `CreateFile`/`CreateDirectory` callback path. Add scenarios covering (a) `InitialDirectories`-created directories carry the root SD, (b) subdirectories under `InitialDirectories` folders are reopenable by AppContainer/strict-access-check clients, (c) `GetFileSecurityByName` MUST NOT return success with a NULL SD.

## Impact

- **Code**: `src/RamDrive.Core/FileSystem/RamFileSystem.cs` (CreateDirectory / CreateFile parent-SD inheritance); `src/RamDrive.Core/FileSystem/WinFspRamAdapter.cs` (GetFileSecurityByName / GetFileSecurity fallback). The two HostedServices (`WinFspHostedService`, `DiagHostedService`) are unchanged.
- **Tests**: new unit tests in `tests/RamDrive.Core.Tests`; new integration test in `tests/RamDrive.IntegrationTests` reproducing the AppContainer-style access check.
- **Specs**: `openspec/specs/default-security-descriptor/spec.md` gains new scenarios.
- **Behavioural impact for users**: `InitialDirectories` paths become usable as TEMP/cache directories for MSIX/AppContainer apps (Windows365.exe, Edge in some configurations, packaged Office tools). No breaking change to existing callers — every existing path either already passed a valid SD or relied on NULL behavior in a way no test exercised.
- **TLA+ model**: no change required; the model abstracts away SD bytes and treats `DoCreateFile` atomically. SD inheritance is below the abstraction line.
- **Performance**: one extra reference assignment when `sd==null` in CreateDirectory/CreateFile. Zero managed heap allocation (we copy the byte[] reference, not the bytes). No impact on the hot path (Read/Write).
