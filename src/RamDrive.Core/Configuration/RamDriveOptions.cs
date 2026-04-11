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
    /// </summary>
    public bool EnableKernelCache { get; set; } = true;

    /// <summary>
    /// Tree of directories to create at the root of the RAM disk after mounting.
    /// JSON keys are directory names; nested objects define subdirectories.
    /// Example: <c>{ "Temp": {}, "Cache": { "App1": {} } }</c>
    /// </summary>
    public DirectoryNode? InitialDirectories { get; set; }
}
