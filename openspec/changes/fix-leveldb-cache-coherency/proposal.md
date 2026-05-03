## Why

Chromium's leveldb (used for `Local Storage`, `Sync Data`, `GCM Store`, etc.) does an atomic-rename + immediate read on the `CURRENT` file every DB open. With WinFsp's `FileInfoTimeout` set to `uint.MaxValue` (the current default for `EnableKernelCache=true`), the kernel keeps the negative-cached "CURRENT does not exist / size=0" entry forever, so the read after `MoveFile(dbtmp → CURRENT)` returns 0 bytes. Leveldb reports `Corruption: CURRENT does not end with newline`, and Chromium's downstream `CHECK()` macros crash the process with `STATUS_BREAKPOINT (0x80000003)`. This is reproducible on every Chromium-based browser launched with `--user-data-dir` on the RAM drive.

## What Changes

- Add a `Notify` API to the `WinFsp.Native` binding so user-mode file systems can invalidate the kernel's `FileInfo` cache after path-mutating operations (`Rename`, `Delete`, `Overwrite`).
- Wire `WinFspRamAdapter` mutators (`MoveFile`, `Cleanup` with `Delete` flag, `OverwriteFile`) to send `FILE_ACTION_*` notifications for every affected path.
- Add a `FileInfoTimeoutMs` configuration option (default `1000`) to replace the unconditional `uint.MaxValue` timeout. Notifications are the primary correctness mechanism; the timeout is a defence-in-depth bound for any path that escapes notification.
- Add an integration test `LevelDbReproTests` that performs the exact Win32 `WriteFile (cached) → FlushFileBuffers → Close → MoveFileEx → Open → ReadFile` sequence captured from Chromium's leveldb, and asserts the read returns the full content. Test runs against the fixture with `FileInfoTimeout=uint.MaxValue` so it will keep catching cache-invalidation regressions.

## Capabilities

### New Capabilities
- `cache-invalidation`: Path-mutating callbacks must notify the WinFsp kernel cache so subsequent opens/reads see fresh state, even when `FileInfoTimeout` is large or `uint.MaxValue`.
- `file-info-timeout-config`: Operators can bound the kernel `FileInfo` cache lifetime via `RamDrive:FileInfoTimeoutMs`, with a safe default that prevents indefinite stale-cache survival if a notification is missed.

### Modified Capabilities
<!-- None — this is the first change in the project; no existing specs to modify. -->

## Impact

- **`winfsp-native` repo** (binding):
  - `src/WinFsp.Native/Interop/FspApi.cs`: add `FspFileSystemNotify` P/Invoke.
  - `src/WinFsp.Native/Interop/FspStructs.cs`: add `FspFsctlNotifyInfo` (12-byte header + inline UTF-16 name) and `FILE_NOTIFY_CHANGE_*` / `FILE_ACTION_*` constants.
  - `src/WinFsp.Native/FileSystemHost.cs`: add public `Notify(filter, action, fileName)` and `NotifyBatch(...)` (handles `NotifyBegin`/`NotifyEnd` framing).
  - Bumped binding version; consumed via local project ref or new pre-release NuGet.
- **`RamDrive` repo**:
  - `src/RamDrive.Cli/WinFspRamAdapter.cs`: invoke `host.Notify(...)` after `MoveFile`, `Cleanup(Delete)`, and `OverwriteFile`. Replace `host.FileInfoTimeout = uint.MaxValue` with the configured `FileInfoTimeoutMs`.
  - `src/RamDrive.Core/Configuration/RamDriveOptions.cs`: add `FileInfoTimeoutMs` (default `1000`).
  - `appsettings.jsonc`: document the new key.
  - `tests/RamDrive.IntegrationTests/RamDriveFixture.cs`: keep `FileInfoTimeout = uint.MaxValue` in the test adapter so the leveldb regression test exercises the worst-case cache lifetime.
  - `tests/RamDrive.IntegrationTests/LevelDbReproTests.cs`: new test, plus a callback trace utility on the fixture.
  - `CLAUDE.md`: update the configuration table and remove the "FileInfoTimeout=MAX" framing as a pure throughput knob.
- **No persisted state migration**; behaviour change is mount-time only.
- **Throughput**: with notifications wired correctly, a finite `FileInfoTimeoutMs` does not regress steady-state cache hit rate for typical workloads. We will spot-check with the existing benchmarks before archiving.
