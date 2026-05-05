// Comparators — compare IFileSystem result types between the production adapter
// and the reference filesystem. On divergence, throw DifferentialMismatchException
// (caller wraps with method name + path).

using WinFsp.Native;

namespace RamDrive.Diagnostics.DifferentialChecker;

public sealed class DifferentialMismatchException : Exception
{
    public DifferentialMismatchException(string message) : base(message) { }
}

internal static class Comparators
{
    // Compare two FspFileInfo. Skip fields that legitimately differ between the
    // production adapter and MemfsReferenceFs:
    //   - timestamps: each adapter calls its own GetSystemTime() and they will differ
    //     by µs.
    //   - AllocationSize: RamDrive uses sparse allocation (pages only on write), memfs
    //     pre-allocates a byte[] for the whole logical size. Both are valid AllocationSize
    //     reports per WinFsp semantics; cache coherency only depends on FileSize.
    //   - IndexNumber: per-instance index counter, never matches.
    //   - ReparseTag: not relevant unless the test exercises reparse points (none do).
    //   - HardLinks / EaSize: not modelled.
    public static void CompareFileInfo(string method, string? path, in FspFileInfo a, in FspFileInfo b)
    {
        if (a.FileAttributes != b.FileAttributes)
            throw New(method, path, $"FileAttributes ram=0x{a.FileAttributes:X} ref=0x{b.FileAttributes:X}");
        if (a.FileSize != b.FileSize)
            throw New(method, path, $"FileSize ram={a.FileSize} ref={b.FileSize}");
    }

    public static void CompareStatus(string method, string? path, int a, int b)
    {
        if (a != b)
            throw New(method, path, $"NTSTATUS ram=0x{a:X8} ref=0x{b:X8}");
    }

    public static void CompareFsResult(string method, string? path, in FsResult a, in FsResult b)
    {
        CompareStatus(method, path, a.Status, b.Status);
        if (a.Status >= 0)
            CompareFileInfo(method, path, a.FileInfo, b.FileInfo);
    }

    public static void CompareCreateResult(string method, string? path, in CreateResult a, in CreateResult b)
    {
        CompareStatus(method, path, a.Status, b.Status);
        if (a.Status >= 0)
            CompareFileInfo(method, path, a.FileInfo, b.FileInfo);
    }

    public static void CompareReadResult(string method, string? path, in ReadResult a, in ReadResult b)
    {
        CompareStatus(method, path, a.Status, b.Status);
        if (a.Status >= 0 && a.BytesTransferred != b.BytesTransferred)
            throw New(method, path, $"BytesTransferred ram={a.BytesTransferred} ref={b.BytesTransferred}");
    }

    public static void CompareWriteResult(string method, string? path, in WriteResult a, in WriteResult b)
    {
        CompareStatus(method, path, a.Status, b.Status);
        if (a.Status >= 0)
        {
            if (a.BytesTransferred != b.BytesTransferred)
                throw New(method, path, $"BytesTransferred ram={a.BytesTransferred} ref={b.BytesTransferred}");
            CompareFileInfo(method, path, a.FileInfo, b.FileInfo);
        }
    }

    public static void CompareReadDirectoryResult(string method, string? path, in ReadDirectoryResult a, in ReadDirectoryResult b)
    {
        CompareStatus(method, path, a.Status, b.Status);
        // BytesTransferred for directory listings is buffer-format dependent (per-entry sizes
        // include FileInfo timestamps that differ between adapters). Comparing the count of
        // entries written is more meaningful but not directly available — skip the byte count.
    }

    public static void CompareVolumeInfo(string method, ulong totalA, ulong totalB, ulong freeA, ulong freeB, string labelA, string labelB)
    {
        if (totalA != totalB)
            throw New(method, null, $"totalSize ram={totalA} ref={totalB}");
        // freeSize legitimately diverges (memfs counts file bytes; RamDrive counts pages).
        // Skip free comparison.
        // Volume label legitimately differs.
    }

    private static DifferentialMismatchException New(string method, string? path, string detail)
        => new($"DIFF in {method}({path ?? "<null>"}): {detail}");
}
