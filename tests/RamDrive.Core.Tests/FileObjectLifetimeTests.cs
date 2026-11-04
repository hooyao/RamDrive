using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using WinFsp.Native;

namespace RamDrive.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class FileObjectLifetimeTests : IDisposable
{
    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;
    private readonly WinFspRamAdapter _adapter;

    public FileObjectLifetimeTests()
    {
        var options = Options.Create(new RamDriveOptions { CapacityMb = 8, PageSizeKb = 64 });
        _pool = new PagePool(options, NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);
        _adapter = new WinFspRamAdapter(_fs, options, NullLogger<WinFspRamAdapter>.Instance);
    }

    private async Task<FileOperationInfo> Create(string path, byte value)
    {
        var info = new FileOperationInfo();
        var created = await _adapter.CreateFile(path, 0, 0, 0, null, 0, info, default);
        created.Status.Should().Be(NtStatus.Success);
        (await _adapter.WriteFile(path, new byte[] { value }, 0, false, false, info, default))
            .Status.Should().Be(NtStatus.Success);
        return info;
    }

    private async Task<byte> Read(FileOperationInfo info)
    {
        var data = new byte[1];
        (await _adapter.ReadFile("\\file", data, 0, info, default)).Status.Should().Be(NtStatus.Success);
        return data[0];
    }

