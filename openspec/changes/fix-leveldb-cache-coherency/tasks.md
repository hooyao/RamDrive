## 1. Binding: Add Notify API to winfsp-native

- [x] 1.1 In `src/WinFsp.Native/Interop/FspApi.cs`, add `[LibraryImport]` for `FspFileSystemNotify(nint fileSystem, nint notifyInfo, nuint size) -> int` next to the existing `FspFileSystemNotifyBegin/End` declarations.
- [x] 1.2 In `src/WinFsp.Native/Interop/FspStructs.cs`, add a `[StructLayout(LayoutKind.Sequential, Pack = 2)]` `FspFsctlNotifyInfo` struct with fields `ushort Size; uint Filter; uint Action;` (12-byte header to match the C `FSP_FSCTL_NOTIFY_INFO`). Verify `sizeof(FspFsctlNotifyInfo) == 12` in a unit test.
- [x] 1.3 In `src/WinFsp.Native/`, create `FileNotify.cs` exposing `public static class FileNotify` with `uint` constants: `ChangeFileName=0x1`, `ChangeDirName=0x2`, `ChangeAttributes=0x4`, `ChangeSize=0x8`, `ChangeLastWrite=0x10`, `ChangeLastAccess=0x20`, `ChangeCreation=0x40`, `ChangeSecurity=0x100`, `ActionAdded=1`, `ActionRemoved=2`, `ActionModified=3`, `ActionRenamedOldName=4`, `ActionRenamedNewName=5`. Cross-check values against `winfsp/inc/winfsp/fsctl.h` and the Windows SDK `WinNT.h`.
- [x] 1.4 In `src/WinFsp.Native/FileSystemHost.cs`, add `public unsafe int Notify(uint filter, uint action, ReadOnlySpan<char> fileName)` that:
  - Stack-allocates a `Span<byte>` of size `12 + 2*fileName.Length` (cap at 4096; longer names allocate via `ArrayPool<byte>` with a try/finally `Return`).
  - Writes header `{Size, Filter, Action}` at offset 0.
  - Encodes `fileName` into the buffer at offset 12 using `Encoding.Unicode.GetBytes(fileName, …)`.
  - If `_caseSensitiveSearch == false`, calls `CharUpperBuffW` (P/Invoke into `user32.dll`) on the in-buffer name pointer for `fileName.Length` chars.
  - Pins the buffer and invokes `FspApi.FspFileSystemNotify(_fileSystemHandle, ptr, (nuint)totalSize)`. Returns the NTSTATUS unchanged.
- [x] 1.5 Add `[LibraryImport("user32.dll", EntryPoint = "CharUpperBuffW")] static partial uint CharUpperBuffW(char* lpsz, uint cchLength);` in a private interop class in `FileSystemHost.cs` (or `Interop/FspApi.cs` if it fits the existing layout).
- [x] 1.6 Update `src/WinFsp.Native/CLAUDE.md` "Namespace Layout" table to include `FileNotify` under `WinFsp.Native`. Add a "Notification" subsection to "Key Design Decisions" explaining single-shot vs `Begin/End` framing and the case-insensitive normalization rule.
- [x] 1.7 Add unit test `tests/WinFsp.Native.Tests/NotifyTests.cs` (or extend an existing test class) with two tests:
  - `Notify_PacksHeaderAndPath_CaseSensitive`: mounts a `TestMemFs` with `CaseSensitiveSearch=true`, calls `host.Notify(FileNotify.ChangeFileName, FileNotify.ActionAdded, @"\probe")`, asserts NT status is `STATUS_SUCCESS` and the call did not throw. (The actual cache invalidation is not directly observable from a unit test — the integration suite in the RamDrive repo covers behaviour.)
  - `Notify_UpperCasesPath_CaseInsensitive`: same as above but with `CaseSensitiveSearch=false` and a mixed-case path; intercept the buffer via a test hook (or add an `internal` overload that returns the prepared buffer) to verify the upper-casing happens before the P/Invoke.
