# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build
dotnet build

# Run (debug, from src/RamDrive.Cli)
dotnet run --project src/RamDrive.Cli -- --RamDrive:MountPoint="R:\\" --RamDrive:CapacityMb=64

# Run tests (unit + integration — requires WinFsp installed)
dotnet test

# AOT publish (requires Visual Studio C++ Build Tools + vswhere in PATH)
dotnet publish src/RamDrive.Cli/RamDrive.Cli.csproj -c Release -r win-x64 -o ./publish-aot
```

**Prerequisites:** .NET 10 SDK, [WinFsp](https://winfsp.dev/rel/) 2.x (install with Developer files).

## Architecture

```
WinFspRamAdapter (IFileSystem — all WinFsp callbacks, zero managed heap alloc on hot path)
    │
    ▼
RamFileSystem (directory tree, path resolution, global structure lock)
    │
    ▼
FileNode (file/dir metadata + PagedFileContent for files)
    │
    ▼
PagedFileContent (per-file nint[] page table + ReaderWriterLockSlim)
    │
    ▼
PagePool (NativeMemory.AllocZeroed + ConcurrentStack<nint> free list)
```

The WinFsp binding is provided by the [`WinFsp.Native`](https://www.nuget.org/packages/WinFsp.Native) NuGet package (namespace `WinFsp.Native`), which offers:
- `FileSystemHost` — high-level host bridging `IFileSystem` to native WinFsp
- `WinFspFileSystem` — low-level API for direct function pointer manipulation
- Full AOT-compatible P/Invoke layer with `[LibraryImport]` source generators

**All file data lives in NativeMemory (outside GC heap).** This is the core design decision — zero GC pressure for I/O operations.

## Key Design Decisions

### PagePool (Memory/PagePool.cs)
- Fixed-size pages (default 64KB) allocated via `NativeMemory.AllocZeroed`
- `ConcurrentStack<nint>` as lock-free LIFO free list
- `RentBatch`/`ReturnBatch` use `TryPopRange`/`PushRange` for single-CAS multi-page operations
- Two modes: on-demand allocation (lazy) or pre-allocate all pages at startup
- CAS loop for thread-safe capacity enforcement without locks

### PagedFileContent (Memory/PagedFileContent.cs)
- `nint[]` page table — index maps to native page pointer, `nint.Zero` = sparse (unallocated)
- **Three-phase write** to minimize write-lock hold time:
  1. Read lock: scan which pages need allocation
  2. No lock: unreserve own reservations (from `SetLength`), then batch-allocate pages from PagePool. Re-reserve on failure. (TLA+ verified — see `tla/PagePoolFixed.tla`)
  3. Write lock: assign page table entries + memcpy only
- `ReaderWriterLockSlim` per file — concurrent reads don't block each other
- Truncation zeros partial page data and batch-returns freed pages

### RamFileSystem (FileSystem/RamFileSystem.cs)
- Single global `_structureLock` for all tree mutations (create/delete/move)
- `Dictionary<string, FileNode>` with `StringComparer.OrdinalIgnoreCase` for Windows paths
- Path format: backslash separated, root is `"\"`

### WinFspRamAdapter (RamDrive.Cli/WinFspRamAdapter.cs)
- Implements `WinFsp.Native.IFileSystem` for use with `FileSystemHost`
- Caches `FileNode` in `FileOperationInfo.Context` to avoid repeated path lookups
- Deletion is deferred: `CanDelete` validates; actual removal in `Cleanup` when `CleanupFlags.Delete`
- `WriteFile`: `writeToEndOfFile` flag means append; `constrainedIo` clamps to logical size
- `GetVolumeInfo`: reports "NTFS" with `PersistentAcls | CasePreservedNames | UnicodeOnDisk`
- All hot-path methods return synchronous `ValueTask<T>` — zero managed heap allocation
- Timestamps use FILETIME (ulong) via `DateTime.ToFileTimeUtc()`

## WinFsp Notes

