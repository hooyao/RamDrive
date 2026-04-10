namespace RamDrive.Core.Configuration;

public sealed class RamDriveOptions
{
    public string MountPoint { get; set; } = @"R:\";
    public long CapacityMb { get; set; } = 512;
    public int PageSizeKb { get; set; } = 64;
    public bool PreAllocate { get; set; }
    public string VolumeLabel { get; set; } = "RamDrive";

    /// <summary>
    /// Enable Windows kernel-level file data caching (sets WinFsp FileInfoTimeout=MAX).
    /// Improves throughput (~3x) by letting the OS cache manager serve repeated reads
    /// without calling back into user mode.
    /// Warning: under high concurrency, WinFsp's metadata cache may return stale file sizes
    /// because the lazy writer has not yet flushed dirty pages to user mode. This does not
    /// corrupt data but can cause readers to see an outdated file size briefly.
    /// </summary>
    public bool EnableKernelCache { get; set; }

    /// <summary>
    /// Create a Temp directory at the root of the RAM disk after mounting.
    /// Useful for redirecting TEMP/TMP environment variables to the RAM disk.
    /// </summary>
    public bool CreateTempDirectory { get; set; }
}
