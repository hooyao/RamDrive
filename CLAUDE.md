# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build
dotnet build

# Run (debug, from src/RamDrive.Cli)
dotnet run --project src/RamDrive.Cli -- --RamDrive:MountPoint="R:\\" --RamDrive:CapacityMb=64

# Run tests
dotnet test

# AOT publish (requires Visual Studio C++ Build Tools + vswhere in PATH)
dotnet publish src/RamDrive.Cli/RamDrive.Cli.csproj -c Release -r win-x64 -o ./publish-aot

# Functional test: start RamDrive in background, then operate on the mount point
dotnet run --project src/RamDrive.Cli -- --RamDrive:MountPoint="R:\\" --RamDrive:CapacityMb=64 &
sleep 8
echo "test" > R:/test.txt && cat R:/test.txt && rm R:/test.txt
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

The WinFsp binding library (`src/WinFsp.Net/`) provides:
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
  2. No lock: batch-allocate pages from PagePool
  3. Write lock: assign page table entries + memcpy only
- `ReaderWriterLockSlim` per file — concurrent reads don't block each other
- Truncation zeros partial page data and batch-returns freed pages

### RamFileSystem (FileSystem/RamFileSystem.cs)
- Single global `_structureLock` for all tree mutations (create/delete/move)
- `Dictionary<string, FileNode>` with `StringComparer.OrdinalIgnoreCase` for Windows paths
- Path format: backslash separated, root is `"\"`

### WinFspRamAdapter (RamDrive.Cli/WinFspRamAdapter.cs)
- Implements `WinFsp.IFileSystem` for use with `FileSystemHost`
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
- Detailed WinFsp binding design and pitfalls documented in `docs/winfsp-net-redesign.md`.

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

## Release Process

Push a tag matching `release-X.Y.Z` to trigger the release workflow:
```bash
git tag release-1.0.0
git push origin release-1.0.0
```

The workflow builds AOT, packages `RamDrive.exe` + `appsettings.jsonc`, and creates a GitHub Release.

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