- `fileSystemName` must be `"NTFS"` for elevated process compatibility.
- `GetFileSecurityByName` returns `securityDescriptor = null` (sdSize=0) — WinFsp skips access checks.
- `ReadDirectory` uses native buffer with `WinFspFileSystem.AddDirInfo` / `EndDirInfo` — no IEnumerable allocation.
- `SetMountPointEx` crashes on `"R:\"` (trailing backslash). Mount point format: `"R:"` = `DefineDosDevice` (invisible to disk tools), `"\\.\R:"` = Mount Manager (visible to all apps, requires admin or `MountUseMountmgrFromFSD=1` registry key). `WinFspHostedService` tries `\\.\R:` first, falls back to `R:` on failure.
- **STATUS_PENDING async**: Must build a fresh stack-allocated `FspTransactRsp` with `Size`/`Kind`/`Hint` fields. Do NOT reuse `OperationContext->Response` — it may be invalidated after returning `STATUS_PENDING`. Save `Request->Hint` before returning, echo it back in the response.
- **Test mounts must use UNC paths** (`host.Prefix = @"\winfsp-tests\name"`; `host.Mount(null)`), not drive letters. Drive letter mounts become zombie on process crash and hang Explorer/entire system.
- Detailed WinFsp binding design documented in the [`winfsp-native`](https://github.com/hooyao/winfsp-native) repository.

## Testing

### Unit Tests (`tests/RamDrive.Core.Tests/`)
Standard xUnit tests for core data structures (PagePool, PagedFileContent, RamFileSystem).

### Integration Tests (`tests/RamDrive.IntegrationTests/`)
Self-hosted WinFsp integration tests. The test fixture boots its own `FileSystemHost` with UNC mount (`\\winfsp-tests\itest-{pid}`) — no external drive letter needed, safe on crash.

```bash
# Run all (structured torture + chaos fuzzer, ~45s)
dotnet test tests/RamDrive.IntegrationTests

# Run only chaos fuzzer
dotnet test tests/RamDrive.IntegrationTests --filter ChaosTests

# Long soak run (12 hours, 64 workers)
CHAOS_DURATION_SEC=43200 CHAOS_WORKERS=64 dotnet test tests/RamDrive.IntegrationTests --filter ChaosTests
```

**TortureTests** — 10 structured concurrent tests:
Sequential write+read, random offset R/W, concurrent append, create/delete churn, directory tree stress, overwrite+truncate+extend, mid-operation cancellation, rename under load, capacity pressure, mixed workload (100 tasks). All verify data integrity byte-by-byte.

**ChaosTests** — random fuzzer:
32 worker threads randomly pick from 14 weighted FS operations (CreateFile, WriteSeek, WriteAppend, ReadVerify, Truncate, Extend, Overwrite, Delete, Rename, SetAttr, Flush, CreateDir, DeleteDir, ListDir). Every write is tracked with SHA256; every read is verified. Duration configurable via `CHAOS_DURATION_SEC` env var (default 30s). Prints live ops/sec stats every 5s.

### Benchmarks (`tests/RamDrive.Benchmarks/`)
BenchmarkDotNet performance tests simulating the full `FileSystemHost` Read/Write call chain.

```bash
dotnet run --project tests/RamDrive.Benchmarks -c Release -- onread   # I/O throughput
dotnet run --project tests/RamDrive.Benchmarks -c Release -- e2e      # end-to-end via mounted FS
```

## Formal Verification (`tla/`)

TLA+ models for the PagePool reservation protocol, verified with TLC model checker.

**Background:** Kernel cache mode (`EnableKernelCache=true`) causes Windows to call `SetFileSize` (which reserves pages via `PagePool.Reserve`) before issuing `WriteFile` (which allocates pages via `PagePool.Rent`). The original code had `AllocateNewPageIfUnderCapacity` check `allocated + reserved >= maxPages`, which counted the caller's own reservations against it — causing spurious `STATUS_DISK_FULL` under concurrent file copies.

```bash
# Requires Java 11+
# Reproduce the bug (invariant NoSpuriousDiskFull violated)
java -jar tla/tla2tools.jar tla/PagePoolReservation.tla

# Verify the fix (all invariants + liveness pass)
java -jar tla/tla2tools.jar tla/PagePoolFixed.tla
```

- `tla/PagePoolReservation.tla` — buggy model: `NoSpuriousDiskFull` violated in 5 states
- `tla/PagePoolFixed.tla` — fixed model: Write Phase 2 unreserves own reservations before `RentBatch`, then re-reserves on failure. Verified with MaxPages={4,6,8} × NumFiles={2,3,4}, all invariants (TypeOK, PoolConsistent, NoCapacityLeak, NoSpuriousDiskFull) + liveness pass.

**Fix in code** (`PagedFileContent.Write` Phase 2): `Unreserve(min(fileReserved, needed))` before `RentBatch`, restore on failure. Phase 3 fallback `Rent()` path also unreserves one slot if available. This matches the TLA+ `PagePoolFixed.DoPhase2` spec.

## AOT Configuration

The project uses Native AOT compilation. Key settings in `RamDrive.Cli.csproj`:
- `OptimizationPreference=Speed` — favor throughput over binary size
- `InvariantGlobalization=true` — no ICU data needed
- `IlcFoldIdenticalMethodBodies=true` — reduce binary size
- `ServerGarbageCollection=true` + `RetainVMGarbageCollection=true` — GC tuned for throughput

Release-only flags (in `RamDrive.Cli.csproj` under `Release` condition):
- `IlcInstructionSet=x86-64-v3,aes` — x86-64-v3 profile (avx2+bmi1+bmi2+fma+lzcnt+movbe) plus AES-NI
- `StackTraceSupport=false` — smaller binary, no debug stack traces

**Serilog was intentionally removed** in favor of `Microsoft.Extensions.Logging` + `SimpleConsole` to maintain AOT compatibility. Do not re-add Serilog.

## Windows Service

The same `RamDrive.exe` runs as both a console app and a Windows Service. `UseWindowsService()` auto-detects the execution context. Console mode uses `SimpleConsole` logging; service mode additionally writes to Windows EventLog (`Application` log, source `RamDrive`).

```powershell
# Register (one-time, admin)
sc.exe create RamDrive binPath= "C:\path\to\RamDrive.exe" start= auto
sc.exe failure RamDrive reset= 60 actions= restart/5000/restart/10000/restart/30000

# Unregister
sc.exe stop RamDrive & sc.exe delete RamDrive
```

The installer handles registration/unregistration automatically.

## Installer (`setup/RamDrive.iss`)

Inno Setup 6.7+ script that produces a single `RamDrive-X.Y.Z-setup.exe`. Requires [Inno Setup](https://jrsoftware.org/isinfo.php) and the WinFsp MSI in `setup/`.

```bash
# Local build (requires Inno Setup installed, WinFsp MSI in setup/, AOT output in publish-aot/)
ISCC.exe setup/RamDrive.iss
# Output: installer-output/RamDrive-{version}-setup.exe
```

**Three install types:** Full (RamDrive + WinFsp + Windows Service), Green/Portable (exe only), Custom.

**Wizard features:**
- Drive letter dropdown (D:–Z:) and capacity spin edit (16 MB–128 GB)
- Bundles and silently installs WinFsp MSI if not already present
- Sets `MountUseMountmgrFromFSD=1` registry key for non-admin Mount Manager mounts
- Registers Windows Service via `{sysnative}\sc.exe` (bypasses WOW64 redirect in 32-bit installer)
- Start Menu shortcuts: app, Edit Configuration (notepad → appsettings.jsonc), Restart Service

**Service registration details:**
- `StopAndDeleteService` polls `sc.exe query` until SCM fully removes the old service before creating a new one — avoids the Windows race where a pending-delete service shadows the new creation.
- Service starts immediately in `CurStepChanged(ssPostInstall)` via `[Code]`, not `[Run]` section (the `[Run]` `{sysnative}` expansion was unreliable).
- `FSFilter Activity Monitor` service group for early boot ordering.
- Failure recovery: restart after 5s / 10s / 30s.

**Uninstall:** stops service, deletes via SCM, kills lingering `RamDrive.exe` process, then removes files.

**CI integration:** The release workflow (`release.yml`) downloads the WinFsp MSI, installs Inno Setup via Chocolatey, patches the version, compiles, and uploads both `.zip` and `-setup.exe` to the GitHub Release.

## Release Process

Push a tag matching `release-X.Y.Z` to trigger the release workflow:
```bash
git tag release-1.0.0
git push origin release-1.0.0
```

The workflow builds AOT, packages `RamDrive.exe` + `appsettings.jsonc`, builds the Inno Setup installer, and creates a GitHub Release with both the zip and setup exe.

## Configuration

All settings in `appsettings.jsonc` under `"RamDrive"` section, overridable via CLI `--RamDrive:Key=Value`:

| Key | Default | Notes |
|-----|---------|-------|
| MountPoint | `R:\` | Drive letter |
| CapacityMb | 512 | Total RAM disk size |
| PageSizeKb | 64 | Page granularity (try 256 for large-file workloads) |
| PreAllocate | false | true = allocate all memory at startup |
| VolumeLabel | RamDrive | Explorer display name |
| EnableKernelCache | true | Let Windows kernel cache file data; improves cached I/O throughput ~3x |
