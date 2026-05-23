// Unit tests for WinFspRamAdapter.GetFileSecurityByName / GetFileSecurity.
//
// Pin two contracts:
//  (a) Happy path: InitialDirectories-style nodes (created via RamFileSystem
//      without an explicit SD argument) have a non-null SD when read back through
//      the adapter — this is the spec scenario "GetFileSecurityByName never
//      returns success with null SD" exercised at the adapter boundary.
//
//  (b) Defence in depth: if a FileNode is somehow forced to have null SD
//      (regression of the RamFileSystem invariant), the adapter substitutes the
//      cached root SD instead of returning (Success, null) to the kernel, and
//      logs a warning exactly once per path.

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
public sealed class WinFspRamAdapterSecurityTests : IDisposable
{
    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;
    private readonly WinFspRamAdapter _adapter;
    private readonly CapturingLogger _log;

    public WinFspRamAdapterSecurityTests()
    {
        var opts = new RamDriveOptions { CapacityMb = 8, PageSizeKb = 64, VolumeLabel = "Test" };
        _pool = new PagePool(new OptionsWrapper<RamDriveOptions>(opts), NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);
        _log = new CapturingLogger();
        // Constructing the adapter triggers SetRootSecurityDescriptor inside ctor.
        _adapter = new WinFspRamAdapter(_fs, new OptionsWrapper<RamDriveOptions>(opts), _log);
    }

    public void Dispose()
    {
        _fs.Dispose();
        _pool.Dispose();
    }

    [Fact]
    public void GetFileSecurityByName_OnInitialDirectoryStyleNode_ReturnsNonNullSd()
    {
        // Reproduce the original bug scenario: a directory created via the bootstrap path
        // (WinFspHostedService/DiagHostedService → _fs.CreateDirectory(path) — no SD arg).
        _fs.CreateDirectory(@"\Temp");

        byte[]? sd = null;
        int status = _adapter.GetFileSecurityByName(@"\Temp", out uint attr, ref sd);

        status.Should().Be(NtStatus.Success);
        sd.Should().NotBeNull("returning (Success, null) to the WinFsp kernel is exactly the bug we fixed");
        sd!.Length.Should().BeGreaterThan(0);
        // Sanity: it should be parseable as a valid self-relative SD.
        var parsed = new System.Security.AccessControl.RawSecurityDescriptor(sd, 0);
        parsed.ControlFlags.Should().HaveFlag(System.Security.AccessControl.ControlFlags.SelfRelative);
        parsed.ControlFlags.Should().HaveFlag(System.Security.AccessControl.ControlFlags.DiscretionaryAclPresent);
    }

    [Fact]
    public void GetFileSecurityByName_RootNode_ReturnsRootSd()
    {
        byte[]? sd = null;
        int status = _adapter.GetFileSecurityByName(@"\", out _, ref sd);

        status.Should().Be(NtStatus.Success);
        sd.Should().NotBeNull();
    }

    [Fact]
    public void GetFileSecurityByName_NodeWithForcedNullSd_FallsBackToRootSd_AndWarnsOnce()
    {
        // Reach into the structural invariant from below: create a node, then forcibly
        // null its SD. This simulates a future regression that bypasses CreateFile/CreateDirectory
        // (e.g. a new mutator that forgets to inherit). The defensive fallback must catch it.
        var node = _fs.CreateDirectory(@"\Broken");
        node.Should().NotBeNull();
        node!.SecurityDescriptor = null;  // <-- simulated regression

        byte[]? sd1 = null;
        int s1 = _adapter.GetFileSecurityByName(@"\Broken", out _, ref sd1);
        s1.Should().Be(NtStatus.Success);
        sd1.Should().NotBeNull("the defensive fallback substituted root SD");
        sd1!.Length.Should().BeGreaterThan(0);

        // Calling again with the same path must NOT log a second warning (suppression).
        byte[]? sd2 = null;
        _ = _adapter.GetFileSecurityByName(@"\Broken", out _, ref sd2);

        _log.Warnings.Should().HaveCount(1, "null-SD fallback must warn once per path, not on every read");
        _log.Warnings[0].Should().Contain(@"\Broken");
    }

    [Fact]
    public void GetFileSecurityByName_NonexistentPath_ReturnsNotFound()
    {
        byte[]? sd = null;
        int status = _adapter.GetFileSecurityByName(@"\does\not\exist", out uint attr, ref sd);

        status.Should().Be(NtStatus.ObjectNameNotFound);
        attr.Should().Be(0u);
    }

    // Minimal ILogger<T> capturing Warning-level messages for assertion.
    private sealed class CapturingLogger : ILogger<WinFspRamAdapter>
    {
        public List<string> Warnings { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