- [x] 1.8 Bump the binding's package version to `0.1.2-pre.1` in `src/WinFsp.Native/WinFsp.Native.csproj`. Run `dotnet pack -c Release -o ./artifacts`. Note the local `.nupkg` path for step 3.1.

## 2. Adapter: Wire notifications into mutators

- [x] 2.1 In `src/RamDrive.Cli/WinFspRamAdapter.cs`, add a private `FileSystemHost? _host` field and capture the host reference in `Mounted(FileSystemHost host)`.
- [x] 2.2 Add a private helper `void Notify(uint filter, uint action, string path)` that calls `_host?.Notify(filter, action, path)` and silently swallows any non-success NTSTATUS (logging at `LogLevel.Trace` via the existing `_logger`). Comment that this is intentional per the `cache-invalidation` spec — IRPs must succeed even if cache invalidation fails.
- [x] 2.3 Wire `MoveFile` (currently lines 360–372) to call `Notify(FileNotify.ChangeFileName, FileNotify.ActionRenamedOldName, fileName)` then `Notify(FileNotify.ChangeFileName, FileNotify.ActionRenamedNewName, newFileName)` after `_fs.Move` returns true. Both calls happen after the `RamFileSystem` lock is released.
- [x] 2.4 Wire `Cleanup` (currently lines 378–394): after `_fs.Delete(fileName)` succeeds, call `Notify(node.IsDirectory ? FileNotify.ChangeDirName : FileNotify.ChangeFileName, FileNotify.ActionRemoved, fileName)`. Capture the node's `IsDirectory` flag *before* the delete (the node is disposed inside `_fs.Delete`).
- [x] 2.5 Wire `OverwriteFile` (currently lines 167–189): after `node.Content.SetLength(0)` succeeds, call `Notify(FileNotify.ChangeSize | FileNotify.ChangeLastWrite, FileNotify.ActionModified, _)`. Reuse the path from `info.Context` — the adapter currently doesn't get a path parameter; if needed, capture it in `OpenFile`/`CreateFile` and store on `info.Context` alongside the `FileNode`.
- [x] 2.6 Wire `CreateFile` (currently lines 116–152): after the new file/dir is created, call `Notify(isDir ? FileNotify.ChangeDirName : FileNotify.ChangeFileName, FileNotify.ActionAdded, fileName)`. This addresses negative-cache invalidation per the `cache-invalidation` spec.
- [x] 2.7 Wire `SetFileSize` (currently lines 281–305): when `setAllocationSize == false` and `SetLength` succeeds, call `Notify(FileNotify.ChangeSize | FileNotify.ChangeLastWrite, FileNotify.ActionModified, fileName)`.
- [x] 2.8 Wire `SetFileAttributes` (currently lines 259–279): after the attribute/timestamp updates land, call `Notify(FileNotify.ChangeAttributes | FileNotify.ChangeLastWrite, FileNotify.ActionModified, fileName)`.
- [x] 2.9 Add an XML doc comment block at the top of the adapter listing the notification matrix (mirrors the table in `design.md` Decision 4) so future maintainers see the contract without leaving the file.

## 3. Configuration: FileInfoTimeoutMs option

- [x] 3.1 In `RamDrive.Cli.csproj`, switch the `<PackageReference Include="WinFsp.Native">` to `Version="0.1.2-pre.1"` (or the local `.nupkg` path produced in 1.8). Restore and verify the build still compiles.
- [x] 3.2 In `src/RamDrive.Core/Configuration/RamDriveOptions.cs`, add `public uint FileInfoTimeoutMs { get; set; } = 1000;`. Add an XML doc comment explaining the trade-off (notifications first, timeout as defence in depth) and the meaning of `0` and `uint.MaxValue`.
- [x] 3.3 In `src/RamDrive.Cli/WinFspRamAdapter.cs::Init`, replace `if (_options.EnableKernelCache) host.FileInfoTimeout = unchecked((uint)(-1));` with:
  ```csharp
  host.FileInfoTimeout = _options.EnableKernelCache ? _options.FileInfoTimeoutMs : 0u;
  ```
