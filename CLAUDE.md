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

**Prerequisites:** .NET 10 SDK, [Dokany](https://github.com/dokan-dev/dokany/releases) v2.3.x driver.

## Architecture

```
DokanRamAdapter (IDokanOperations2 — all Dokan callbacks)
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

### DokanRamAdapter (FileSystem/DokanRamAdapter.cs)
- Caches `FileNode` in `DokanFileInfo.Context` to avoid repeated path lookups
- Deletion is deferred: `DeleteFile`/`DeleteDirectory` just validate; actual removal in `Cleanup` when `info.DeletePending == true`
- `WriteFile`: offset == -1 means append
- `ReadFile`/`WriteFile` with `PagingIo`: must clamp to logical file size
- `GetVolumeInformation`: reports "NTFS" with `PersistentAcls | SupportsRemoteStorage | CasePreservedNames | UnicodeOnDisk`

## Dokan Compatibility Notes

- **Do NOT add `NamedStreams` to FileSystemFeatures.** Dokan/dokany has incompatibilities with this flag that cause mount failures ("not accessible" or "File Too Large" errors). The official Mirror sample also omits it.
- **Explorer "properties can't be copied" warning** is expected behavior when copying files with Alternate Data Streams (e.g., Zone.Identifier from downloaded files). This is the same behavior as copying to FAT32. The copy itself succeeds fine.
- `GetFileSecurity` returns empty `FileSecurity`/`DirectorySecurity` (not `NotImplemented`) to avoid additional Explorer warnings.
- `FindStreams` returns `NotImplemented` (matches Mirror sample).
- `fileSystemName` must be `"NTFS"` for elevated process compatibility (Dokany #947).

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
