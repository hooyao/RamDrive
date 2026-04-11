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
  2. No lock: batch-allocate pages from PagePool via `RentBatch`. On failure return pages and report DISK_FULL.
  3. Write lock: assign page table entries + memcpy only. Race fallback: if a page was allocated by a concurrent writer between Phase 1 and 3, use single `Rent()`.
- **Sparse SetLength**: extending a file only expands the page table (`nint.Zero` entries); no pages are reserved or allocated. Pages are allocated on demand in Write.
- Full protocol verified with TLA+ — see `tla/RamDiskSystem.tla`.
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
- `GetFileSecurityByName` returns the file's stored security descriptor. Root default: `O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)`. `GetFileSecurity`/`SetFileSecurity` implemented via `RawSecurityDescriptor` merge.
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

TLA+ models verified with [TLC model checker](https://github.com/tlaplus/tlaplus). Requires Java 11+.

### Downloading TLC

```bash
# Download tla2tools.jar into tla/ (one-time, ~4 MB)
curl -sL -o tla/tla2tools.jar https://github.com/tlaplus/tlaplus/releases/download/v1.8.0/tla2tools.jar
```

### Full-system model (`tla/RamDiskSystem.tla`)

Models the complete concurrency protocol: PagePool accounting, per-file sparse page tables, 3-phase write, concurrent Read, CreateFile, Extend, Truncate, Delete, SetAllocationSize TOCTOU check, and GetVolumeInfo as an external observer.

**Verified invariants:** PoolConsistent, NoPageLeak, FreeBytesAccurate, DataIntegrity, ReadConsistent, DeadFilesClean, SingleFileCap. **Liveness:** WriteTerminates.

```bash
# Smoke test (3 pages × 2 files, ~12 min, ~3M states)
java -XX:+UseParallelGC -jar tla/tla2tools.jar -workers auto \
     -config tla/RamDiskSystem_Minimal.cfg tla/RamDiskSystem.tla

# Full verification (4 pages × 2 files, ~5h, ~66M states, needs ~60GB RAM for liveness)
java -XX:+UseParallelGC -Xmx100g -jar tla/tla2tools.jar -workers auto \
     -config tla/RamDiskSystem_Standard.cfg tla/RamDiskSystem.tla
```

### How the model maps to code

| TLA+ Action | Code | Lock |
|-------------|------|------|
| `DoWriteP1(f, pages)` | `PagedFileContent.Write` Phase 1 | Read-lock |
| `DoWriteP2(f)` | `PagedFileContent.Write` Phase 2 | None (CAS) |
| `DoWriteP3(f)` | `PagedFileContent.Write` Phase 3 | Write-lock |
| `DoExtend(f, newLen)` | `PagedFileContent.SetLength` (extend) | Write-lock |
| `DoTruncate(f, newLen)` | `PagedFileContent.SetLength` (truncate) | Write-lock |
| `DoDelete(f)` | `PagedFileContent.Dispose` | Write-lock |
| `DoRead(f, p)` | `PagedFileContent.Read` | Read-lock |
| `DoCreateFile(f)` | `RamFileSystem.CreateFile` | Structure-lock |
| `DoSetAllocSize(f, n)` | `WinFspRamAdapter.SetFileSize` (alloc=true) | None |
| `DoGetVolumeInfo` | `WinFspRamAdapter.GetVolumeInfo` | None |

### Modeling guidelines

When modifying the concurrency protocol, update the TLA+ model:

1. **Add new actions** for any operation that mutates pool state (`poolAllocated`, `poolRented`) or per-file page tables.
2. **Model at page granularity** — bytes are unnecessary; data tags (`0` = unallocated, `f` = file f's data) suffice.
3. **External observers matter** — the old model missed a bug because it didn't include `GetVolumeInfo` reading `FreeBytes`. Any state read by WinFsp callbacks should be an observable action.
4. **CAS loops are atomic** — model `Interlocked.CompareExchange` loops as single atomic steps (linearizable).
5. **Locks = atomicity boundaries** — read-lock and write-lock regions each become one TLA+ action.
6. **Run Minimal first** (~12 min) to catch bugs fast, then Standard (~5h) for full coverage.

### Historical models

- `tla/PagePoolReservation.tla` — original buggy model: `NoSpuriousDiskFull` violated
- `tla/PagePoolFixed.tla` — narrow fix for the reserve/rent double-counting bug

These are preserved as historical artifacts. The narrow scope of `PagePoolFixed.tla` missed the `FreeBytes` pollution bug (Reserve in SetLength changed `_reservedCount`, which changed `FreeBytes` reported by `GetVolumeInfo`, causing stale metadata). The full-system model `RamDiskSystem.tla` was built to prevent such scope gaps.

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
| EnableKernelCache | true | Kernel page cache (~3x throughput). Uses WinFsp `FileInfoTimeout=MAX`. |
| InitialDirectories | `{}` | Tree of directories to create on mount (e.g. `{ "Temp": {} }`) |
