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
    public async Task CleanupDelete_PreservesLatePagingIoAndMetadata_UntilFinalClose()
    {
        var info = await Create("\\file", 17);
        _adapter.Cleanup("\\file", info, CleanupFlags.Delete);
        _fs.FindNode("\\file").Should().BeNull();
        _pool.RentedCount.Should().Be(1);

        var write = await _adapter.WriteFile("\\file", new byte[] { 29 }, 0, false, true, info, default);
        write.Status.Should().Be(NtStatus.Success);
        (await Read(info)).Should().Be(29);
        (await _adapter.GetFileInformation("\\file", info, default)).FileInfo.FileSize.Should().Be(1);
        (await _adapter.FlushFileBuffers("\\file", info, default)).FileInfo.FileSize.Should().Be(1);

        _adapter.Close(info);
        _pool.RentedCount.Should().Be(0);
        _pool.ReservedCount.Should().Be(0);
        _adapter.Close(info); // An already-cleared context cannot release twice.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultipleContexts_KeepUnlinkedDataUntilLastClose(bool creatorClosesFirst)
    {
        var first = await Create("\\file", 31);
        var second = new FileOperationInfo();
        (await _adapter.OpenFile("\\file", 0, 0, second, default)).Status.Should().Be(NtStatus.Success);
        _adapter.Cleanup("\\file", first, CleanupFlags.Delete);
        var early = creatorClosesFirst ? first : second;
        var last = creatorClosesFirst ? second : first;
        _adapter.Close(early);
        _pool.RentedCount.Should().Be(1);
        (await Read(last)).Should().Be(31);
        _adapter.Close(last);
        _pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public async Task StaleCleanup_CannotDeleteARecreatedName()
    {
        var old = await Create("\\file", 11);
        _adapter.Cleanup("\\file", old, CleanupFlags.Delete);
        var current = await Create("\\file", 22);
        var currentNode = _fs.FindNode("\\file");
        _adapter.Cleanup("\\file", old, CleanupFlags.Delete);
        _fs.FindNode("\\file").Should().BeSameAs(currentNode);
        (await Read(old)).Should().Be(11);
        (await Read(current)).Should().Be(22);
        _adapter.Close(old);
        _pool.RentedCount.Should().Be(1);
        _adapter.Cleanup("\\file", current, CleanupFlags.Delete);
        _adapter.Close(current);
        _pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public async Task Replacement_PreservesDestinationContextsAndIgnoresTheirStaleCleanup()
    {
        var source = await Create("\\source", 41);
        var target = await Create("\\target", 52);
        var oldTarget = target.Context;
        (await _adapter.MoveFile("\\source", "\\target", true, source, default)).Should().Be(NtStatus.Success);
        _fs.FindNode("\\source").Should().BeNull();
        _fs.FindNode("\\target").Should().BeSameAs(source.Context);
        _fs.FindNode("\\target").Should().NotBeSameAs(oldTarget);
        (await Read(source)).Should().Be(41);
        (await Read(target)).Should().Be(52);
        _adapter.Cleanup("\\target", target, CleanupFlags.Delete);
        _adapter.Close(target);
        _fs.FindNode("\\target").Should().BeSameAs(source.Context);
        _pool.RentedCount.Should().Be(1);
        _adapter.Cleanup("\\target", source, CleanupFlags.Delete);
        _adapter.Close(source);
        _pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public async Task CaseOnlyRename_PreservesIdentityAndContent()
    {
        var info = await Create("\\file", 63);
        var node = info.Context;
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
        var linked = await Create("\\live", 2);
        _adapter.Cleanup("\\old", unlinked, CleanupFlags.Delete);
        _pool.RentedCount.Should().Be(2);
        _fs.Dispose();
        _pool.RentedCount.Should().Be(0);
        _pool.ReservedCount.Should().Be(0);
        _fs.Dispose();
        _adapter.Close(unlinked);
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
