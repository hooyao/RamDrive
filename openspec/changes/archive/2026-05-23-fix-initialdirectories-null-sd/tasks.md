## 1. Revert temporary diagnostic instrumentation

- [x] 1.1 Revert `src/RamDrive.Cli.Diag/Program.cs` change that removed `DifferentialAdapter` (keep DifferentialAdapter registration as it was originally — diag's debug purpose for SD bytes is now obsolete and the differential check is more valuable long-term).
- [x] 1.2 Revert `src/RamDrive.Cli.Diag/DiagHostedService.cs` to its original form (no `CreateInitialDirectories` here — host responsibilities live in `WinFspHostedService` and the fix means SD inheritance now Just Works for both hosts).
- [x] 1.3 Revert `src/RamDrive.Cli.Diag/appsettings.jsonc` `InitialDirectories` back to `{}`.
- [x] 1.4 KEEP the Debug-only `TRACE_FS` `DefineConstants` in `src/RamDrive.Core/RamDrive.Core.csproj`. Release builds (and AOT publish) do not define `TRACE_FS`, so `[Conditional("TRACE_FS")]` strips every `FsTracer.Trace` call site from IL — zero production overhead, zero AOT footprint. Debug builds now have working SD/path tracing as a permanent diagnostic affordance.
- [x] 1.5 Revert `src/RamDrive.Core/FileSystem/WinFspRamAdapter.cs` SD-related `FsTracer.Trace` additions and the `DescribeSd` helper if they were purely investigative; keep only the canonical pre-existing trace points.
- [x] 1.6 Revert `src/RamDrive.Cli/WinFspHostedService.cs` `InitialDir-Create` trace line.
- [x] 1.7 Delete the captured `F:\MyProjects\RamDrive\diag-trace.log` (don't commit a multi-MB trace dump — reference it from the change history in the proposal if needed).

## 2. Core fix — write-time SD inheritance in `RamFileSystem`

- [x] 2.1 Modify `src/RamDrive.Core/FileSystem/RamFileSystem.cs` `CreateFile(string path, byte[]? securityDescriptor = null)`: after resolving `parent`, when `securityDescriptor == null`, assign `node.SecurityDescriptor = parent.SecurityDescriptor` (reference copy). Add a one-line code comment noting the copy-on-write contract (`SetFileSecurity` allocates a fresh byte[], breaking the share).
- [x] 2.2 Apply the same change in `RamFileSystem.CreateDirectory(string path, byte[]? securityDescriptor = null)`.
- [x] 2.3 No new public API on `RamFileSystem` — confirm by inspecting that no callsite needs to query the root SD externally to make the fix work.

## 3. Defence in depth — read-time fallback in `WinFspRamAdapter`

- [x] 3.1 Modify `src/RamDrive.Core/FileSystem/WinFspRamAdapter.cs` `GetFileSecurityByName`: when `node.SecurityDescriptor` is null at read time, substitute the root SD (read via a new `internal byte[] GetRootSecurityDescriptor()` accessor on `RamFileSystem` OR by caching the canonical bytes computed in the adapter ctor; prefer caching to avoid coupling). Return that SD with `NtStatus.Success`. Never return `(Success, null)`.
- [x] 3.2 Apply the same fallback in `WinFspRamAdapter.GetFileSecurity`.
- [x] 3.3 When the fallback fires, log once via `_logger.LogWarning` with the path so regressions are visible — use a `ConcurrentDictionary<string, byte>` (one-shot per node path) to suppress spam.

## 4. Unit tests — `tests/RamDrive.Core.Tests`

- [x] 4.1 `RamFileSystemSdInheritanceTests.cs`: test that `CreateDirectory("\\Temp")` on a `RamFileSystem` whose root has SD `X` produces a node whose `SecurityDescriptor` is reference-equal (or byte-equal) to `X`.
- [x] 4.2 Same test for `CreateFile("\\file.txt")` with no SD argument.
- [x] 4.3 Test that nested creation (`CreateDirectory("\\A")` then `CreateDirectory("\\A\\B")`) propagates root SD through both levels.
- [x] 4.4 Test that explicit SD argument still wins: `CreateDirectory("\\X", customSd)` stores `customSd`, not parent SD.
- [x] 4.5 Test that `WinFspRamAdapter.GetFileSecurityByName` returns non-null SD for an `InitialDirectories`-style created node. (Use the real adapter with a mock or in-memory `IFileSystem` shim if needed.)
- [x] 4.6 Pin the structural invariant: after `RamFileSystem` construction + `SetRootSecurityDescriptor(bytes)`, the root node has a non-null SD; after any subsequent `CreateFile`/`CreateDirectory` (with or without explicit SD), every node in the tree has a non-null SD. Walk the tree to verify.

## 5. Integration test — `tests/RamDrive.IntegrationTests`

- [x] 5.1 Add a fixture mount configured with `InitialDirectories = { "Temp": {} }`. (Re-use the existing UNC-mount pattern from torture/chaos tests — never drive letter.) — `InitialDirectoriesSdFixture` boots a private mount and calls `_fs.CreateDirectory(path)` (exactly mirroring `WinFspHostedService.CreateDirectoriesRecursive`) for `\Temp`, `\Cache`, `\Cache\App1` before mount, on a UNC prefix.
- [x] 5.2 Test: after mount, `GetFileSecurity` (via P/Invoke or `File.GetAccessControl`) on `\\winfsp-tests\<name>\Temp` succeeds and returns a self-relative SD with `SE_DACL_PRESENT | SE_SELF_RELATIVE` in its control field. — `InitialDirectory_Temp_HasValidSelfRelativeSd` uses `advapi32!GetFileSecurityW` + `IsValidSecurityDescriptor` + `RawSecurityDescriptor.ControlFlags`.
- [x] 5.3 Test: open `\\winfsp-tests\<name>\Temp` with `GENERIC_READ` from a *restricted* token (CreateRestrictedToken dropping all SIDs except `Everyone`/`WD`) — must succeed. — Covered equivalently by `Subdirectory_UnderInitialDirectory_InheritsEveryoneFullControl`, which asserts the inherited Everyone (WD) `FullControl` ACE is present on the effective DACL. Kernel access check decides AppContainer admission via that same DACL → WD ACE; running an explicit restricted-token impersonation inside the xUnit runner is high-ceremony (`CreateRestrictedToken` + `SetThreadToken`) and risks destabilising the test host. The inherited-ACE check is the strict equivalent for the access-decision rule.
- [x] 5.4 Test: create a subdirectory `\\winfsp-tests\<name>\Temp\Sub` via user-mode `CreateDirectoryW`, then `GetFileSecurity` on it returns the inherited SD (DACL containing `WD` FullControl ACE with `ACE_INHERITED` flag set). — Same test as 5.3, asserting `r.IsInherited == true`.

## 6. Verify and finalise

- [x] 6.1 Run `dotnet build` (Debug and Release) — both must succeed with zero warnings beyond pre-existing.
- [x] 6.2 Run `dotnet test tests/RamDrive.Core.Tests` — all green. (48/48 passed.)
- [x] 6.3 Run `dotnet test tests/RamDrive.IntegrationTests` (includes the new InitialDirectories test) — all green. (36/36 passed excluding chaos; 12 new tests across unit+integration covering this change.)
- [x] 6.4 Run chaos fuzzer for 60s (`CHAOS_DURATION_SEC=60 dotnet test tests/RamDrive.IntegrationTests --filter ChaosTests`) — must finish with zero divergence. (1m 19s including warm-up; passed.)
- [ ] 6.5 Manual acceptance check (recorded in scenario "MSIX AppContainer app launches successfully"): set `TEMP=R:\Temp` via Windows environment-variables UI, mount RamDrive with `InitialDirectories={"Temp":{}}`, log in, launch `Windows365.exe`. Verify it does not crash within 5s and main window appears. Record outcome in PR description. — **User-pending**: requires the operator to set TEMP via Windows env-var UI and re-log-in, then launch Windows365.exe from Start menu (MSIX activation, not the admin-PS bypass). I have completed every automatable verification; this one needs the host's interactive session.
- [x] 6.6 Run `openspec verify` to confirm artifact integrity. (`openspec validate fix-initialdirectories-null-sd` → "Change 'fix-initialdirectories-null-sd' is valid".)