- [x] 3.4 In `src/RamDrive.Cli/appsettings.jsonc`, add `"FileInfoTimeoutMs": 1000` to the `RamDrive` section with a `// 0 = no kernel cache; uint.MaxValue = trust notifications only` comment. Update the existing `EnableKernelCache` comment to clarify that `false` overrides this to `0`.
- [x] 3.5 Update `CLAUDE.md` configuration table (lines 257–266) to include `FileInfoTimeoutMs` and the updated `EnableKernelCache` semantics. Replace the line "Uses WinFsp `FileInfoTimeout=MAX`" with a reference to the new option.

## 4. Tests: Repro + regression coverage

- [x] 4.1 In `tests/RamDrive.IntegrationTests/RamDriveFixture.cs`, ensure the `TestAdapter.Init` sets `host.FileInfoTimeout = unchecked((uint)(-1))` (i.e. `uint.MaxValue`) — the worst-case lifetime — so any missing notification fails the suite. Revert the temporary `1000` value used during diagnosis.
- [x] 4.2 Apply the same notification matrix as 2.x to the fixture's `TestAdapter` so it stays behaviour-equivalent to the production adapter (the integration suite tests the matrix; the fixture must implement it).
- [x] 4.3 Verify the existing `tests/RamDrive.IntegrationTests/LevelDbReproTests.cs` (added during diagnosis) compiles against the new `TestAdapter` and the `Win32_LevelDb_Sequence_Cached` test PASSES with the matrix wired in.
- [x] 4.4 Add `LevelDbReproTests.MixedCase_Win32_LevelDb_Sequence` covering paths like `\Temp\Dbtmp` → `\TEMP\CURRENT` (mixed-case), asserting the post-rename read still returns full content. Validates the case-insensitive upper-casing in 1.4.
- [x] 4.5 Add a focused regression test `LevelDbReproTests.Notify_DefaultTimeoutAlsoWorks` that mounts a *separate* fixture instance with `FileInfoTimeout = 1000` and runs the same leveldb sequence. Asserts the test passes regardless of timeout — verifies the production default does not regress correctness.
- [x] 4.6 Strip the verbose `RamDriveFixture.Trace` instrumentation added during diagnosis (it stays as opt-in via `SetTraceFilter`), keeping the helper for future debugging but ensuring it's a no-op when the filter is null.
- [x] 4.7 Run `dotnet test tests/RamDrive.IntegrationTests` end-to-end. Required result: `TortureTests` (10), `ChaosTests` (1), `InitialDirectoryTests`, `CacheCoherencyTests` (7), `LevelDbReproTests` (3) all PASS. Capture the duration; should stay under the current ~45 s + a few seconds for the new tests.

## 5. Manual end-to-end validation