    [Fact]
    public async Task CleanupDelete_RetainsDataForOtherContexts_UntilTheirClose()
    {
        var first = await Create("\\file", 17);
        var second = new FileOperationInfo();
        (await _adapter.OpenFile("\\file", 0, 0, second, default)).Status.Should().Be(NtStatus.Success);
        _adapter.Cleanup("\\file", first, CleanupFlags.Delete);
        _fs.FindNode("\\file").Should().BeNull();
        _pool.RentedCount.Should().Be(1);

        // The surviving context keeps the unlinked data usable: paging I/O and metadata
        // work until ITS Cleanup releases the last reference.
        var write = await _adapter.WriteFile("\\file", new byte[] { 29 }, 0, false, true, second, default);
        write.Status.Should().Be(NtStatus.Success);
        (await Read(second)).Should().Be(29);
        (await _adapter.GetFileInformation("\\file", second, default)).FileInfo.FileSize.Should().Be(1);
        (await _adapter.FlushFileBuffers("\\file", second, default)).FileInfo.FileSize.Should().Be(1);

        _adapter.Cleanup("\\file", second, CleanupFlags.None);
        _pool.RentedCount.Should().Be(1);   // references released, but the pages survive the Cleanups
        _adapter.Close(first);
        _adapter.Close(second); // the last Close reclaims the unlinked node's pages
        _pool.RentedCount.Should().Be(0);
        _pool.ReservedCount.Should().Be(0);
        _adapter.Close(second); // An already-cleared context cannot release twice.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultipleContexts_KeepUnlinkedDataUntilLastClose(bool creatorClosesFirst)
    {
        var first = await Create("\\file", 31);
        var second = new FileOperationInfo();
        (await _adapter.OpenFile("\\file", 0, 0, second, default)).Status.Should().Be(NtStatus.Success);
        var early = creatorClosesFirst ? first : second;
        var last = creatorClosesFirst ? second : first;
        _adapter.Cleanup("\\file", early, CleanupFlags.Delete);
        _adapter.Close(early);
        _pool.RentedCount.Should().Be(1);
        (await Read(last)).Should().Be(31);
        _adapter.Cleanup("\\file", last, CleanupFlags.None);
        _adapter.Close(last);
        _pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public async Task StaleDelete_CannotDeleteARecreatedName()
    {
        var old = await Create("\\file", 11);
        var oldNode = WinFspRamAdapter.Node(old);
        var second = new FileOperationInfo();
        (await _adapter.OpenFile("\\file", 0, 0, second, default)).Status.Should().Be(NtStatus.Success);
        _adapter.Cleanup("\\file", old, CleanupFlags.Delete);   // unlinks; "second" retains the data
        var current = await Create("\\file", 22);
        var currentNode = _fs.FindNode("\\file");
        _fs.Delete(oldNode!).Should().BeFalse();               // stale delete of the old node is refused
        _fs.FindNode("\\file").Should().BeSameAs(currentNode);
        (await Read(second)).Should().Be(11);
        (await Read(current)).Should().Be(22);
        _adapter.Close(old);
        _pool.RentedCount.Should().Be(2);   // old data retained by "second" + the new file
        _adapter.Cleanup("\\file", current, CleanupFlags.Delete);
        _adapter.Close(current);
        _pool.RentedCount.Should().Be(1);   // only the old data remains
        _adapter.Cleanup("\\file", second, CleanupFlags.None);
        _adapter.Close(second);
        _pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public async Task Overwrite_RemovesClosedStreamsButRetainsASectionDeferredOne_UntilItsClose()
    {
        var baseInfo = await Create("\\base", 1);
        var deferred = new FileOperationInfo();
        (await _adapter.CreateFile("\\base:s", 0, 0, 0, null, 0, deferred, default)).Status.Should().Be(NtStatus.Success);
        (await _adapter.WriteFile("\\base:s", new byte[] { 5 }, 0, false, false, deferred, default)).Status.Should().Be(NtStatus.Success);
        var closed = new FileOperationInfo();
        (await _adapter.CreateFile("\\base:t", 0, 0, 0, null, 0, closed, default)).Status.Should().Be(NtStatus.Success);
        (await _adapter.WriteFile("\\base:t", new byte[] { 6 }, 0, false, false, closed, default)).Status.Should().Be(NtStatus.Success);
        _pool.RentedCount.Should().Be(3);

        // "t" is fully closed (Cleanup AND Close observed); "s" is cleaned but its Close
        // is still pending — the driver defers it while a Cc section / memory mapping
        // keeps the file node alive (memory-mapped I/O on a deleted file is legal).
        _adapter.Cleanup("\\base:t", closed, CleanupFlags.None);
        _adapter.Close(closed);
        _adapter.Cleanup("\\base:s", deferred, CleanupFlags.None);

        (await _adapter.OverwriteFile(0, false, 0, baseInfo, default)).Status.Should().Be(NtStatus.Success);
        // The base is truncated to 0 (its page released), "t" is reclaimed (fully
        // closed), and "s" is retained — its Close is still pending.
        _pool.RentedCount.Should().Be(1);   // only "s"'s page survives the overwrite

        _adapter.Close(deferred);
        _pool.RentedCount.Should().Be(0);   // "s" is reclaimed by its Close
    }

    [Fact]
    public async Task FirstCloseDoesNotReclaimWhileAnotherContextsCloseIsStillDeferred()
    {
        // The driver posts one Close transact per file object: a live Cc section /
        // memory mapping pins its owning file object (deferring that owner's Close)
        // while OTHER handles' Closes arrive early. The first Close must therefore not
        // reclaim an unlinked node — only the point at which every Close has arrived is
        // safe.
        var first = await Create("\\file", 17);
        var second = new FileOperationInfo();
        (await _adapter.OpenFile("\\file", 0, 0, second, default)).Status.Should().Be(NtStatus.Success);
        _adapter.Cleanup("\\file", second, CleanupFlags.None);   // second cleaned; Close deferred (section alive)
        _adapter.Cleanup("\\file", first, CleanupFlags.Delete);  // unlinks; no references left
        _adapter.Close(first);                                   // first's Close arrives while second's section is live
        _pool.RentedCount.Should().Be(1);                        // must NOT be reclaimed
        (await Read(second)).Should().Be(17);                    // late Cc I/O via second's context must work
        _adapter.Close(second);                                  // section gone → second's Close reclaims
        _pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public async Task UnmodifiedHandleReleasesItsReferenceInClose_SoOverwriteRemovesItsStream()
    {
        // The driver suppresses the Cleanup transact for unmodified handles
        // (PostCleanupWhenModifiedOnly): a read-only handle closes with no Cleanup at
        // all, so its reference must be released in Close — otherwise the stream would
        // look "open" forever and the overwrite sweep would skip it.
        var baseInfo = await Create("\\base", 1);
        var ro = new FileOperationInfo();
        (await _adapter.CreateFile("\\base:ro", 0, 0, 0, null, 0, ro, default)).Status.Should().Be(NtStatus.Success);
        (await _adapter.WriteFile("\\base:ro", new byte[] { 9 }, 0, false, false, ro, default)).Status.Should().Be(NtStatus.Success);
        _pool.RentedCount.Should().Be(2);

        _adapter.Close(ro);   // read-only close: no Cleanup transact, Close releases the reference

        (await _adapter.OverwriteFile(0, false, 0, baseInfo, default)).Status.Should().Be(NtStatus.Success);
        _pool.RentedCount.Should().Be(0);   // the base is truncated to 0 and "ro" is removed + reclaimed
    }

    [Fact]
    public async Task Replacement_PreservesDestinationContextsAndIgnoresTheirStaleCleanup()
    {
        var source = await Create("\\source", 41);
        var target = await Create("\\target", 52);
        var oldTarget = WinFspRamAdapter.Node(target);
        (await _adapter.MoveFile("\\source", "\\target", true, source, default)).Should().Be(NtStatus.Success);
        _fs.FindNode("\\source").Should().BeNull();
        _fs.FindNode("\\target").Should().BeSameAs(WinFspRamAdapter.Node(source));
        _fs.FindNode("\\target").Should().NotBeSameAs(oldTarget);
        (await Read(source)).Should().Be(41);
        (await Read(target)).Should().Be(52);
        _adapter.Cleanup("\\target", target, CleanupFlags.Delete);
        _adapter.Close(target);
        _fs.FindNode("\\target").Should().BeSameAs(WinFspRamAdapter.Node(source));
        _pool.RentedCount.Should().Be(1);
        _adapter.Cleanup("\\target", source, CleanupFlags.Delete);
        _adapter.Close(source);
        _pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public async Task CaseOnlyRename_PreservesIdentityAndContent()
    {
        var info = await Create("\\file", 63);
        var node = WinFspRamAdapter.Node(info);
        (await _adapter.MoveFile("\\file", "\\FILE", false, info, default)).Should().Be(NtStatus.Success);
        _fs.FindNode("\\FILE").Should().BeSameAs(node);
        (await Read(info)).Should().Be(63);
        _adapter.Cleanup("\\FILE", info, CleanupFlags.Delete);
        _adapter.Close(info);
        _pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public async Task Shutdown_ReclaimsLinkedAndRetainedNodes_AndAllowsLateContextRelease()
    {
        var unlinked = await Create("\\old", 1);
        var retained = new FileOperationInfo();
        (await _adapter.OpenFile("\\old", 0, 0, retained, default)).Status.Should().Be(NtStatus.Success);
        var linked = await Create("\\live", 2);
        _adapter.Cleanup("\\old", unlinked, CleanupFlags.Delete);  // unlinked, kept alive by "retained"
        _pool.RentedCount.Should().Be(2);
        _fs.Dispose();
        _pool.RentedCount.Should().Be(0);
        _pool.ReservedCount.Should().Be(0);
        _fs.Dispose();
        _adapter.Close(unlinked);   // the fs is already disposed; Close is a no-op
        _adapter.Close(retained);
        _adapter.Close(linked);
        _pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public async Task OpenAndDeleteRace_ReturnsAbsentOrRetainedUsableNode()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            string path = $"\\race{attempt}";
            var node = _fs.CreateFile(path)!;
            node.Content!.Write(0, new byte[] { 71 });
            FileNode? opened = null;
            await Task.WhenAll(
                Task.Run(() => opened = _fs.OpenNode(path)),
                Task.Run(() => _fs.Delete(path)));
            _fs.FindNode(path).Should().BeNull();
            if (opened != null)
            {
                var data = new byte[1];
                opened.Content!.Read(0, data).Should().Be(1);
                data[0].Should().Be(71);
                _fs.CloseNode(opened);
            }
            _pool.RentedCount.Should().Be(0);
        }
    }

    [Fact]
    public void ExceptionHandler_LogsTheFailureAndReturnsIoError()
    {
        var logger = new RecordingLogger();
        var adapter = new WinFspRamAdapter(_fs, Options.Create(new RamDriveOptions()), logger);
        var error = new InvalidOperationException("callback failure");
        adapter.ExceptionHandler(error).Should().Be(NtStatus.UnexpectedIoError);
        logger.Exception.Should().BeSameAs(error);
    }

    private sealed class RecordingLogger : ILogger<WinFspRamAdapter>
    {
        public Exception? Exception { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Exception = exception;
    }

    public void Dispose()
    {
        _fs.Dispose();
        _pool.Dispose();
    }
}
