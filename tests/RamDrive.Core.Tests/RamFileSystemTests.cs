using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using Xunit;

namespace RamDrive.Core.Tests;

/// <summary>
/// Integration tests for <see cref="RamFileSystem"/> — file/directory CRUD through the
/// in-memory file system layer, verifying that page-backed file I/O works end-to-end.
/// </summary>
public sealed class RamFileSystemTests : IDisposable
{
    private const int PageSizeKb = 64;
    private const int PageSize = PageSizeKb * 1024;

    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;

    public RamFileSystemTests()
    {
        var options = Options.Create(new RamDriveOptions
        {
            CapacityMb = 2,
            PageSizeKb = PageSizeKb,
        });
        _pool = new PagePool(options, NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);
    }

    public void Dispose()
    {
        _fs.Dispose();
        _pool.Dispose();
    }

    // ==================== File CRUD ====================

    [Fact]
    public void CreateFile_InRoot_Succeeds()
    {
        var node = _fs.CreateFile(@"\test.txt");
        node.Should().NotBeNull();
        node!.Name.Should().Be("test.txt");
        node.IsFile.Should().BeTrue();
    }

    [Fact]
    public void CreateFile_InSubdirectory_Succeeds()
    {
        _fs.CreateDirectory(@"\subdir");
        var node = _fs.CreateFile(@"\subdir\file.txt");
        node.Should().NotBeNull();
        node!.Name.Should().Be("file.txt");
    }

    [Fact]
    public void CreateFile_DuplicateName_ReturnsNull()
    {
        _fs.CreateFile(@"\dup.txt");
        _fs.CreateFile(@"\dup.txt").Should().BeNull();
    }

    [Fact]
    public void CreateFile_ParentNotFound_ReturnsNull()
    {
        _fs.CreateFile(@"\nonexistent\file.txt").Should().BeNull();
    }

    [Fact]
    public void FindNode_Root()
    {
        _fs.FindNode(@"\").Should().NotBeNull();
        _fs.FindNode(@"\")!.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void FindNode_CaseInsensitive()
    {
        _fs.CreateFile(@"\MyFile.TXT");
        _fs.FindNode(@"\myfile.txt").Should().NotBeNull();
        _fs.FindNode(@"\MYFILE.TXT").Should().NotBeNull();
    }

    // ==================== File I/O through FileSystem ====================

    [Fact]
    public void WriteAndRead_ThroughFileNode()
    {
        var node = _fs.CreateFile(@"\data.bin")!;

        byte[] data = [10, 20, 30, 40, 50];
        node.Content!.Write(0, data);

        var buf = new byte[5];
        node.Content.Read(0, buf);
        buf.Should().Equal(data);

        node.Size.Should().Be(5);
    }

    [Fact]
    public void RandomWrite_ThroughFileNode_SpanningPages()
    {
        var node = _fs.CreateFile(@"\large.bin")!;

        // Write spanning two pages
        int offset = PageSize - 10;
        var data = new byte[20];
        Array.Fill(data, (byte)0xCD);
        node.Content!.Write(offset, data);

        var buf = new byte[20];
        node.Content.Read(offset, buf);
        buf.Should().Equal(data);
    }

    // ==================== Directory Operations ====================

    [Fact]
    public void CreateDirectory_Succeeds()
    {
        var dir = _fs.CreateDirectory(@"\mydir");
        dir.Should().NotBeNull();
        dir!.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void ListDirectory_ShowsChildren()
    {
        _fs.CreateFile(@"\a.txt");
        _fs.CreateFile(@"\b.txt");
        _fs.CreateDirectory(@"\subdir");

        var children = _fs.ListDirectory(@"\");
        children.Should().NotBeNull();
        children!.Count.Should().Be(3);
        children.Select(c => c.Name).Should().Contain(["a.txt", "b.txt", "subdir"]);
    }

    // ==================== Delete ====================

    [Fact]
    public void Delete_File_FreesPages()
    {
        var node = _fs.CreateFile(@"\temp.txt")!;
        node.Content!.Write(0, new byte[PageSize]);
        _pool.RentedCount.Should().Be(1);

        _fs.Delete(@"\temp.txt").Should().BeTrue();
        _pool.RentedCount.Should().Be(0, "deleting a file should free its pages");
    }

    [Fact]
    public void Delete_NonEmptyDirectory_Fails()
    {
        _fs.CreateDirectory(@"\dir");
        _fs.CreateFile(@"\dir\file.txt");

        _fs.Delete(@"\dir").Should().BeFalse();
    }

    [Fact]
    public void Delete_EmptyDirectory_Succeeds()
    {
        _fs.CreateDirectory(@"\dir");
        _fs.Delete(@"\dir").Should().BeTrue();
        _fs.FindNode(@"\dir").Should().BeNull();
    }

    // ==================== Move ====================

    [Fact]
    public void Move_RenameFile()
    {
        var node = _fs.CreateFile(@"\old.txt")!;
        node.Content!.Write(0, new byte[] { 1, 2, 3 });

        _fs.Move(@"\old.txt", @"\new.txt", replace: false).Should().BeTrue();
        _fs.FindNode(@"\old.txt").Should().BeNull();

        var moved = _fs.FindNode(@"\new.txt");
        moved.Should().NotBeNull();

        var buf = new byte[3];
        moved!.Content!.Read(0, buf);
        buf.Should().Equal([1, 2, 3], "data should be preserved after move");
    }

    // ==================== Capacity Tracking ====================

    [Fact]
    public void TotalBytes_And_FreeBytes_ReportCorrectly()
    {
        _fs.TotalBytes.Should().Be(2 * 1024 * 1024);
        _fs.FreeBytes.Should().Be(2 * 1024 * 1024);

        var node = _fs.CreateFile(@"\file.dat")!;
        node.Content!.Write(0, new byte[PageSize]);

        _fs.UsedBytes.Should().Be(PageSize);
        _fs.FreeBytes.Should().Be(2 * 1024 * 1024 - PageSize);
    }
}
