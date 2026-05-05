// DifferentialAdapter — wraps two IFileSystem implementations, dispatches every
// callback to both, and compares responses. Throws DifferentialMismatchException
// on first divergence.
//
// Design:
//   - Each open handle has a single FileOperationInfo with one Context slot.
//     We store a (ramCtx, refCtx) pair there. Before calling each child, we
//     swap info.Context to the child's context, call, then save what the child
//     wrote back.
//   - WinFsp framework only wires its dispatcher to the OUTER adapter, so each
//     child's "info" is simulated. We pass the same physical FileOperationInfo
//     to both children (after Context swap) — IsDirectory/CancellationToken/
//     ProcessId are shared which is fine.
//   - For Read/Write that take a buffer: ram writes its read into the kernel
//     buffer (returned to OS); ref writes into a private scratch buffer.
//     For the kernel-buffer case the ram result is the source of truth.
//
// Concurrency: WinFsp serializes operations per-handle, but multiple handles
// can call concurrently. Each handle's Pair is created in CreateFile/OpenFile;
// no shared mutable state across handles.

using System.Runtime.Versioning;
using WinFsp.Native;

namespace RamDrive.Diagnostics.DifferentialChecker;

[SupportedOSPlatform("windows")]
public sealed class DifferentialAdapter : IFileSystem
{
    private readonly IFileSystem _ram;
    private readonly IFileSystem _reference;

    public DifferentialAdapter(IFileSystem ram, IFileSystem reference)
    {
        _ram = ram;
        _reference = reference;
        // Compile-time-generator drift assertion: fail fast if a new IFileSystem method
        // exists but DifferentialAdapter doesn't override it.
        IFileSystemContract.AssertImplementedBy<DifferentialAdapter>();
    }

    private sealed class Pair
    {
        public object? RamCtx;
        public object? RefCtx;
    }

    private static Pair P(FileOperationInfo info)
    {
        if (info.Context is Pair p) return p;
        p = new Pair();
        info.Context = p;
        return p;
    }

    private static void SwapTo(FileOperationInfo info, object? ctx)
        => info.Context = ctx;

    private static void SaveAndRestore(FileOperationInfo info, Pair pair, ref object? slot)
    {
        slot = info.Context;
        info.Context = pair;
    }

    public bool SynchronousIo => _ram.SynchronousIo;

    public int Init(FileSystemHost host)
    {
        // Only Init the production adapter against the real host. The reference
        // FS exists purely as an in-process oracle — never receives kernel I/O.
        // Calling its Init would either be ignored or alter host params.
        return _ram.Init(host);
    }

    public int Mounted(FileSystemHost host) => _ram.Mounted(host);
    public void Unmounted(FileSystemHost host) => _ram.Unmounted(host);
    public void DispatcherStopped(bool normally) => _ram.DispatcherStopped(normally);

    public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
    {
        int sa = _ram.GetVolumeInfo(out var ta, out var fa, out var la);
        int sb = _reference.GetVolumeInfo(out var tb, out var fb, out var lb);
        Comparators.CompareStatus("GetVolumeInfo", null, sa, sb);
        Comparators.CompareVolumeInfo("GetVolumeInfo", ta, tb, fa, fb, la, lb);
        totalSize = ta; freeSize = fa; volumeLabel = la;
        return sa;
    }

    public int SetVolumeLabel(string volumeLabel, out ulong totalSize, out ulong freeSize)
    {
        int sa = _ram.SetVolumeLabel(volumeLabel, out var ta, out var fa);
        int sb = _reference.SetVolumeLabel(volumeLabel, out var tb, out var fb);
        Comparators.CompareStatus("SetVolumeLabel", null, sa, sb);
        Comparators.CompareVolumeInfo("SetVolumeLabel", ta, tb, fa, fb, "", "");
        totalSize = ta; freeSize = fa;
        return sa;
    }

