using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using WinFsp.Native;

namespace RamDrive.IntegrationTests;

/// <summary>
/// Shared fixture: boots a WinFsp mount via UNC path, shared across all tests in
/// the collection. Uses the production <see cref="WinFspRamAdapter"/> directly so
/// integration tests cover the real adapter, not a parallel test-only port.
///
/// Set <c>RAMDRIVE_DIFF=1</c> to wrap the adapter in
/// <see cref="RamDrive.Diagnostics.DifferentialChecker.DifferentialAdapter"/>
/// against <see cref="RamDrive.Diagnostics.MemfsReference.MemfsReferenceFs"/>.
///
/// Set <c>RAMDRIVE_TRACE_PATH=&lt;substring&gt;</c> to enable
/// <see cref="RamDrive.Core.Diagnostics.FsTracer"/> for matching paths
/// (output goes to <c>%TEMP%\ramdrive_trace.log</c> by default).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RamDriveFixture : IDisposable
{
    public string Root { get; private set; } = string.Empty;
    public long CapacityMb { get; } = 512;

    private PagePool _pool = null!;
    private RamFileSystem _fs = null!;
    private FileSystemHost _host = null!;
    private int _generation;
    private bool _disposed;

    public RamDriveFixture() => Mount();

    /// <summary>
    /// Each serialized test starts with its own volume. Clean kernel cache references
    /// can retain deleted backing objects until Close; they must not consume the next
    /// test's capacity or require premature disposal to make the suite pass.
    /// </summary>
    public RamDriveFixture ResetForTest()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopMount();
        Mount();
        return this;
    }

    private void Mount()
    {
        var options = new RamDriveOptions
        {
            CapacityMb = CapacityMb,
            PageSizeKb = 64,
            EnableKernelCache = true,
            // Pin to worst-case (permanent) cache lifetime so missing FspFileSystemNotify
            // calls produce stale-cache test failures in CI rather than only against
            // real Chromium with the production default. See specs/cache-invalidation.
            FileInfoTimeoutMs = uint.MaxValue,
            VolumeLabel = "IntegrationTest",
        };

        _pool = new PagePool(new OptionsWrapper<RamDriveOptions>(options), NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);
        IFileSystem adapter = new WinFspRamAdapter(
            _fs, new OptionsWrapper<RamDriveOptions>(options), NullLogger<WinFspRamAdapter>.Instance);

        // Opt-in: RAMDRIVE_DIFF=1 wraps the production adapter with the
        // Diagnostics.DifferentialChecker against MemfsReferenceFs. Throws on
        // any divergence between the two file systems.
        if (Environment.GetEnvironmentVariable("RAMDRIVE_DIFF") == "1")
        {
            var reference = new RamDrive.Diagnostics.MemfsReference.MemfsReferenceFs(CapacityMb);
            adapter = new RamDrive.Diagnostics.DifferentialChecker.DifferentialAdapter(adapter, reference);
        }

        _host = new FileSystemHost(adapter);
        string share = $"itest-{Environment.ProcessId}-{++_generation}";
        _host.Prefix = $@"\winfsp-tests\{share}";

        int result = _host.Mount(null);
        if (result < 0)
            throw new InvalidOperationException($"WinFsp mount failed: 0x{result:X8}. Is WinFsp installed?");

        Root = $@"\\winfsp-tests\{share}\";
    }

    public void CleanRoot()
    {
        foreach (var d in Directory.GetDirectories(Root))
            try { Directory.Delete(d, true); } catch { }
        foreach (var f in Directory.GetFiles(Root))
            try { File.Delete(f); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopMount();
    }

    private void StopMount()
    {
        _host?.Dispose();
        _fs?.Dispose();
        _pool?.Dispose();
    }
}

[CollectionDefinition("RamDrive")]
public class RamDriveCollection : ICollectionFixture<RamDriveFixture>;