- [x] 5.1 Build a fresh `RamDrive.exe` (`dotnet build -c Debug src/RamDrive.Cli`).
- [x] 5.2 Mount manually at a sandbox drive letter (`H:` per memory of available letters): `dotnet run --project src/RamDrive.Cli -- --RamDrive:MountPoint='H:\\' --RamDrive:CapacityMb=128 --RamDrive:VolumeLabel=DevTest`. Default `FileInfoTimeoutMs=1000` and `EnableKernelCache=true`.
- [x] 5.3 Run `node F:\MyProjects\RamDrive\repro_chrome.js H:\Temp\repro_post_fix` (the Node repro script created during diagnosis). Expect: chrome reaches `about:blank`, no `Corruption: CURRENT does not end with newline` lines in stderr, the script's 30 s `child.kill()` is what terminates the process (not a self-crash). **Result: leveldb files written correctly (`CURRENT` ends with `\n` confirmed by hex dump). The original "Corruption" warning is gone — leveldb fix works in production.** However a SEPARATE pre-existing bug surfaces: `--remote-debugging-pipe` + `EnableKernelCache=true` produces `STATUS_BREAKPOINT` very early (before chrome's logging is set up). Direct `chrome.exe` (no `--remote-debugging-pipe`) runs fine on H: with cache enabled — full UI, network, USB enumeration. The pipe-mode crash also reproduces with `Notify` entirely disabled, confirming it is **not** related to this change. Filed as follow-up: a new RamDrive bug independent of the leveldb cache-coherency issue this change fixes.
- [ ] 5.4 Repeat 5.2 with `--RamDrive:FileInfoTimeoutMs=4294967295` (max value) and confirm chrome still launches cleanly — proves notifications alone are sufficient. **Skipped: blocked by the separate `--remote-debugging-pipe` bug above. The integration test `Win32_LevelDb_Sequence_Cached` covers this configuration without needing chrome.**
- [x] 5.5 Repeat 5.2 with `--RamDrive:EnableKernelCache=false` and confirm chrome still launches (no cache, no race, but slower). This is the documented backout configuration. **Result: chrome launches and runs cleanly via `--remote-debugging-pipe` (SIGTERM after 30s).** Confirms backout config works as documented.
- [ ] 5.6 Spot-check throughput: `dotnet run --project tests/RamDrive.Benchmarks -c Release -- onread`. Compare to the pre-change baseline. Acceptable: within ±5% noise. Record the numbers in the change folder for reviewers. **Deferred: requires a known-good baseline; can be run when needed.**

## 6. Cleanup and ship

- [x] 6.1 Delete or `.gitignore` the diagnostic artefacts at the repo root: `repro_chrome.js`, `procmon_chrome.pml`, `procmon_chrome.csv`, `Procmon64.exe`, `imdiskBench.png`, `winfspbench.png`/`winfspBench[2-5].png`. Keep `tla/` and `winfsp-native/` (those are real code). **(Done via .gitignore; `repro_chrome.js` is intentionally kept and referenced by the postmortem doc.)**
- [x] 6.2 Run `openspec validate fix-leveldb-cache-coherency --strict` and resolve any warnings.
- [ ] 6.3 Commit the binding change in the `winfsp-native` repo with message `feat: add FileSystemHost.Notify for kernel cache invalidation`. Tag `v0.1.2-pre.1`.
- [ ] 6.4 Commit the RamDrive change with message `fix: invalidate kernel FileInfo cache on path mutations (leveldb/Chromium)`. Reference the change folder in the commit body.
- [ ] 6.5 Run `/opsx:archive fix-leveldb-cache-coherency` once both commits land and CI is green.

## 7. Postmortem doc + TLA+ modeling extension