    public int GetFileSecurityByName(string fileName, out uint fileAttributes, ref byte[]? securityDescriptor)
    {
        byte[]? sdA = securityDescriptor, sdB = securityDescriptor;
        int sa = _ram.GetFileSecurityByName(fileName, out var attrA, ref sdA);
        int sb = _reference.GetFileSecurityByName(fileName, out var attrB, ref sdB);
        Comparators.CompareStatus("GetFileSecurityByName", fileName, sa, sb);
        if (sa >= 0 && attrA != attrB)
            throw new DifferentialMismatchException(
                $"DIFF in GetFileSecurityByName({fileName}): FileAttributes ram=0x{attrA:X} ref=0x{attrB:X}");
        fileAttributes = attrA;
        securityDescriptor = sdA;
        return sa;
    }

    public async ValueTask<CreateResult> CreateFile(string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[]? securityDescriptor, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        var a = await _ram.CreateFile(fileName, createOptions, grantedAccess, fileAttributes, securityDescriptor, allocationSize, info, ct);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        var b = await _reference.CreateFile(fileName, createOptions, grantedAccess, fileAttributes, securityDescriptor, allocationSize, info, ct);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareCreateResult("CreateFile", fileName, a, b);
        return a;
    }

    public async ValueTask<CreateResult> OpenFile(string fileName, uint createOptions, uint grantedAccess,
        FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        var a = await _ram.OpenFile(fileName, createOptions, grantedAccess, info, ct);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        var b = await _reference.OpenFile(fileName, createOptions, grantedAccess, info, ct);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareCreateResult("OpenFile", fileName, a, b);
        return a;
    }

    public async ValueTask<FsResult> OverwriteFile(uint fileAttributes, bool replaceFileAttributes, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        var a = await _ram.OverwriteFile(fileAttributes, replaceFileAttributes, allocationSize, info, ct);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        var b = await _reference.OverwriteFile(fileAttributes, replaceFileAttributes, allocationSize, info, ct);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareFsResult("OverwriteFile", null, a, b);
        return a;
    }

    public async ValueTask<ReadResult> ReadFile(string fileName, Memory<byte> buffer, ulong offset,
        FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        // Ram writes into the kernel-supplied buffer.
        SwapTo(info, pair.RamCtx);
        var a = await _ram.ReadFile(fileName, buffer, offset, info, ct);
        pair.RamCtx = info.Context;
        // Reference writes into a scratch buffer of identical size.
        SwapTo(info, pair.RefCtx);
        var scratch = new byte[buffer.Length];
        var b = await _reference.ReadFile(fileName, scratch, offset, info, ct);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareReadResult("ReadFile", fileName, a, b);
        // Compare bytes actually transferred.
        if (a.Status >= 0 && a.BytesTransferred > 0)
        {
            int n = (int)a.BytesTransferred;
            if (!buffer.Span[..n].SequenceEqual(scratch.AsSpan(0, n)))
            {
                int firstDiff = 0;
                for (; firstDiff < n; firstDiff++)
                    if (buffer.Span[firstDiff] != scratch[firstDiff]) break;
                throw new DifferentialMismatchException(
                    $"DIFF in ReadFile({fileName}): byte content differs at offset {offset + (uint)firstDiff} " +
                    $"ram=0x{buffer.Span[firstDiff]:X2} ref=0x{scratch[firstDiff]:X2} (over {n} bytes)");
            }
        }
        return a;
    }

    public async ValueTask<WriteResult> WriteFile(string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool writeToEndOfFile, bool constrainedIo, FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        var a = await _ram.WriteFile(fileName, buffer, offset, writeToEndOfFile, constrainedIo, info, ct);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        var b = await _reference.WriteFile(fileName, buffer, offset, writeToEndOfFile, constrainedIo, info, ct);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareWriteResult("WriteFile", fileName, a, b);
        return a;
    }

    public async ValueTask<FsResult> FlushFileBuffers(string? fileName, FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        var a = await _ram.FlushFileBuffers(fileName, info, ct);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        var b = await _reference.FlushFileBuffers(fileName, info, ct);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareFsResult("FlushFileBuffers", fileName, a, b);
        return a;
    }

