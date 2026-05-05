// Negative tests for IFileSystemContract — the source-generated drift assertion.
//
// The generator scans WinFsp.Native.IFileSystem and emits AssertImplementedBy<T>().
// These tests verify the assertion catches missing overrides and accepts complete
// implementations.

using System.Runtime.Versioning;
using FluentAssertions;
using RamDrive.Diagnostics.DifferentialChecker;
using WinFsp.Native;

namespace RamDrive.Core.Tests;

[SupportedOSPlatform("windows")]
public class IFileSystemContractTests
{
    [Fact]
    public void DifferentialAdapter_satisfies_contract()
    {
        // Sanity: the real DifferentialAdapter must satisfy its own contract.
        // (Its constructor would throw if not — but exercising it here keeps the
        // assertion in a place that's tested even if no test instantiates the
        // adapter.)
        var act = () => IFileSystemContract.AssertImplementedBy<DifferentialAdapter>();
        act.Should().NotThrow();
    }

    [Fact]
    public void Stub_missing_methods_throws_with_method_list()
    {
        var act = () => IFileSystemContract.AssertImplementedBy<EmptyStub>();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EmptyStub is missing overrides for IFileSystem methods*");
    }

    /// <summary>
    /// A class that implements IFileSystem only via the interface's default-method
    /// implementations — i.e. declares no methods of its own. The contract assertion
    /// checks declared-only methods, so this stub should be reported as missing
    /// every required method.
    /// </summary>
    private sealed class EmptyStub : IFileSystem
    {
        // GetVolumeInfo, GetFileSecurityByName, CreateFile, OpenFile, ReadFile,
        // WriteFile, CanDelete, ReadDirectory have no default impl, so we must
        // provide them just to make the type compile. We declare them but the
        // contract still treats them as "declared" so won't fail on those.
        // The point: many other interface members (SetVolumeLabel, OverwriteFile,
        // FlushFileBuffers, GetFileInformation, SetFileAttributes, SetFileSize,
        // MoveFile, GetReparsePoint, SetReparsePoint, DeleteReparsePoint,
        // GetStreamInfo, GetEa, SetEa, DeviceControl, GetDirInfoByName,
        // ExceptionHandler, Init, Mounted, Unmounted, DispatcherStopped, Cleanup,
        // Close, GetFileSecurity, SetFileSecurity) are inherited from interface
        // defaults — DeclaredOnly reflection won't see them, so AssertImplementedBy
        // must report them as missing.
        public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
            { totalSize = 0; freeSize = 0; volumeLabel = ""; return NtStatus.Success; }
        public int GetFileSecurityByName(string fileName, out uint fileAttributes, ref byte[]? securityDescriptor)
            { fileAttributes = 0; return NtStatus.ObjectNameNotFound; }
        public ValueTask<CreateResult> CreateFile(string fileName, uint createOptions, uint grantedAccess,
            uint fileAttributes, byte[]? securityDescriptor, ulong allocationSize, FileOperationInfo info, CancellationToken ct)
            => new(CreateResult.Error(NtStatus.InvalidDeviceRequest));
        public ValueTask<CreateResult> OpenFile(string fileName, uint createOptions, uint grantedAccess,
            FileOperationInfo info, CancellationToken ct)
            => new(CreateResult.Error(NtStatus.InvalidDeviceRequest));
        public ValueTask<ReadResult> ReadFile(string fileName, Memory<byte> buffer, ulong offset,
            FileOperationInfo info, CancellationToken ct)
            => new(ReadResult.Error(NtStatus.InvalidDeviceRequest));
        public ValueTask<WriteResult> WriteFile(string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
            bool writeToEndOfFile, bool constrainedIo, FileOperationInfo info, CancellationToken ct)
            => new(WriteResult.Error(NtStatus.InvalidDeviceRequest));
        public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
            => new(NtStatus.InvalidDeviceRequest);
        public ValueTask<FsResult> GetFileInformation(string fileName, FileOperationInfo info, CancellationToken ct)
            => new(FsResult.Error(NtStatus.InvalidDeviceRequest));
        public ValueTask<ReadDirectoryResult> ReadDirectory(string fileName, string? pattern, string? marker,
            nint buffer, uint length, FileOperationInfo info, CancellationToken ct)
            => new(ReadDirectoryResult.Error(NtStatus.InvalidDeviceRequest));
    }
}