- [x] 7.1 Write `docs/leveldb-cache-coherency-postmortem.md`. **Self-contained for a future Claude Code session — assume the reader has only this file, the repo, and `tla/RamDiskSystem.tla`.** Include:
  - **Symptom**: Chromium launched with `--user-data-dir=Z:\...` exits with `STATUS_BREAKPOINT (0x80000003)`; stderr shows `leveldb_database.cc:124] ... Corruption: CURRENT file does not end with newline`. Reproducible across `Local Storage`, `GCM Store`, `Sync Data`, `Site Characteristics Database`, etc. — every leveldb-backed component.
  - **Environment**: Windows 11 Pro for Workstations 26200, WinFsp 2.x, RamDrive mounted at `Z:\` (drive-letter mode, MountManager-registered). Failure observed with default `EnableKernelCache=true` (`FileInfoTimeout=uint.MaxValue`). Same Chromium config on ImDisk `V:\` works fine — proves it is FS-side.
  - **Repro recipe** (the actual node script we used): include the contents of `repro_chrome.js` verbatim in a fenced code block, plus the exact command line `node repro_chrome.js Z:\Temp\rarbg_proc`. Note that Playwright-style `--remote-debugging-pipe` requires fd 3/4 to be inherited via `stdio: ['ignore','pipe','pipe','pipe','pipe']`; without that, Chromium exits 13 with "Remote debugging pipe file descriptors are not open" and the bug never triggers.
  - **Diagnostic dead-ends** (so the next session does not redo them): the original bug report listed 8 suspects (DRIVE_FIXED, mountvol GUID, GetVolumeInformation, LockFileEx semantics, byte-range Lock callback, FileIdInfo, FlushFileBuffers, sandbox path checks). All eight were verified non-issues on this repo. The `WinFsp.Native` `IFileSystem` interface does not expose Lock/Unlock — WinFsp handles byte-range locks in the kernel; the user-mode FS does not implement them. Document this so nobody chases it again.
  - **How procmon was used**: launch via `"C:\Users\HuYao\Desktop\Procmon - Copy.exe" /AcceptEula /Quiet /Minimized /BackingFile F:\procmon_chrome.pml /Runtime 35` from an admin shell, simultaneously run the node repro, then export with `... /OpenLog F:\procmon_chrome.pml /SaveAs F:\procmon_chrome.csv`. The csv is ~150 MB; filter via `grep '"chrome.exe"' procmon_chrome.csv | grep '\\CURRENT' | grep -v SUCCESS`. **Key caveat**: procmon stretches IRP timing enough to mask the STATUS_BREAKPOINT (chrome runs further before crashing), but the underlying `Corruption: CURRENT does not end with newline` warning still appears — diagnose against the warning, not the exit code.
  - **The smoking-gun trace** (paste the precise 5-line procmon excerpt that nailed it):
    ```
    SetRenameInformationFile  dbtmp -> CURRENT  SUCCESS  ReplaceIfExists:False
    CreateFile                CURRENT          SUCCESS  Generic Read
    ReadFile                  CURRENT          END OF FILE  Length: 8192
    ```
    Comment that ReadFile being END OF FILE proves the kernel did not consult user-mode — WinFsp returned cached size=0 from the negative cache populated by earlier `CreateFile NAME NOT FOUND` lookups.
  - **Root cause**: `WinFspRamAdapter.Init` set `host.FileInfoTimeout = uint.MaxValue`. Adapter never invalidated the kernel's cache after `MoveFile`. Negative cache for `CURRENT` ("name does not exist") survived the rename.
  - **Reproduction in the integration suite without procmon** (so the next session can iterate locally): the file `tests/RamDrive.IntegrationTests/LevelDbReproTests.cs` and the trace plumbing on `RamDriveFixture` (`SetTraceFilter`, `Trace`). Show the failing-then-passing trace excerpt and the toggle (fixture's `FileInfoTimeout` between `uint.MaxValue` and `1000`).
  - **Fix summary**: binding adds `FileSystemHost.Notify`; adapter calls it from every path-mutating callback per the matrix in `design.md` Decision 4. Belt-and-braces: `FileInfoTimeoutMs` config defaults to 1000 ms.
  - **Open follow-ups**: TLA+ modeling (described in 7.2–7.4 below); also the question whether `WriteFile` needs a `(ChangeSize, ActionModified)` notification when size grew (`design.md` Open Questions).
- [x] 7.2 In `docs/leveldb-cache-coherency-postmortem.md`, append a **"TLA+ modeling extension"** section describing the planned model changes in enough detail that a fresh session can implement without re-deriving them. Include:
  - **Why model it**: the previous `RamDiskSystem.tla` already verifies internal data integrity but treats the kernel as a passive observer. The new bug is between user-mode state and the kernel's `FileInfo` cache — exactly the `GetVolumeInfo` / `FreeBytes` lesson from the existing `Modeling guidelines` section in `CLAUDE.md`. The notification matrix is verifiable.
  - **New TLA+ variables** (give exact intended type, not pseudocode):
    - `kernelCache \in [Path -> {NotCached} \cup [size: Nat, exists: BOOLEAN]]`
    - `cacheMode \in {Permanent, Bounded}` — corresponds to `FileInfoTimeoutMs = uint.MaxValue` vs finite. Bounded mode enables a non-deterministic `CacheExpire(path)` action.
  - **New actions**:
    - `KernelOpenFile(path)`: if `kernelCache[path] = NotCached`, perform the existing `DoOpen(path)` and write the result to `kernelCache`. Otherwise return the cached value with NO call into the user-mode portion.
    - `KernelReadFile(path)`: same shape — relies on cached size if present.
    - `Notify(path, action)`: sets `kernelCache[path] := NotCached`. Action is one of `Added`, `Removed`, `RenamedOldName`, `RenamedNewName`, `Modified`.
    - `CacheExpire(path)` — only enabled when `cacheMode = Bounded`; non-deterministically clears any cache entry. Models the timeout.
  - **Modify existing actions** to call `Notify` per the matrix:
    - `DoCreateFile(f)` → `Notify(f, Added)`
    - `DoMove(f, g)` → `Notify(f, RenamedOldName)` then `Notify(g, RenamedNewName)`
    - `DoDelete(f)` → `Notify(f, Removed)`
    - `DoTruncate(f, n)` / `DoExtend(f, n)` → `Notify(f, Modified)`
    - `DoWriteP3(f)` → no notification (kernel page cache is invalidated by the FSD using the `FspFileInfo` returned by `Write`, not by `Notify`; document this asymmetry).
  - **Invariants to add**:
    - `CacheCoherent`: every `Cached(s, e)` entry agrees with the current user-mode state of that path, OR `cacheMode = Bounded` (i.e. an entry may be transiently stale only when timeout-bounded).
    - `NoStaleSizeAfterRename`: after `DoMove(f, g)`, any subsequent `KernelReadFile(g)` returns the post-move size of `g`, not a pre-move cached value.
  - **Liveness to add**:
    - `RenameThenReadEventuallyConsistent`: in `Bounded` mode, `[](DoMove(f,g) ~> KernelReadFile(g) returns SizeOf(g))`. In `Permanent` mode the same property holds because the matrix forces `Notify`.
  - **Bug-finding test**: the model should be **provably faulty when `Notify` is removed from `DoMove`** under `cacheMode = Permanent`. The next session should run a configuration with this regression to confirm TLC produces a counterexample matching the procmon trace from 7.1.
  - **State-space estimate**: minimal config (3 pages × 2 files × 2 paths × 3 cacheStates) on top of current `RamDiskSystem_Minimal.cfg` (≈ 12 min, 3M states) is expected to grow to ≈ 1–2 hours. Standard config (≈ 5 h, 66M states) likely 1–2 days. If TLC blows up, restrict `Path` to a 2-element symmetry set first.
- [ ] 7.3 Update `tla/RamDiskSystem.tla` per 7.2: add variables, actions, invariants, liveness. Update `tla/RamDiskSystem_Minimal.cfg` to declare the new constants (`Permanent`/`Bounded` cache modes; default to `Permanent` for fastest counterexample-finding).
- [ ] 7.4 Run TLC on the minimal config. Required outcome: with the notification matrix in place, all invariants hold and no liveness violation. Then deliberately remove the `Notify(g, RenamedNewName)` from `DoMove` and re-run — TLC must produce a counterexample trace that shows `KernelReadFile(g)` returning a stale size after rename. Capture both runs (clean + counterexample) into `tla/RamDiskSystem_CacheModel_results.txt` so the postmortem doc has concrete output to reference.
- [ ] 7.5 Append the TLC results to the postmortem doc under a "Verification results" section, then update `CLAUDE.md`'s "How the model maps to code" table with the new actions (`KernelOpenFile`, `KernelReadFile`, `Notify`, `CacheExpire`) and their corresponding code locations (`FileSystemHost.Notify`, `WinFspRamAdapter` matrix calls).
