// Unit tests for RamFileSystem.GetNamedStreamsForNode.
//
// This is the node-based stream query that WinFspRamAdapter.GetStreamInfo uses instead
// of resolving the handle's cached pathname. The point is that it reads the node's
// CURRENT parent/name under the structure lock, so it stays correct after a rename by
// another handle -- exactly the case a stale pathname would get wrong.

using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using Xunit;

namespace RamDrive.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class GetNamedStreamsForNodeTests : IDisposable
{
    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;

    public GetNamedStreamsForNodeTests()
    {
        var opts = new RamDriveOptions { CapacityMb = 8, PageSizeKb = 64 };
        _pool = new PagePool(new OptionsWrapper<RamDriveOptions>(opts), NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);
    }

    public void Dispose()
    {
        _fs.Dispose();
        _pool.Dispose();
    }

    private FileNode Node(string path) => _fs.FindNode(path)!;

    private static string[] Names(FileNode n, RamFileSystem fs)
        => fs.GetNamedStreamsForNode(n).Select(s => s.StreamName).ToArray();

    [Fact]
    public void A_node_with_no_streams_reports_none()
    {
        _fs.CreateFile(@"\f.txt");
        Names(Node(@"\f.txt"), _fs).Should().BeEmpty();
    }

    [Fact]
    public void Streams_are_reported_by_their_plain_names()
    {
        _fs.CreateFile(@"\f.txt");
        _fs.CreateFile(@"\f.txt:s1");
        _fs.CreateFile(@"\f.txt:s2");

        Names(Node(@"\f.txt"), _fs).Should().Equal("s1", "s2");
    }

    [Fact]
    public void Streams_follow_the_node_across_a_rename()
    {
        // The whole reason this method exists: after the object is renamed (by anyone),
        // its streams must still be found from the node reference.
        _fs.CreateFile(@"\a.txt");
        _fs.CreateFile(@"\a.txt:s1");
        var node = Node(@"\a.txt");

        _fs.Move(@"\a.txt", @"\b.txt", replace: false).Should().BeTrue();

        Names(node, _fs).Should().Equal("s1");
    }

    [Fact]
    public void Streams_follow_the_node_across_a_case_only_rename()
    {
        _fs.CreateFile(@"\a.txt");
        _fs.CreateFile(@"\a.txt:s1");
        var node = Node(@"\a.txt");

        _fs.Move(@"\a.txt", @"\A.txt", replace: true).Should().BeTrue();

        Names(node, _fs).Should().Equal("s1");
    }

    [Fact]
    public void Streams_follow_the_node_across_a_directory_move()
    {
        _fs.CreateDirectory(@"\d1");
        _fs.CreateDirectory(@"\d2");
        _fs.CreateFile(@"\d1\a.txt");
        _fs.CreateFile(@"\d1\a.txt:s1");
        var node = Node(@"\d1\a.txt");

        _fs.Move(@"\d1", @"\d2\d1", replace: false).Should().BeTrue();

        Names(node, _fs).Should().Equal("s1");
        node.Name.Should().Be(@"a.txt");
    }

    [Fact]
    public void Only_streams_of_this_base_are_reported()
    {
        _fs.CreateFile(@"\f.txt");
        _fs.CreateFile(@"\f.txt:s1");
        _fs.CreateFile(@"\f.txt2:other");

        Names(Node(@"\f.txt"), _fs).Should().Equal("s1");
    }

    [Fact]
    public void The_volume_root_reports_its_own_streams()
    {
        // The root has no Parent; its streams are its own stream children. Querying it
        // must not silently return nothing (the shape that would break FindFirstStreamW
        // on the volume root).
        _fs.CreateFile(@"\:rootstream");

        Names(Node(@"\"), _fs).Should().Equal("rootstream");
    }

    [Fact]
    public void The_volume_root_reports_none_when_it_has_no_streams()
    {
        _fs.CreateFile(@"\f.txt");
        Names(Node(@"\"), _fs).Should().BeEmpty();
    }

    // The path-based counterpart (spec task 1.3) is still public; keep it covered so the
    // two entry points cannot drift apart.

    [Fact]
    public void The_path_based_query_reports_the_same_streams()
    {
        _fs.CreateDirectory(@"\d");
        _fs.CreateFile(@"\d\a.txt");
        _fs.CreateFile(@"\d\a.txt:s1");
        _fs.CreateFile(@"\d\a.txt:s2");
        _fs.CreateFile(@"\:rootstream");

        _fs.GetNamedStreams(@"\d\a.txt").Select(s => s.StreamName).Should().Equal("s1", "s2");
        _fs.GetNamedStreams(@"\d\a.txt:s1").Select(s => s.StreamName).Should().Equal("s1", "s2");
        _fs.GetNamedStreams(@"\").Select(s => s.StreamName).Should().Equal("rootstream");
    }

    [Fact]
    public void A_stream_node_reports_its_base_objects_streams()
    {
        // Querying the streams of a handle that is ITSELF a stream must list the owning
        // file's streams (NTFS behavior), not nothing.
        _fs.CreateFile(@"\f.txt");
        _fs.CreateFile(@"\f.txt:s1");
        _fs.CreateFile(@"\f.txt:s2");

        Names(Node(@"\f.txt:s1"), _fs).Should().Equal("s1", "s2");
    }

    [Fact]
    public void A_root_stream_node_reports_the_roots_streams()
    {
        _fs.CreateFile(@"\:rootstream");
        _fs.CreateFile(@"\:other");

        Names(Node(@"\:rootstream"), _fs).Should().Equal("other", "rootstream");
    }
}
