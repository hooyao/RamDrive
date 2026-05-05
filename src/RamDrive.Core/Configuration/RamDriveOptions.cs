namespace RamDrive.Core.Configuration;

public sealed class RamDriveOptions
{
    public string MountPoint { get; set; } = @"R:\";
    public long CapacityMb { get; set; } = 512;
    public int PageSizeKb { get; set; } = 64;
    public bool PreAllocate { get; set; }
    public string VolumeLabel { get; set; } = "RamDrive";

    /// <summary>
    /// Enable Windows kernel-level file data caching. When true, the WinFsp host's
    /// <c>FileInfoTimeout</c> is set to <see cref="FileInfoTimeoutMs"/>. When false the
    /// timeout is forced to 0 (no kernel cache, ~3× lower throughput) regardless of
    /// <see cref="FileInfoTimeoutMs"/> — this is the documented backout switch.
    /// </summary>
    public bool EnableKernelCache { get; set; } = true;

    /// <summary>
    /// Lifetime of the WinFsp kernel <c>FileInfo</c> cache, in milliseconds. Default 1000.
    ///
    /// <para>Cache coherence relies on every IFileSystem callback returning the correct
    /// post-operation <c>FspFileInfo</c> in its response (the kernel updates Cc with that
    /// FileInfo). <see cref="EnableNotifications"/> can be turned on for defence in depth
    /// during debugging — the production default is off because correct callback responses
    /// are sufficient.</para>
    ///
    /// <para>Special values:
    /// <list type="bullet">
    /// <item><c>0</c> — cache disabled (same effect as <see cref="EnableKernelCache"/>=false).</item>
    /// <item><c>uint.MaxValue</c> (4294967295) — cache effectively permanent. Correctness
    /// depends entirely on every callback returning a correct FileInfo; the integration
    /// test fixture pins this value to catch regressions.</item>
    /// </list></para>
    /// </summary>
    public uint FileInfoTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Send <c>FspFileSystemNotify</c> after every path-mutating callback (Create, Write,
    /// SetFileSize, SetFileAttributes, Rename, Delete) to proactively invalidate the WinFsp
    /// kernel <c>FileInfo</c> cache.
    ///
    /// <para>Default <c>false</c>. Cache coherence in normal operation is maintained by
    /// every IFileSystem callback returning the correct post-operation FileInfo in its
    /// <c>FsResult</c>; the kernel updates Cc directly from that. Notifications are
    /// redundant in that case and add a small kernel-IOCTL overhead per mutation.</para>
    ///
    /// <para>Set to <c>true</c> when debugging suspected cache-coherence regressions —
    /// notifications act as a belt-and-braces invalidation in case some callback
    /// accidentally returns a stale or empty FileInfo.</para>
    /// </summary>
    public bool EnableNotifications { get; set; }

    /// <summary>
    /// Tree of directories to create at the root of the RAM disk after mounting.
    /// JSON keys are directory names; nested objects define subdirectories.
    /// Example: <c>{ "Temp": {}, "Cache": { "App1": {} } }</c>
    /// </summary>
    public DirectoryNode? InitialDirectories { get; set; }
}