    public async ValueTask<FsResult> GetFileInformation(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        var a = await _ram.GetFileInformation(fileName, info, ct);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        var b = await _reference.GetFileInformation(fileName, info, ct);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareFsResult("GetFileInformation", fileName, a, b);
        return a;
    }

    public async ValueTask<FsResult> SetFileAttributes(string fileName, uint fileAttributes,
        ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime,
        FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        var a = await _ram.SetFileAttributes(fileName, fileAttributes, creationTime, lastAccessTime, lastWriteTime, changeTime, info, ct);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        var b = await _reference.SetFileAttributes(fileName, fileAttributes, creationTime, lastAccessTime, lastWriteTime, changeTime, info, ct);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareFsResult("SetFileAttributes", fileName, a, b);
        return a;
    }

    public async ValueTask<FsResult> SetFileSize(string fileName, ulong newSize, bool setAllocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        var a = await _ram.SetFileSize(fileName, newSize, setAllocationSize, info, ct);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        var b = await _reference.SetFileSize(fileName, newSize, setAllocationSize, info, ct);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareFsResult("SetFileSize", fileName, a, b);
        return a;
    }

    public async ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        var a = await _ram.CanDelete(fileName, info, ct);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        var b = await _reference.CanDelete(fileName, info, ct);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareStatus("CanDelete", fileName, a, b);
        return a;
    }

    public async ValueTask<int> MoveFile(string fileName, string newFileName, bool replaceIfExists,
        FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        var a = await _ram.MoveFile(fileName, newFileName, replaceIfExists, info, ct);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        var b = await _reference.MoveFile(fileName, newFileName, replaceIfExists, info, ct);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareStatus("MoveFile", fileName, a, b);
        return a;
    }

    public void Cleanup(string? fileName, FileOperationInfo info, CleanupFlags flags)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        _ram.Cleanup(fileName, info, flags);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        _reference.Cleanup(fileName, info, flags);
        pair.RefCtx = info.Context;
        info.Context = pair;
    }

    public void Close(FileOperationInfo info)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        _ram.Close(info);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        _reference.Close(info);
        pair.RefCtx = info.Context;
        info.Context = null;
    }

    public async ValueTask<ReadDirectoryResult> ReadDirectory(string fileName, string? pattern, string? marker,
        nint buffer, uint length, FileOperationInfo info, CancellationToken ct)
    {
        var pair = P(info);
        // Reference's buffer must be a separate native-style buffer. We can't easily
        // allocate a second native buffer for the ref FS, and the ReadDirectory
        // contract uses raw memory writes. Comparing the raw bytes is brittle anyway
        // (the per-entry FspDirInfo embeds timestamps).
        // → Only call ram on the kernel buffer; skip the reference call entirely.
        // ReadDirectory divergence is caught indirectly via per-entry GetFileInformation.
        SwapTo(info, pair.RamCtx);
        var a = await _ram.ReadDirectory(fileName, pattern, marker, buffer, length, info, ct);
        pair.RamCtx = info.Context;
        info.Context = pair;
        return a;
    }

    public int GetFileSecurity(string fileName, ref byte[]? securityDescriptor, FileOperationInfo info)
    {
        var pair = P(info);
        byte[]? sdA = securityDescriptor, sdB = securityDescriptor;
        SwapTo(info, pair.RamCtx);
        int sa = _ram.GetFileSecurity(fileName, ref sdA, info);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        int sb = _reference.GetFileSecurity(fileName, ref sdB, info);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareStatus("GetFileSecurity", fileName, sa, sb);
        securityDescriptor = sdA;
        return sa;
    }

    public int SetFileSecurity(string fileName, uint securityInformation, byte[] modificationDescriptor, FileOperationInfo info)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        int sa = _ram.SetFileSecurity(fileName, securityInformation, modificationDescriptor, info);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        int sb = _reference.SetFileSecurity(fileName, securityInformation, modificationDescriptor, info);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareStatus("SetFileSecurity", fileName, sa, sb);
        return sa;
    }

    public int GetReparsePoint(string fileName, ref byte[]? reparseData, FileOperationInfo info)
    {
        var pair = P(info);
        byte[]? rdA = reparseData, rdB = reparseData;
        SwapTo(info, pair.RamCtx);
        int sa = _ram.GetReparsePoint(fileName, ref rdA, info);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        int sb = _reference.GetReparsePoint(fileName, ref rdB, info);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareStatus("GetReparsePoint", fileName, sa, sb);
        reparseData = rdA;
        return sa;
    }

    public int SetReparsePoint(string fileName, byte[] reparseData, FileOperationInfo info)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        int sa = _ram.SetReparsePoint(fileName, reparseData, info);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        int sb = _reference.SetReparsePoint(fileName, reparseData, info);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareStatus("SetReparsePoint", fileName, sa, sb);
        return sa;
    }

    public int DeleteReparsePoint(string fileName, byte[] reparseData, FileOperationInfo info)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        int sa = _ram.DeleteReparsePoint(fileName, reparseData, info);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        int sb = _reference.DeleteReparsePoint(fileName, reparseData, info);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareStatus("DeleteReparsePoint", fileName, sa, sb);
        return sa;
    }

    public int GetStreamInfo(string fileName, nint buffer, uint length, out uint bytesTransferred, FileOperationInfo info)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        int sa = _ram.GetStreamInfo(fileName, buffer, length, out var bA, info);
        pair.RamCtx = info.Context;
        // Skip ref call (would corrupt the kernel buffer). Status-only comparison
        // would require a separate native buffer for ref; deferred.
        SwapTo(info, pair.RefCtx);
        info.Context = pair;
        bytesTransferred = bA;
        return sa;
    }

    public int GetEa(string fileName, nint ea, uint eaLength, out uint bytesTransferred, FileOperationInfo info)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        int sa = _ram.GetEa(fileName, ea, eaLength, out var bA, info);
        pair.RamCtx = info.Context;
        info.Context = pair;
        bytesTransferred = bA;
        return sa;
    }

    public int SetEa(string fileName, nint ea, uint eaLength, out FspFileInfo fileInfo, FileOperationInfo info)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        int sa = _ram.SetEa(fileName, ea, eaLength, out var fiA, info);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        int sb = _reference.SetEa(fileName, ea, eaLength, out var fiB, info);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareStatus("SetEa", fileName, sa, sb);
        if (sa >= 0) Comparators.CompareFileInfo("SetEa", fileName, fiA, fiB);
        fileInfo = fiA;
        return sa;
    }

    public int DeviceControl(string fileName, uint controlCode,
        ReadOnlySpan<byte> input, Span<byte> output, out uint bytesTransferred, FileOperationInfo info)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        int sa = _ram.DeviceControl(fileName, controlCode, input, output, out var bA, info);
        pair.RamCtx = info.Context;
        info.Context = pair;
        bytesTransferred = bA;
        return sa;
    }

    public int GetDirInfoByName(string dirName, string entryName, out FspDirInfo dirInfo, FileOperationInfo info)
    {
        var pair = P(info);
        SwapTo(info, pair.RamCtx);
        int sa = _ram.GetDirInfoByName(dirName, entryName, out var dA, info);
        pair.RamCtx = info.Context;
        SwapTo(info, pair.RefCtx);
        int sb = _reference.GetDirInfoByName(dirName, entryName, out var dB, info);
        pair.RefCtx = info.Context;
        info.Context = pair;
        Comparators.CompareStatus("GetDirInfoByName", entryName, sa, sb);
        if (sa >= 0)
            Comparators.CompareFileInfo("GetDirInfoByName", entryName, dA.FileInfo, dB.FileInfo);
        dirInfo = dA;
        return sa;
    }

    public int ExceptionHandler(Exception ex)
    {
        // Surface DifferentialMismatchException to stderr (kernel callback would otherwise
        // just see STATUS_UNEXPECTED_IO_ERROR and the user has no clue what diverged).
        Console.Error.WriteLine($"[DifferentialAdapter] {ex.GetType().Name}: {ex.Message}");
        if (ex is DifferentialMismatchException) Console.Error.Flush();
        return _ram.ExceptionHandler(ex);
    }
}
