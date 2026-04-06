using System.Text;
using FluentAssertions;
using WinFsp;
using Xunit;
using Xunit.Abstractions;

namespace WinFsp.Net.Tests;

/// <summary>
/// Integration tests that mount a real WinFsp file system using TestMemFs,
/// exercise file I/O through the Windows API, then verify data correctness.
///
/// Design:
/// - UNC mount (no drive letter) — avoids Explorer probing dead mounts that can hang the system
/// - One mount per async mode via IClassFixture — mount once, run all tests, then unmount
/// - Each mode gets a unique UNC prefix: \\winfsp-tests\sync, \\winfsp-tests\synccompleted, \\winfsp-tests\trueasync
///
/// Code paths exercised:
/// - Sync: SynchronousIo=true, thread-static NativeBufferMemory, zero-copy
/// - SyncCompleted: SynchronousIo=false, ValueTask.IsCompletedSuccessfully=true, zero-copy
/// - TrueAsync: SynchronousIo=false, Task.Yield() → STATUS_PENDING, zero-copy
/// </summary>

// ═══════════════════════════════════════════
//  Fixtures — one mount per async mode
// ═══════════════════════════════════════════

public abstract class MountFixtureBase : IDisposable
{
    private readonly FileSystemHost _host;
    public string Root { get; }
    public AsyncMode Mode { get; }

    protected MountFixtureBase(AsyncMode mode, string shareName)
    {
        Mode = mode;
        // Unique prefix per run to avoid stale UNC share collisions after crashes
        var unique = $@"\winfsp-tests\{shareName}-{Environment.ProcessId}";
        var fs = new TestMemFs(mode, unique);
        _host = new FileSystemHost(fs);

        // mountPoint = null → WinFsp creates UNC path from Prefix
        int result = _host.Mount(null);
        if (result < 0)
            throw new InvalidOperationException($"Mount failed for {mode}: 0x{result:X8}");

        Root = _host.MountPoint ?? throw new InvalidOperationException("MountPoint is null after mount");
        // UNC path needs trailing backslash for Path.Combine to work
        if (!Root.EndsWith('\\'))
            Root += @"\";
    }

    public void Dispose() => _host.Dispose();
}

public sealed class SyncFixture() : MountFixtureBase(AsyncMode.Sync, "sync");
public sealed class SyncCompletedFixture() : MountFixtureBase(AsyncMode.SyncCompleted, "synccompleted");
public sealed class TrueAsyncFixture() : MountFixtureBase(AsyncMode.TrueAsync, "trueasync");

// ═══════════════════════════════════════════
//  Shared test logic
// ═══════════════════════════════════════════

public abstract class FileSystemHostTestsBase<TFixture>(TFixture fixture, ITestOutputHelper output)
    where TFixture : MountFixtureBase
{
    protected readonly string Root = fixture.Root;

    [Fact]
    public void WriteAndReadText()
    {
        var path = Path.Combine(Root, $"text_{Guid.NewGuid():N}.txt");
        output.WriteLine($"[{fixture.Mode}] {path}");

        File.WriteAllText(path, "Hello, WinFsp zero-copy!");
        File.ReadAllText(path).Should().Be("Hello, WinFsp zero-copy!");
    }

    [Fact]
    public void WriteAndReadBinary()
    {
        var path = Path.Combine(Root, $"bin_{Guid.NewGuid():N}.dat");
        var data = new byte[32 * 1024];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 251);

        File.WriteAllBytes(path, data);
        File.ReadAllBytes(path).Should().Equal(data);
    }

    [Fact]
    public void LargeFile()
    {
        var path = Path.Combine(Root, $"large_{Guid.NewGuid():N}.bin");
        var data = new byte[1024 * 1024]; // 1MB
        Random.Shared.NextBytes(data);

        File.WriteAllBytes(path, data);
        File.ReadAllBytes(path).Should().Equal(data);
    }

    [Fact]
    public void CreateAndDelete()
    {
        var path = Path.Combine(Root, $"del_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "delete me");
        File.Exists(path).Should().BeTrue();

        File.Delete(path);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void CreateSubdirectory()
    {
        var dir = Path.Combine(Root, $"dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Directory.Exists(dir).Should().BeTrue();
    }

    [Fact]
    public void DirectoryEnumeration()
    {
        var dir = Path.Combine(Root, $"enum_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "b");
        File.WriteAllText(Path.Combine(dir, "c.txt"), "c");

        var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        files.Should().Equal("a.txt", "b.txt", "c.txt");
    }

    [Fact]
    public async Task ConcurrentReadWrite()
    {
        int fileCount = 8;
        int dataSize = 64 * 1024;

        var tasks = Enumerable.Range(0, fileCount).Select(i => Task.Run(() =>
        {
            var path = Path.Combine(Root, $"conc_{Guid.NewGuid():N}_{i}.bin");
            var data = new byte[dataSize];
            Array.Fill(data, (byte)i);

            File.WriteAllBytes(path, data);
            File.ReadAllBytes(path).Should().Equal(data, because: $"file {i} round-trip");
        })).ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public void StreamChunkedWrite()
    {
        var path = Path.Combine(Root, $"stream_{Guid.NewGuid():N}.txt");

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
        {
            for (int i = 0; i < 100; i++)
                fs.Write(Encoding.UTF8.GetBytes($"Line {i}\n"));
        }

        var text = File.ReadAllText(path);
        text.Should().Contain("Line 0\n").And.Contain("Line 99\n");
    }

    [Fact]
    public void Truncate()
    {
        var path = Path.Combine(Root, $"trunc_{Guid.NewGuid():N}.bin");
        var data = new byte[10000];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(path, data);

        using (var fs = new FileStream(path, FileMode.Open))
            fs.SetLength(1000);

        var readBack = File.ReadAllBytes(path);
        readBack.Length.Should().Be(1000);
        readBack.Should().Equal(data[..1000]);
    }

    [Fact]
    public void OverwriteExistingFile()
    {
        var path = Path.Combine(Root, $"overwrite_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "original content");
        File.WriteAllText(path, "new content");
        File.ReadAllText(path).Should().Be("new content");
    }
}

// ═══════════════════════════════════════════
//  Concrete test classes — one per mode, each mounts once
// ═══════════════════════════════════════════

public class SyncTests(SyncFixture fixture, ITestOutputHelper output)
    : FileSystemHostTestsBase<SyncFixture>(fixture, output), IClassFixture<SyncFixture>;

public class SyncCompletedTests(SyncCompletedFixture fixture, ITestOutputHelper output)
    : FileSystemHostTestsBase<SyncCompletedFixture>(fixture, output), IClassFixture<SyncCompletedFixture>;

public class TrueAsyncTests(TrueAsyncFixture fixture, ITestOutputHelper output)
    : FileSystemHostTestsBase<TrueAsyncFixture>(fixture, output), IClassFixture<TrueAsyncFixture>;
