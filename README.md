# RamDrive

A high-performance RAM disk for Windows, built with [WinFsp](https://winfsp.dev/) and .NET 10. Mounts a virtual drive backed entirely by system memory for maximum I/O throughput.

## Performance

Benchmarked with ATTO Disk Benchmark (Queue Depth 4):

| I/O Size | Write (Direct I/O) | Read (Direct I/O) | Read (Cached) |
|----------|--------------------|--------------------|---------------|
| 64 KB | 3.74 GB/s | 4.67 GB/s | ~9.5 GB/s |
| 256 KB | **6.47 GB/s** | **9.31 GB/s** | ~9.5 GB/s |
| 1 MB | 4.48 GB/s | 3.51 GB/s | ~9.5 GB/s |

With `EnableKernelCache: true` (default), cached reads match kernel-mode ImDisk performance (~9.5 GB/s).

## Architecture

```
WinFspRamAdapter (IFileSystem — all WinFsp callbacks, zero managed heap alloc on hot path)
    │
    ▼
RamFileSystem (path resolution, directory tree)
    │
    ▼
PagedFileContent (per-file page table: nint[])
    │
    ▼
PagePool (NativeMemory + ConcurrentStack<nint>)
```

**Key design decisions:**

- **NativeMemory page pool** -- all file data stored outside the GC heap via `NativeMemory.AllocZeroed`, zero GC pressure
- **Lock-free page allocation** -- `ConcurrentStack<nint>` with batch `TryPopRange`/`PushRange` for O(1) rent/return
- **Per-file ReaderWriterLockSlim** -- concurrent reads don't block each other
- **Sparse files** -- pages allocated on demand; unwritten regions consume no memory
- **Write-lock minimization** -- pages pre-allocated outside the lock, only memcpy inside
- **Native AOT compiled** -- single-file executable, no .NET runtime required

## Quick Start

**Option A: Installer (recommended)**

1. Download `RamDrive-X.Y.Z-setup.exe` from [Releases](https://github.com/hooyao/RamDrive/releases)
2. Run the installer — it bundles WinFsp, configures the drive letter and capacity, and registers a Windows Service
3. The RAM disk starts automatically on boot

**Option B: Portable**

1. Download the `.zip` from [Releases](https://github.com/hooyao/RamDrive/releases) and extract
2. Install [WinFsp](https://winfsp.dev/rel/) 2.x manually
3. Run `RamDrive.exe` — press `Ctrl+C` to unmount

### Configuration

Edit `appsettings.jsonc` or override via command line (`--RamDrive:CapacityMb=4096`):

```jsonc
{
  "RamDrive": {
    "MountPoint": "R:\\",           // Drive letter
    "CapacityMb": 2048,             // Total capacity in MB
    "PageSizeKb": 64,               // Page size (64 KB default, try 256 for large files)
    "PreAllocate": false,           // true = allocate all memory at startup
    "VolumeLabel": "RamDrive",      // Volume label in Explorer
    "EnableKernelCache": true,      // Kernel page cache (~3x read throughput)
    "InitialDirectories": {         // Directories created on mount
      "Temp": {}                    //   e.g. { "Temp": {}, "Cache": { "App1": {} } }
    }
  }
}
```

## Formal Verification

The core concurrency protocol is formally verified with [TLA+](https://lamport.azurewebsted.net/tla/tla.html) and the TLC model checker.

**What's verified (`tla/RamDiskSystem.tla`):**

| Property | Meaning |
|----------|---------|
| PoolConsistent | Pool accounting: `rented <= allocated <= maxPages` |
| NoPageLeak | Every rented page is accounted for (in a file or in-transit) |
| FreeBytesAccurate | `FreeBytes = Capacity - RentedPages` — never polluted by intermediate state |
| DataIntegrity | No file ever contains another file's data |
| ReadConsistent | Reads always return data belonging to the file being read |
| DeadFilesClean | Deleted files hold no pages |
| WriteTerminates | Every write eventually completes or fails (liveness) |

**Verified configurations:**

| Config | Pages | Files | Distinct States | Result |
|--------|-------|-------|----------------|--------|
| Minimal | 3 | 2 | 3,163,692 | All pass |
| Standard | 4 | 2 | 66,190,728 | All pass |

The model covers: 3-phase write (scan/rent/assign), concurrent reads, sparse extend, truncate, delete, file creation, `SetAllocationSize` TOCTOU check, and `GetVolumeInfo` as an external observer.

**Historical context:** An earlier narrow model (`PagePoolFixed.tla`) only verified the PagePool reserve/rent protocol. It missed a system-level bug where `Reserve()` in `SetLength` polluted `FreeBytes`, causing stale metadata under high concurrency. The full-system model (`RamDiskSystem.tla`) was built to prevent such gaps.

## Building from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and [WinFsp](https://winfsp.dev/rel/) 2.x (install with Developer files).

```bash
# Regular build
dotnet build

# AOT publish (requires Visual Studio C++ Build Tools)
dotnet publish src/RamDrive.Cli/RamDrive.Cli.csproj -c Release -r win-x64 -o ./publish-aot
```

## Release

Push a tag matching `release-X.Y.Z` to trigger the CI pipeline, which builds, tests, and creates a GitHub Release with the AOT-compiled binary.

## License

MIT

This project uses [WinFsp - Windows File System Proxy](https://github.com/winfsp/winfsp), Copyright (C) Bill Zissimopoulos, under the GPLv3 with FLOSS exception.
