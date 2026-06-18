// Unit tests for file-id (IndexNumber) uniqueness.
//
// Background: WinFspRamAdapter.MakeFileInfo never set FspFileInfo.IndexNumber and Init never set
// a VolumeSerialNumber, so every file reported (VolumeSerialNumber, file id) = (0, 0). The CRT/STL
// std::filesystem::copy_file (and Win32 same-volume copy fast paths) compare that pair on the
// source and destination to detect "copying a file onto itself". With every node reporting id 0,
// a same-volume copy of two DISTINCT files was rejected with std::errc::file_exists — the dotTrace
// ETW-collector deploy failure ("copy: file exists ... jetbrainsproc_<GUID> (generic:17)").
//
// These tests pin the fix at the core layer: every node has a unique, non-zero IndexNumber and the
// adapter surfaces it through GetFileInformation. The same-volume copy behaviour is covered at the
// mounted-FS layer in tests/RamDrive.IntegrationTests/FileIdUniquenessTests.cs.

using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using WinFsp.Native;

namespace RamDrive.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class FileIdUniquenessTests : IDisposable
{
    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;
    private readonly WinFspRamAdapter _adapter;

    public FileIdUniquenessTests()
    {
        var opts = new RamDriveOptions { CapacityMb = 8, PageSizeKb = 64, VolumeLabel = "Test" };
        _pool = new PagePool(new OptionsWrapper<RamDriveOptions>(opts), NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);
        _adapter = new WinFspRamAdapter(_fs, new OptionsWrapper<RamDriveOptions>(opts), NullLogger<WinFspRamAdapter>.Instance);
    }

    public void Dispose()
    {
        _fs.Dispose();
        _pool.Dispose();
    }

    [Fact]
    public void FileNode_IndexNumber_IsNonZeroAndUnique()
    {
        var a = _fs.CreateFile(@"\a.bin");
        var b = _fs.CreateFile(@"\b.bin");
        var dir = _fs.CreateDirectory(@"\d");

        a!.IndexNumber.Should().NotBe(0, "a zero file id makes copy_file treat distinct files as identical");
        b!.IndexNumber.Should().NotBe(0);
        dir!.IndexNumber.Should().NotBe(0);

        a.IndexNumber.Should().NotBe(b.IndexNumber, "two distinct files must have distinct file ids");
        a.IndexNumber.Should().NotBe(dir.IndexNumber);
        b.IndexNumber.Should().NotBe(dir.IndexNumber);
    }

    [Fact]
    public async Task GetFileInformation_ReturnsUniqueNonZeroIndexNumber_ForDistinctFiles()
    {
        // Two distinct files, opened through the adapter, must report distinct non-zero IndexNumbers
        // in their FspFileInfo — this is exactly what std::filesystem::copy_file reads to decide
        // whether source and destination are the same file.
        _fs.CreateFile(@"\one.bin");
        _fs.CreateFile(@"\two.bin");

        var i1 = new FileOperationInfo { Context = _fs.FindNode(@"\one.bin") };
        var i2 = new FileOperationInfo { Context = _fs.FindNode(@"\two.bin") };

        var r1 = await _adapter.GetFileInformation(@"\one.bin", i1, default);
        var r2 = await _adapter.GetFileInformation(@"\two.bin", i2, default);

        r1.Status.Should().Be(NtStatus.Success);
        r2.Status.Should().Be(NtStatus.Success);
        r1.FileInfo.IndexNumber.Should().NotBe(0u);
        r2.FileInfo.IndexNumber.Should().NotBe(0u);
        r1.FileInfo.IndexNumber.Should().NotBe(r2.FileInfo.IndexNumber,
            "distinct files must surface distinct file ids or same-volume copy_file fails with file_exists");
    }

    [Fact]
    public async Task GetFileInformation_IndexNumber_IsStableAcrossCalls()
    {
        // The file id must be stable for the lifetime of the node (NTFS file ids are stable),
        // otherwise tools that stat-then-open see it "change" underneath them.
        _fs.CreateFile(@"\stable.bin");
        var info = new FileOperationInfo { Context = _fs.FindNode(@"\stable.bin") };

        var a = await _adapter.GetFileInformation(@"\stable.bin", info, default);
        var b = await _adapter.GetFileInformation(@"\stable.bin", info, default);

        a.FileInfo.IndexNumber.Should().Be(b.FileInfo.IndexNumber);
        a.FileInfo.IndexNumber.Should().NotBe(0u);
    }
}
