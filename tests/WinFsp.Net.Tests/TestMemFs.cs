using System.Runtime.Versioning;
using WinFsp;
using WinFsp.Native;

namespace WinFsp.Net.Tests;

/// <summary>
/// Async mode for TestMemFs I/O operations.
/// </summary>
public enum AsyncMode
{
    /// <summary>SynchronousIo = true, all ValueTasks complete synchronously.</summary>
    Sync,

    /// <summary>SynchronousIo = false, but ValueTasks complete synchronously (IsCompletedSuccessfully = true).</summary>
    SyncCompleted,

    /// <summary>SynchronousIo = false, uses Task.Yield() to force STATUS_PENDING path.</summary>
    TrueAsync,
}

/// <summary>
/// Lightweight in-memory IFileSystem for testing FileSystemHost code paths.
/// Supports all three async modes to exercise sync, sync-completed, and STATUS_PENDING paths.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TestMemFs : IFileSystem
{
    private readonly AsyncMode _mode;
    private readonly string _prefix;
    private readonly object _lock = new();
    private readonly Dictionary<string, MemNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private long _totalBytes;

    public TestMemFs(AsyncMode mode, string prefix, long totalBytes = 64 * 1024 * 1024)
    {
        _mode = mode;
        _prefix = prefix;
        _totalBytes = totalBytes;
        // Root directory
        _nodes[@"\"] = new MemNode { IsDirectory = true, Name = @"\" };
    }

    public bool SynchronousIo => _mode == AsyncMode.Sync;

    private sealed class MemNode
    {
        public string Name = "";
        public bool IsDirectory;
        public byte[] Data = [];
        public uint Attributes;
        public ulong CreationTime;
        public ulong LastAccessTime;
        public ulong LastWriteTime;
    }

    // ── Lifecycle ──

    public int Init(FileSystemHost host)
    {
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = true;
        host.FileSystemName = "NTFS";
        host.Prefix = _prefix; // UNC mount — no drive letter needed
        return NtStatus.Success;
    }

    public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
    {
        long used;
        lock (_lock) { used = _nodes.Values.Where(n => !n.IsDirectory).Sum(n => (long)n.Data.Length); }
        totalSize = (ulong)_totalBytes;
        freeSize = (ulong)Math.Max(0, _totalBytes - used);
        volumeLabel = "TestMemFs";
        return NtStatus.Success;
    }

    public int GetFileSecurityByName(string fileName, out uint fileAttributes, ref byte[]? securityDescriptor)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(fileName, out var node))
            {
                fileAttributes = node.IsDirectory ? 0x10u : (node.Attributes != 0 ? node.Attributes : 0x80u);
                securityDescriptor = null;
                return NtStatus.Success;
            }
        }
        fileAttributes = 0;
        return NtStatus.ObjectNameNotFound;
    }

    // ── Create / Open ──

    public ValueTask<CreateResult> CreateFile(
        string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[]? securityDescriptor, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        bool isDir = (createOptions & 1) != 0; // FILE_DIRECTORY_FILE

        lock (_lock)
        {
            if (_nodes.ContainsKey(fileName))
                return V(CreateResult.Error(NtStatus.ObjectNameCollision));

            // Check parent exists
            string? parent = GetParent(fileName);
            if (parent != null && !_nodes.ContainsKey(parent))
                return V(CreateResult.Error(NtStatus.ObjectPathNotFound));

            var now = FileTimeNow();
            var node = new MemNode
            {
                Name = GetName(fileName),
                IsDirectory = isDir,
                Attributes = fileAttributes != 0 ? fileAttributes : (isDir ? 0x10u : 0x80u),
                CreationTime = now,
                LastAccessTime = now,
                LastWriteTime = now,
            };
            _nodes[fileName] = node;
            info.Context = fileName;
            info.IsDirectory = isDir;
            return V(new CreateResult(NtStatus.Success, MakeFileInfo(node)));
        }
    }

    public ValueTask<CreateResult> OpenFile(
        string fileName, uint createOptions, uint grantedAccess,
        FileOperationInfo info, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(fileName, out var node))
                return V(CreateResult.Error(NtStatus.ObjectNameNotFound));

            info.Context = fileName;
            info.IsDirectory = node.IsDirectory;
            return V(new CreateResult(NtStatus.Success, MakeFileInfo(node)));
        }
    }

    public ValueTask<FsResult> OverwriteFile(
        uint fileAttributes, bool replaceFileAttributes, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        lock (_lock)
        {
            var node = GetNode(info);
            if (node == null) return V(FsResult.Error(NtStatus.ObjectNameNotFound));
            node.Data = [];
            node.LastWriteTime = FileTimeNow();
            return V(FsResult.Success(MakeFileInfo(node)));
        }
    }

    // ── Read / Write — these are the methods under test ──

    public ValueTask<ReadResult> ReadFile(
        string fileName, Memory<byte> buffer, ulong offset,
        FileOperationInfo info, CancellationToken ct)
    {
        if (_mode == AsyncMode.TrueAsync)
            return ReadFileAsync(fileName, buffer, offset, info, ct);

        return V(ReadFileCore(fileName, buffer, offset));
    }

    private async ValueTask<ReadResult> ReadFileAsync(
        string fileName, Memory<byte> buffer, ulong offset,
        FileOperationInfo info, CancellationToken ct)
    {
        await Task.Yield(); // Force STATUS_PENDING
        return ReadFileCore(fileName, buffer, offset);
    }

    private ReadResult ReadFileCore(string fileName, Memory<byte> buffer, ulong offset)
    {
        lock (_lock)
        {
            var node = GetNode(fileName);
            if (node == null || node.IsDirectory)
                return ReadResult.Error(NtStatus.InvalidDeviceRequest);

            if ((long)offset >= node.Data.Length)
                return ReadResult.EndOfFile();

            int available = node.Data.Length - (int)offset;
            int toRead = Math.Min(buffer.Length, available);
            node.Data.AsSpan((int)offset, toRead).CopyTo(buffer.Span);
            return ReadResult.Success((uint)toRead);
        }
    }

    public ValueTask<WriteResult> WriteFile(
        string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool writeToEndOfFile, bool constrainedIo,
        FileOperationInfo info, CancellationToken ct)
    {
        if (_mode == AsyncMode.TrueAsync)
            return WriteFileAsync(fileName, buffer, offset, writeToEndOfFile, constrainedIo, info, ct);

        return V(WriteFileCore(fileName, buffer, offset, writeToEndOfFile, constrainedIo));
    }

    private async ValueTask<WriteResult> WriteFileAsync(
        string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool writeToEndOfFile, bool constrainedIo,
        FileOperationInfo info, CancellationToken ct)
    {
        await Task.Yield(); // Force STATUS_PENDING
        return WriteFileCore(fileName, buffer, offset, writeToEndOfFile, constrainedIo);
    }

    private WriteResult WriteFileCore(
        string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool writeToEndOfFile, bool constrainedIo)
    {
        lock (_lock)
        {
            var node = GetNode(fileName);
            if (node == null || node.IsDirectory)
                return WriteResult.Error(NtStatus.InvalidDeviceRequest);

            long writeOffset = writeToEndOfFile ? node.Data.Length : (long)offset;
            int writeLength = buffer.Length;

            if (constrainedIo)
            {
                if (writeOffset >= node.Data.Length)
                    return WriteResult.Success(0, MakeFileInfo(node));
                writeLength = (int)Math.Min(writeLength, node.Data.Length - writeOffset);
            }

            long endPos = writeOffset + writeLength;
            if (endPos > node.Data.Length)
            {
                var newData = new byte[endPos];
                node.Data.CopyTo(newData, 0);
                node.Data = newData;
            }

            buffer.Span[..writeLength].CopyTo(node.Data.AsSpan((int)writeOffset));
            node.LastWriteTime = FileTimeNow();
            return WriteResult.Success((uint)writeLength, MakeFileInfo(node));
        }
    }

    public ValueTask<FsResult> FlushFileBuffers(
        string? fileName, FileOperationInfo info, CancellationToken ct)
        => V(FsResult.Success());

    // ── Metadata ──

    public ValueTask<FsResult> GetFileInformation(
        string fileName, FileOperationInfo info, CancellationToken ct)
    {
        lock (_lock)
        {
            var node = GetNode(info);
            if (node == null) return V(FsResult.Error(NtStatus.ObjectNameNotFound));
            return V(FsResult.Success(MakeFileInfo(node)));
        }
    }

    public ValueTask<FsResult> SetFileAttributes(
        string fileName, uint fileAttributes, ulong creationTime, ulong lastAccessTime,
        ulong lastWriteTime, ulong changeTime,
        FileOperationInfo info, CancellationToken ct)
    {
        lock (_lock)
        {
            var node = GetNode(info);
            if (node == null) return V(FsResult.Error(NtStatus.ObjectNameNotFound));
            if (fileAttributes != unchecked((uint)(-1)) && fileAttributes != 0)
                node.Attributes = fileAttributes;
            return V(FsResult.Success(MakeFileInfo(node)));
        }
    }

    public ValueTask<FsResult> SetFileSize(
        string fileName, ulong newSize, bool setAllocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        if (setAllocationSize)
        {
            lock (_lock)
            {
                var n = GetNode(info);
                return n == null ? V(FsResult.Error(NtStatus.ObjectNameNotFound)) : V(FsResult.Success(MakeFileInfo(n)));
            }
        }

        lock (_lock)
        {
            var node = GetNode(info);
            if (node == null || node.IsDirectory) return V(FsResult.Error(NtStatus.ObjectNameNotFound));
            if ((long)newSize != node.Data.Length)
            {
                var newData = new byte[newSize];
                Buffer.BlockCopy(node.Data, 0, newData, 0, (int)Math.Min((ulong)node.Data.Length, newSize));
                node.Data = newData;
            }
            return V(FsResult.Success(MakeFileInfo(node)));
        }
    }

    // ── Delete / Move ──

    public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        lock (_lock)
        {
            var node = GetNode(info);
            if (node == null) return V(NtStatus.ObjectNameNotFound);
            if (node.IsDirectory)
            {
                string prefix = ((string)info.Context!).TrimEnd('\\') + @"\";
                if (_nodes.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.Length > prefix.Length))
                    return V(NtStatus.DirectoryNotEmpty);
            }
            return V(NtStatus.Success);
        }
    }

    public ValueTask<int> MoveFile(
        string fileName, string newFileName, bool replaceIfExists,
        FileOperationInfo info, CancellationToken ct)
    {
        lock (_lock)
        {
            string path = (string)info.Context!;
            if (!_nodes.TryGetValue(path, out var node))
                return V(NtStatus.ObjectNameNotFound);
            if (_nodes.ContainsKey(newFileName) && !replaceIfExists)
                return V(NtStatus.ObjectNameCollision);
            _nodes.Remove(path);
            node.Name = GetName(newFileName);
            _nodes[newFileName] = node;
            info.Context = newFileName;
            return V(NtStatus.Success);
        }
    }

    // ── Cleanup / Close ──

    public void Cleanup(string? fileName, FileOperationInfo info, CleanupFlags flags)
    {
        if (flags.HasFlag(CleanupFlags.Delete) && info.Context is string path)
        {
            lock (_lock) { _nodes.Remove(path); }
        }
    }

    public void Close(FileOperationInfo info) => info.Context = null;

    // ── Directory ──

    public ValueTask<ReadDirectoryResult> ReadDirectory(
        string fileName, string? pattern, string? marker,
        nint buffer, uint length,
        FileOperationInfo info, CancellationToken ct)
    {
        if (_mode == AsyncMode.TrueAsync)
            return ReadDirectoryAsync(fileName, pattern, marker, buffer, length, info, ct);

        return V(ReadDirectoryCore(fileName, marker, buffer, length));
    }

    private async ValueTask<ReadDirectoryResult> ReadDirectoryAsync(
        string fileName, string? pattern, string? marker,
        nint buffer, uint length,
        FileOperationInfo info, CancellationToken ct)
    {
        await Task.Yield();
        return ReadDirectoryCore(fileName, marker, buffer, length);
    }

    private ReadDirectoryResult ReadDirectoryCore(string fileName, string? marker, nint buffer, uint length)
    {
        string prefix = fileName.TrimEnd('\\') + @"\";
        List<(string name, MemNode node)> entries;

        lock (_lock)
        {
            entries = _nodes
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                             && kv.Key.Length > prefix.Length
                             && !kv.Key[prefix.Length..].Contains('\\'))
                .Select(kv => (kv.Value.Name, kv.Value))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        uint bt = 0;
        foreach (var (name, node) in entries)
        {
            if (marker != null && string.Compare(name, marker, StringComparison.OrdinalIgnoreCase) <= 0)
                continue;

            var di = new FspDirInfo();
            di.FileInfo = MakeFileInfo(node);
            di.SetFileName(name);
            unsafe
            {
                if (!WinFspFileSystem.AddDirInfo(&di, buffer, length, &bt))
                    return ReadDirectoryResult.Success(bt);
            }
        }

        unsafe { WinFspFileSystem.EndDirInfo(buffer, length, &bt); }
        return ReadDirectoryResult.Success(bt);
    }

    // ── Helpers ──

    private MemNode? GetNode(FileOperationInfo info)
    {
        if (info.Context is string path && _nodes.TryGetValue(path, out var node))
            return node;
        return null;
    }

    private MemNode? GetNode(string path)
    {
        _nodes.TryGetValue(path, out var node);
        return node;
    }

    private static FspFileInfo MakeFileInfo(MemNode node) => new()
    {
        FileAttributes = node.IsDirectory ? 0x10u : (node.Attributes != 0 ? node.Attributes : 0x80u),
        FileSize = (ulong)node.Data.Length,
        AllocationSize = (ulong)node.Data.Length,
        CreationTime = node.CreationTime,
        LastAccessTime = node.LastAccessTime,
        LastWriteTime = node.LastWriteTime,
        ChangeTime = node.LastWriteTime,
    };

    private static ulong FileTimeNow() => (ulong)DateTime.UtcNow.ToFileTimeUtc();

    private static string? GetParent(string path)
    {
        int i = path.LastIndexOf('\\');
        if (i <= 0) return @"\";
        return path[..i];
    }

    private static string GetName(string path)
    {
        int i = path.LastIndexOf('\\');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    // Zero-alloc ValueTask wrappers
    private static ValueTask<CreateResult> V(CreateResult r) => new(r);
    private static ValueTask<FsResult> V(FsResult r) => new(r);
    private static ValueTask<ReadResult> V(ReadResult r) => new(r);
    private static ValueTask<WriteResult> V(WriteResult r) => new(r);
    private static ValueTask<ReadDirectoryResult> V(ReadDirectoryResult r) => new(r);
    private static ValueTask<int> V(int r) => new(r);
}
