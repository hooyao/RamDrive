// Unit tests for RamFileSystem.IsEmptyForDeletion.
//
// This is the structure-locked, stream-filtered emptiness query that
// WinFspRamAdapter.CanDelete uses instead of enumerating FileNode.Children directly.
// Enumerating Children outside the structure lock races create/move/delete: a
// concurrent insertion invalidates the enumerator and the callback throws instead of
// returning a status. Named streams are attached data and must NOT make a directory
// look non-empty.

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
public sealed class IsEmptyForDeletionTests : IDisposable
{
    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;

    public IsEmptyForDeletionTests()
    {
        var opts = new RamDriveOptions { CapacityMb = 64, PageSizeKb = 64 };
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
    public void An_empty_directory_is_empty_for_deletion()
    {
        _fs.CreateDirectory(@"\d");
        _fs.IsEmptyForDeletion(Node(@"\d")).Should().BeTrue();
    }

    [Fact]
    public void A_directory_with_a_child_is_not_empty_for_deletion()
    {
        _fs.CreateDirectory(@"\d");
        _fs.CreateFile(@"\d\f.txt");
        _fs.IsEmptyForDeletion(Node(@"\d")).Should().BeFalse();
    }

    [Fact]
    public void A_directory_carrying_only_streams_is_empty_for_deletion()
    {
        // Named streams are attached data, not directory entries.
        _fs.CreateDirectory(@"\d");
        _fs.CreateFile(@"\d:meta");
        _fs.IsEmptyForDeletion(Node(@"\d")).Should().BeTrue();
    }

    [Fact]
    public void A_directory_with_a_child_and_streams_is_not_empty_for_deletion()
    {
        _fs.CreateDirectory(@"\d");
        _fs.CreateFile(@"\d:meta");
        _fs.CreateFile(@"\d\real.txt");
        _fs.IsEmptyForDeletion(Node(@"\d")).Should().BeFalse();
    }

    [Fact]
    public void A_plain_file_is_always_empty_for_deletion()
    {
        _fs.CreateFile(@"\f.txt");
        _fs.IsEmptyForDeletion(Node(@"\f.txt")).Should().BeTrue();
    }

    [Fact]
    public void Deleting_a_directory_whose_only_content_is_streams_succeeds()
    {
        // Delete must agree with IsEmptyForDeletion: streams do not block it.
        _fs.CreateDirectory(@"\d");
        _fs.CreateFile(@"\d:meta");
        _fs.Delete(@"\d").Should().BeTrue();
        _fs.FindNode(@"\d").Should().BeNull();
    }
}
