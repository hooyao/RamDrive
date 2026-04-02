# RamDrive

A high-performance RAM disk for Windows, built with [dokan-dotnet](https://github.com/dokan-dev/dokan-dotnet) and .NET 10. Mounts a virtual drive backed entirely by system memory for maximum I/O throughput.

## Performance

Benchmarked with ATTO Disk Benchmark (Queue Depth 4, Direct I/O):

| I/O Size | Write | Read |
|----------|-------|------|
| 4 KB | 383 MB/s | 390 MB/s |
| 64 KB | 3.74 GB/s | 4.67 GB/s |
| 256 KB | **6.47 GB/s** | **9.31 GB/s** |
| 1 MB | 4.48 GB/s | 3.51 GB/s |

## Architecture

```
DokanRamAdapter (IDokanOperations2)
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
- **Native AOT compiled** -- 5.4 MB single-file executable, no .NET runtime required

## Prerequisites

- **Windows 10/11** (x64)
- **[Dokany](https://github.com/dokan-dev/dokany/releases)** v2.3.x -- the user-mode file system driver

## Usage

1. Install Dokany from the link above
2. Download `RamDrive.exe` and `appsettings.jsonc` from [Releases](https://github.com/hooyao/RamDrive/releases)
3. Edit `appsettings.jsonc` to configure capacity and mount point
4. Run `RamDrive.exe` (requires administrator privileges for drive mounting)
5. Press `Ctrl+C` to unmount

### Configuration

Edit `appsettings.jsonc` or override via command line (`--RamDrive:CapacityMb=4096`):

```jsonc
{
  "RamDrive": {
    "MountPoint": "R:\\",       // Drive letter
    "CapacityMb": 2048,         // Total capacity in MB
    "PageSizeKb": 64,           // Page size (64 KB default, try 256 for large files)
    "PreAllocate": false,       // true = allocate all memory at startup
    "VolumeLabel": "RamDrive"   // Volume label in Explorer
  }
}
```

## Building from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

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
