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

1. Download the latest release from [Releases](https://github.com/hooyao/RamDrive/releases) and unzip
2. **Right-click `Setup.bat` → Run as administrator** (one-time setup: installs WinFsp and configures the system)
3. Double-click `RamDrive.exe` — no admin required from now on
4. Press `Ctrl+C` to unmount

### What does Setup.bat do?

- Installs [WinFsp](https://winfsp.dev/) if not already present (silent install, no reboot)
- Configures WinFsp to use kernel-mode Mount Manager (so the drive is visible to all apps including ATTO, CrystalDiskMark, etc.)
- Only needs to be run once — after that, `RamDrive.exe` works without admin

### Configuration

Edit `appsettings.jsonc` or override via command line (`--RamDrive:CapacityMb=4096`):

```jsonc
{
  "RamDrive": {
    "MountPoint": "R:\\",         // Drive letter
    "CapacityMb": 2048,           // Total capacity in MB
    "PageSizeKb": 64,             // Page size (64 KB default, try 256 for large files)
    "PreAllocate": false,         // true = allocate all memory at startup
    "VolumeLabel": "RamDrive",    // Volume label in Explorer
    "EnableKernelCache": true     // Windows kernel page cache (~3x read throughput)
  }
}
```

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
