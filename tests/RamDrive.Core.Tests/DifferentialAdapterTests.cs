// Negative tests for DifferentialAdapter — verify it throws on response divergence.
//
// We construct two stub IFileSystem implementations whose responses differ by one
// known field, wrap them in DifferentialAdapter, and assert the comparator throws
// DifferentialMismatchException with the expected method/field in the message.
//
// These tests exercise the comparator and the per-method dispatch logic without
// needing a real WinFsp mount.

using System.Runtime.Versioning;
using FluentAssertions;
using RamDrive.Diagnostics.DifferentialChecker;
using WinFsp.Native;

namespace RamDrive.Core.Tests;

[SupportedOSPlatform("windows")]
public class DifferentialAdapterTests
{
    [Fact]
    public async Task GetFileInformation_FileSize_mismatch_throws()
    {
        var ram = new StubFs { FileSize = 100 };
        var refFs = new StubFs { FileSize = 200 };
        var diff = new DifferentialAdapter(ram, refFs);

        var info = new FileOperationInfo();
        var act = async () => await diff.GetFileInformation("\\file.txt", info, default);

        var ex = await act.Should().ThrowAsync<DifferentialMismatchException>();
        ex.WithMessage("*GetFileInformation*FileSize*ram=100*ref=200*");
    }

    [Fact]
    public async Task GetFileInformation_FileAttributes_mismatch_throws()
    {
        var ram = new StubFs { FileAttributes = 0x80 };
        var refFs = new StubFs { FileAttributes = 0x20 };
        var diff = new DifferentialAdapter(ram, refFs);

        var info = new FileOperationInfo();
        var act = async () => await diff.GetFileInformation("\\file.txt", info, default);

        var ex = await act.Should().ThrowAsync<DifferentialMismatchException>();
        ex.WithMessage("*FileAttributes*ram=0x80*ref=0x20*");
    }

    [Fact]
    public async Task FlushFileBuffers_empty_FileInfo_vs_real_throws()
    {
        // Reproduces the bug #3 pattern: ram returns Success() with default(FspFileInfo)
        // (all zero fields) while ref returns the actual FileInfo. The DifferentialAdapter
        // catches the divergence on the first compared field — exactly the kind of
        // regression that would have prevented bug #3 from shipping.
        var ram = new StubFs { FlushReturnsEmptyFileInfo = true, FileSize = 100 };
        var refFs = new StubFs { FileSize = 100 };
        var diff = new DifferentialAdapter(ram, refFs);

        var info = new FileOperationInfo();
        var act = async () => await diff.FlushFileBuffers("\\file.txt", info, default);

        var ex = await act.Should().ThrowAsync<DifferentialMismatchException>();
        ex.WithMessage("*FlushFileBuffers*ram=0x0*ref=0x80*");
    }

    [Fact]
    public async Task GetFileInformation_identical_results_no_throw()
    {
        var ram = new StubFs { FileSize = 100, FileAttributes = 0x20 };
        var refFs = new StubFs { FileSize = 100, FileAttributes = 0x20 };
        var diff = new DifferentialAdapter(ram, refFs);

        var info = new FileOperationInfo();
        var result = await diff.GetFileInformation("\\file.txt", info, default);

        result.Status.Should().Be(NtStatus.Success);
        result.FileInfo.FileSize.Should().Be(100);
    }

    [Fact]
    public async Task ReadFile_byte_content_mismatch_throws()
    {
        var ram = new StubFs { ReadBytes = [0xAA, 0xBB] };
        var refFs = new StubFs { ReadBytes = [0xAA, 0xCC] };
        var diff = new DifferentialAdapter(ram, refFs);

        var info = new FileOperationInfo();
        var buffer = new byte[2];
        var act = async () => await diff.ReadFile("\\file.txt", buffer, 0, info, default);

        var ex = await act.Should().ThrowAsync<DifferentialMismatchException>();
        ex.WithMessage("*ReadFile*byte content differs*");
    }

    /// <summary>
    /// Minimal IFileSystem stub. Returns parametric responses so tests can inject
    /// specific divergences. NOT a full file system — only the methods the tests
    /// exercise are meaningful.
    /// </summary>
    private sealed class StubFs : IFileSystem
    {
        public uint FileAttributes { get; init; } = 0x80;
        public ulong FileSize { get; init; }
        public byte[] ReadBytes { get; init; } = [];
        public bool FlushReturnsEmptyFileInfo { get; init; }

        private FspFileInfo MkInfo() => new()
        {
            FileAttributes = FileAttributes,
            FileSize = FileSize,
            AllocationSize = FileSize,
        };

        public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
            { totalSize = 0; freeSize = 0; volumeLabel = "stub"; return NtStatus.Success; }

        public int GetFileSecurityByName(string fileName, out uint fileAttributes, ref byte[]? sd)
            { fileAttributes = FileAttributes; return NtStatus.Success; }

        public ValueTask<CreateResult> CreateFile(string fileName, uint co, uint ga, uint fa,
            byte[]? sd, ulong alloc, FileOperationInfo info, CancellationToken ct)
            => new(new CreateResult(NtStatus.Success, MkInfo()));

        public ValueTask<CreateResult> OpenFile(string fileName, uint co, uint ga,
            FileOperationInfo info, CancellationToken ct)
            => new(new CreateResult(NtStatus.Success, MkInfo()));

        public ValueTask<ReadResult> ReadFile(string fileName, Memory<byte> buffer, ulong offset,
            FileOperationInfo info, CancellationToken ct)
        {
            ReadBytes.AsSpan().CopyTo(buffer.Span);
            return new(ReadResult.Success((uint)ReadBytes.Length));
        }

        public ValueTask<WriteResult> WriteFile(string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
            bool wteof, bool cio, FileOperationInfo info, CancellationToken ct)
            => new(WriteResult.Success((uint)buffer.Length, MkInfo()));

        public ValueTask<FsResult> FlushFileBuffers(string? fileName, FileOperationInfo info, CancellationToken ct)
            => new(FlushReturnsEmptyFileInfo ? FsResult.Success() : FsResult.Success(MkInfo()));

        public ValueTask<FsResult> GetFileInformation(string fileName, FileOperationInfo info, CancellationToken ct)
            => new(FsResult.Success(MkInfo()));

        public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
            => new(NtStatus.Success);

        public ValueTask<ReadDirectoryResult> ReadDirectory(string fileName, string? pattern, string? marker,
            nint buffer, uint length, FileOperationInfo info, CancellationToken ct)
            => new(ReadDirectoryResult.Success(0));
    }
}
