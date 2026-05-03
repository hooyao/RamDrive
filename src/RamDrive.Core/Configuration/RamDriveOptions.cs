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
    /// <para>The adapter sends <c>FspFileSystemNotify</c> calls after every path-mutating
    /// operation (rename, delete, overwrite, create, set-size, set-attrs) so the cache is
    /// invalidated explicitly. This timeout is defence in depth for any path that escapes
    /// the notification matrix.</para>
    ///
    /// <para>Special values:
    /// <list type="bullet">
    /// <item><c>0</c> — cache disabled (same effect as <see cref="EnableKernelCache"/>=false).</item>
    /// <item><c>uint.MaxValue</c> (4294967295) — cache effectively permanent. Correctness
    /// depends entirely on the notification matrix; the integration test fixture pins this
    /// value to catch regressions.</item>
    /// </list></para>
    /// </summary>
    public uint FileInfoTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// Tree of directories to create at the root of the RAM disk after mounting.
    /// JSON keys are directory names; nested objects define subdirectories.
    /// Example: <c>{ "Temp": {}, "Cache": { "App1": {} } }</c>
    /// </summary>
    public DirectoryNode? InitialDirectories { get; set; }
}
