// Unit tests for the stream → base-object association (FileNode.BaseNode /
// MetadataOwner) and RamFileSystem.DeleteClosedNamedStreams.
//
// NTFS keeps one MFT record per file: every stream of a file shares the file's
// security descriptor and common metadata (timestamps, attributes, file id), and
// truncating the main stream on open (TRUNCATE_EXISTING) removes the file's other
// data streams unless a handle still holds one open.

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
public sealed class StreamBaseAssociationTests : IDisposable
{
    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;

    public StreamBaseAssociationTests()
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

    [Fact]
    public void A_stream_node_references_its_base_object()
    {
        _fs.CreateDirectory(@"\dir");
        _fs.CreateFile(@"\dir\f.txt");
        _fs.CreateFile(@"\dir\f.txt:s1");

        var stream = Node(@"\dir\f.txt:s1");
        stream.BaseNode.Should().BeSameAs(Node(@"\dir\f.txt"));
        stream.MetadataOwner.Should().BeSameAs(Node(@"\dir\f.txt"));
    }

    [Fact]
    public void A_root_stream_node_references_the_volume_root()
    {
        _fs.CreateFile(@"\:s1");

        Node(@"\:s1").MetadataOwner.Should().BeSameAs(Node(@"\"));
    }

    [Fact]
    public void A_non_stream_node_is_its_own_metadata_owner()
    {
        _fs.CreateFile(@"\f.txt");

        var file = Node(@"\f.txt");
        file.BaseNode.Should().BeNull();
        file.MetadataOwner.Should().BeSameAs(file);
    }

    [Fact]
    public void DeleteClosedNamedStreams_removes_closed_streams_and_returns_their_names()
    {
        _fs.CreateFile(@"\f.txt");
        _fs.CreateFile(@"\f.txt:s1");
        _fs.CreateFile(@"\f.txt:s2");

        var removed = _fs.DeleteClosedNamedStreams(Node(@"\f.txt"));

        removed.Should().Equal("s1", "s2");
        _fs.FindNode(@"\f.txt:s1").Should().BeNull();
        _fs.FindNode(@"\f.txt:s2").Should().BeNull();
        Node(@"\f.txt").Should().NotBeNull();
    }

    [Fact]
    public void DeleteClosedNamedStreams_keeps_streams_that_are_still_open()
    {
        _fs.CreateFile(@"\f.txt");
        _fs.CreateFile(@"\f.txt:open");
        _fs.CreateFile(@"\f.txt:closed");

        FileNode held = _fs.OpenNode(@"\f.txt:open")!;
        try
        {
            _fs.DeleteClosedNamedStreams(Node(@"\f.txt")).Should().Equal("closed");

            _fs.FindNode(@"\f.txt:open").Should().BeSameAs(held);
        }
        finally
        {
            _fs.CloseNode(held);
        }
    }

    [Fact]
    public void DeleteClosedNamedStreams_on_the_root_removes_closed_root_streams()
    {
        _fs.CreateFile(@"\:s1");
        _fs.CreateFile(@"\f.txt");

        _fs.DeleteClosedNamedStreams(Node(@"\")).Should().Equal("s1");

        _fs.FindNode(@"\:s1").Should().BeNull();
        _fs.FindNode(@"\f.txt").Should().NotBeNull();
    }

    [Fact]
    public void DeleteClosedNamedStreams_of_an_unlinked_base_is_a_noop()
    {
        _fs.CreateFile(@"\f.txt");
        _fs.CreateFile(@"\f.txt:s1");
        var node = Node(@"\f.txt");

        _fs.Delete(@"\f.txt").Should().BeTrue();   // cascades to the stream

        _fs.DeleteClosedNamedStreams(node).Should().BeEmpty();
    }

    [Fact]
    public void Deleting_one_stream_keeps_the_base_association_of_the_others()
    {
        _fs.CreateFile(@"\f.txt");
        _fs.CreateFile(@"\f.txt:s1");
        _fs.CreateFile(@"\f.txt:s2");

        _fs.Delete(@"\f.txt:s1").Should().BeTrue();

        Node(@"\f.txt:s2").MetadataOwner.Should().BeSameAs(Node(@"\f.txt"));
    }
}
