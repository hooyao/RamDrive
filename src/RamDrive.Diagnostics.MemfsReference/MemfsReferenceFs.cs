// MemfsReferenceFs — 1:1 C# translation of winfsp's tst/memfs/memfs.cpp.
//
// This is the *canonical correct* in-memory filesystem reference. It exists to be
// run side-by-side with WinFspRamAdapter (production) inside DifferentialAdapter
// to catch behavioral divergence. NEVER reference this from RamDrive.Cli.
//
// Mappings to memfs.cpp:
//   MEMFS_FILE_NODE_MAP (std::map keyed by full path with custom comparator)
//     → SortedDictionary<string, MemfsNode> with MemfsFileNameComparer
//   MEMFS_FILE_NODE.FileName (full path)        → MemfsNode.FileName
//   MEMFS_FILE_NODE.FileData (LargeHeapAlloc)   → MemfsNode.FileData
//   MemfsFileNodeMapGetParent / HasChild / EnumerateChildren / EnumerateDescendants
//     → ported via SortedDictionary key comparator + linear walk
//
// Skipped from memfs.cpp (chrome-irrelevant):
//   - SLOWIO (test-only delay/STATUS_PENDING harness)
//   - EA / WSL extra-buffer paths in Create (we expose the no-EA Create signature)
//   - MEMFS_NAME_NORMALIZATION (binding limitation)

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using WinFsp.Native;

namespace RamDrive.Diagnostics.MemfsReference;

[SupportedOSPlatform("windows")]
public sealed unsafe class MemfsReferenceFs : IFileSystem
{
    private const ushort MEMFS_SECTOR_SIZE = 512;
    private const ushort MEMFS_SECTORS_PER_ALLOCATION_UNIT = 1;
    private const long ALLOCATION_UNIT = MEMFS_SECTOR_SIZE * MEMFS_SECTORS_PER_ALLOCATION_UNIT;

    // memfs default RootSddl when no -S flag is passed (memfs-main.c via MemfsCreateFunnel).
    private const string DefaultRootSddl = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)";

    private readonly long _capacityBytes;
    private readonly ulong _maxFileSize;
    private readonly SortedDictionary<string, MemfsNode> _nodes;
    private readonly object _mapLock = new();
    private long _nextIndexNumber = 1;
    private string _volumeLabel = "MEMFS";

    public MemfsReferenceFs(long capacityMb)
    {
        _capacityBytes = capacityMb * 1024 * 1024;
        _maxFileSize = (ulong)_capacityBytes;
        _nodes = new SortedDictionary<string, MemfsNode>(MemfsFileNameComparer.CaseInsensitive);

        var root = NewNode("\\");
        root.FileAttributes = (uint)FileAttributes.Directory;
        var sd = new RawSecurityDescriptor(DefaultRootSddl);
        var sdBytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(sdBytes, 0);
        root.FileSecurity = sdBytes;
        _nodes.Add(root.FileName, root);
        Interlocked.Increment(ref root.RefCount);
    }

    public bool SynchronousIo => true;

    public int Init(FileSystemHost host)
    {
        host.SectorSize                       = MEMFS_SECTOR_SIZE;
        host.SectorsPerAllocationUnit         = MEMFS_SECTORS_PER_ALLOCATION_UNIT;
        host.MaxComponentLength               = 255;
        host.VolumeCreationTime               = MemfsGetSystemTime();
        host.VolumeSerialNumber               = (uint)(MemfsGetSystemTime() / (10000UL * 1000UL));
        host.FileInfoTimeout                  = uint.MaxValue;
        host.CaseSensitiveSearch              = false;
        host.CasePreservedNames               = true;
        host.UnicodeOnDisk                    = true;
        host.PersistentAcls                   = true;
        host.ReparsePoints                    = true;
        host.ReparsePointsAccessCheck         = false;
        host.NamedStreams                     = true;
        host.PostCleanupWhenModifiedOnly      = true;
        host.PostDispositionWhenNecessaryOnly = true;
        host.PassQueryDirectoryFileName       = true;
        host.FlushAndPurgeOnCleanup           = false;
        host.DeviceControl                    = true;
        host.WslFeatures                      = true;
        host.AllowOpenInKernelMode            = true;
        host.SupportsPosixUnlinkRename        = true;
        host.FileSystemName                   = "NTFS";
        return NtStatus.Success;
    }

    public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
    {
        totalSize = (ulong)_capacityBytes;
        long used;
        lock (_mapLock)
        {
            used = 0;
            foreach (var kv in _nodes) used += (long)kv.Value.FileSize;
        }
        freeSize = (ulong)Math.Max(0, _capacityBytes - used);
        volumeLabel = _volumeLabel;
        return NtStatus.Success;
    }

    public int SetVolumeLabel(string label, out ulong totalSize, out ulong freeSize)
    {
        _volumeLabel = label.Length > 32 ? label[..32] : label;
        return GetVolumeInfo(out totalSize, out freeSize, out _);
    }

    public int GetFileSecurityByName(string fileName, out uint fileAttributes, ref byte[]? securityDescriptor)
    {
        MemfsNode? n;
        lock (_mapLock) _nodes.TryGetValue(fileName, out n);
        if (n == null)
        {
            fileAttributes = 0;
            return GetParentStatus(fileName);
        }
        fileAttributes = n.FileAttributes;
        securityDescriptor = n.FileSecurity;
        return NtStatus.Success;
    }

    public int GetFileSecurity(string fileName, ref byte[]? securityDescriptor, FileOperationInfo info)
    {
        var n = N(info);
        if (n == null) return NtStatus.ObjectNameNotFound;
        securityDescriptor = n.FileSecurity;
        return NtStatus.Success;
    }

    public int SetFileSecurity(string fileName, uint securityInformation, byte[] modificationDescriptor, FileOperationInfo info)
    {
        var n = N(info);
        if (n == null) return NtStatus.ObjectNameNotFound;
        var existing = n.FileSecurity != null
            ? new RawSecurityDescriptor(n.FileSecurity, 0)
            : new RawSecurityDescriptor(DefaultRootSddl);
        var modification = new RawSecurityDescriptor(modificationDescriptor, 0);
        if ((securityInformation & 1) != 0) existing.Owner = modification.Owner;
        if ((securityInformation & 2) != 0) existing.Group = modification.Group;
        if ((securityInformation & 4) != 0) existing.DiscretionaryAcl = modification.DiscretionaryAcl;
        if ((securityInformation & 8) != 0) existing.SystemAcl = modification.SystemAcl;
        var result = new byte[existing.BinaryLength];
        existing.GetBinaryForm(result, 0);
        n.FileSecurity = result;
        return NtStatus.Success;
    }

    public ValueTask<CreateResult> CreateFile(string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[]? sd, ulong allocationSize, FileOperationInfo info, CancellationToken ct)
    {
        bool isDir = (createOptions & (uint)CreateOptions.FileDirectoryFile) != 0;
        if (isDir) allocationSize = 0;

        lock (_mapLock)
        {
            if (_nodes.ContainsKey(fileName))
                return new(CreateResult.Error(NtStatus.ObjectNameCollision));

            int s = GetParentStatus(fileName);
            if (s != NtStatus.ObjectNameNotFound)
                return new(CreateResult.Error(s));

            if (allocationSize > _maxFileSize)
                return new(CreateResult.Error(NtStatus.DiskFull));

            var n = NewNode(fileName);
            n.FileAttributes = isDir
                ? fileAttributes
                : (fileAttributes | (uint)FileAttributes.Archive);
            n.FileSecurity = sd;
            n.AllocationSize = allocationSize;
            if (allocationSize > 0)
                n.FileData = new byte[(int)allocationSize];

            _nodes.Add(n.FileName, n);
            Interlocked.Increment(ref n.RefCount);
            TouchParent(n);
            Interlocked.Increment(ref n.RefCount);

            info.Context = n;
            info.IsDirectory = isDir;
            return new(new CreateResult(NtStatus.Success, MkInfo(n)));
        }
    }

    public ValueTask<CreateResult> OpenFile(string fileName, uint createOptions, uint grantedAccess,
        FileOperationInfo info, CancellationToken ct)
    {
        MemfsNode? n;
        lock (_mapLock) _nodes.TryGetValue(fileName, out n);
        if (n == null)
            return new(CreateResult.Error(GetParentStatus(fileName)));

        Interlocked.Increment(ref n.RefCount);
        info.Context = n;
        info.IsDirectory = (n.FileAttributes & (uint)FileAttributes.Directory) != 0;
        return new(new CreateResult(NtStatus.Success, MkInfo(n)));
    }

    public ValueTask<FsResult> OverwriteFile(uint fileAttributes, bool replaceFileAttributes, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info);
        if (n == null) return new(FsResult.Error(NtStatus.ObjectNameNotFound));

        int s = SetFileSizeInternal(n, allocationSize, true);
        if (s != NtStatus.Success) return new(FsResult.Error(s));

        if (replaceFileAttributes)
            n.FileAttributes = fileAttributes | (uint)FileAttributes.Archive;
        else
            n.FileAttributes |= fileAttributes | (uint)FileAttributes.Archive;

        n.FileSize = 0;
        ulong now = MemfsGetSystemTime();
        n.LastAccessTime = n.LastWriteTime = n.ChangeTime = now;
        return new(FsResult.Success(MkInfo(n)));
    }

    public ValueTask<ReadResult> ReadFile(string fileName, Memory<byte> buffer, ulong offset,
        FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info);
        if (n == null) return new(ReadResult.Error(NtStatus.ObjectNameNotFound));
        if (offset >= n.FileSize) return new(ReadResult.EndOfFile());
        ulong end = offset + (ulong)buffer.Length;
        if (end > n.FileSize) end = n.FileSize;
        int len = (int)(end - offset);
        new ReadOnlySpan<byte>(n.FileData!, (int)offset, len).CopyTo(buffer.Span);
        return new(ReadResult.Success((uint)len));
    }

    public ValueTask<WriteResult> WriteFile(string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool writeToEndOfFile, bool constrainedIo, FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info);
        if (n == null) return new(WriteResult.Error(NtStatus.ObjectNameNotFound));

        ulong off = offset;
        ulong end;
        if (constrainedIo)
        {
            if (off >= n.FileSize) return new(WriteResult.Success(0, MkInfo(n)));
            end = off + (ulong)buffer.Length;
            if (end > n.FileSize) end = n.FileSize;
        }
        else
        {
            if (writeToEndOfFile) off = n.FileSize;
            end = off + (ulong)buffer.Length;
            if (end > n.FileSize)
            {
                int s = SetFileSizeInternal(n, end, false);
                if (s != NtStatus.Success) return new(WriteResult.Error(s));
            }
        }

        int wlen = (int)(end - off);
        if (wlen > 0)
            buffer.Span[..wlen].CopyTo(new Span<byte>(n.FileData!, (int)off, wlen));
        return new(WriteResult.Success((uint)wlen, MkInfo(n)));
    }

    public ValueTask<FsResult> FlushFileBuffers(string? fileName, FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info);
        return new(n != null ? FsResult.Success(MkInfo(n)) : FsResult.Success());
    }

    public ValueTask<FsResult> GetFileInformation(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info);
        if (n == null) return new(FsResult.Error(NtStatus.ObjectNameNotFound));
        return new(FsResult.Success(MkInfo(n)));
    }

    public ValueTask<FsResult> SetFileAttributes(string fileName, uint fileAttributes,
        ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime,
        FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info);
        if (n == null) return new(FsResult.Error(NtStatus.ObjectNameNotFound));
        if (fileAttributes != unchecked((uint)-1)) n.FileAttributes = fileAttributes;
        if (creationTime    != 0) n.CreationTime    = creationTime;
        if (lastAccessTime  != 0) n.LastAccessTime  = lastAccessTime;
        if (lastWriteTime   != 0) n.LastWriteTime   = lastWriteTime;
        if (changeTime      != 0) n.ChangeTime      = changeTime;
        return new(FsResult.Success(MkInfo(n)));
    }

    public ValueTask<FsResult> SetFileSize(string fileName, ulong newSize, bool setAllocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info);
        if (n == null) return new(FsResult.Error(NtStatus.ObjectNameNotFound));
        int s = SetFileSizeInternal(n, newSize, setAllocationSize);
        if (s != NtStatus.Success) return new(FsResult.Error(s));
        return new(FsResult.Success(MkInfo(n)));
    }

    private int SetFileSizeInternal(MemfsNode n, ulong newSize, bool setAlloc)
    {
        if (setAlloc)
        {
            if (n.AllocationSize != newSize)
            {
                if (newSize > _maxFileSize) return NtStatus.DiskFull;
                if (newSize == 0)
                {
                    n.FileData = null;
                }
                else
                {
                    var newData = new byte[(int)newSize];
                    if (n.FileData != null)
                        Buffer.BlockCopy(n.FileData, 0, newData, 0,
                            (int)Math.Min((long)newSize, n.FileData.LongLength));
                    n.FileData = newData;
                }
                n.AllocationSize = newSize;
                if (n.FileSize > newSize) n.FileSize = newSize;
            }
        }
        else
        {
            if (n.FileSize != newSize)
            {
                if (n.AllocationSize < newSize)
                {
                    ulong allocSize = ((newSize + (ulong)ALLOCATION_UNIT - 1) / (ulong)ALLOCATION_UNIT) * (ulong)ALLOCATION_UNIT;
                    int r = SetFileSizeInternal(n, allocSize, true);
                    if (r != NtStatus.Success) return r;
                }
                if (n.FileSize < newSize && n.FileData != null)
                    Array.Clear(n.FileData, (int)n.FileSize, (int)(newSize - n.FileSize));
                n.FileSize = newSize;
            }
        }
        return NtStatus.Success;
    }

    public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info);
        if (n == null) return new(NtStatus.ObjectNameNotFound);
        lock (_mapLock)
        {
            if (HasChild(n)) return new(NtStatus.DirectoryNotEmpty);
        }
        return new(NtStatus.Success);
    }

    public ValueTask<int> MoveFile(string fileName, string newFileName, bool replaceIfExists,
        FileOperationInfo info, CancellationToken ct)
    {
        lock (_mapLock)
        {
            if (!_nodes.TryGetValue(fileName, out var node))
                return new(NtStatus.ObjectNameNotFound);

            _nodes.TryGetValue(newFileName, out var newNode);
            if (newNode != null && !ReferenceEquals(node, newNode))
            {
                if (!replaceIfExists) return new(NtStatus.ObjectNameCollision);
                if ((newNode.FileAttributes & (uint)FileAttributes.Directory) != 0)
                    return new(NtStatus.AccessDenied);
            }

            var descendants = new List<MemfsNode>();
            string srcKey = node.FileName;
            foreach (var kv in _nodes)
            {
                if (MemfsFileNameComparer.HasPrefix(kv.Key, srcKey, true))
                    descendants.Add(kv.Value);
            }

            int srcLen = srcKey.Length;
            int dstLen = newFileName.Length;
            foreach (var d in descendants)
            {
                if (d.FileName.Length - srcLen + dstLen >= 512)
                    return new(NtStatus.ObjectNameInvalid);
            }

            if (newNode != null && !ReferenceEquals(node, newNode))
            {
                _nodes.Remove(newNode.FileName);
                Dereference(newNode);
            }

            foreach (var d in descendants)
            {
                _nodes.Remove(d.FileName);
                d.FileName = newFileName + d.FileName[srcLen..];
            }
            foreach (var d in descendants)
            {
                _nodes.Add(d.FileName, d);
            }

            return new(NtStatus.Success);
        }
    }

    public void Cleanup(string? fileName, FileOperationInfo info, CleanupFlags flags)
    {
        var n = N(info);
        if (n == null) return;

        if (flags.HasFlag(CleanupFlags.SetArchiveBit))
        {
            if ((n.FileAttributes & (uint)FileAttributes.Directory) == 0)
                n.FileAttributes |= (uint)FileAttributes.Archive;
        }
        if ((flags & (CleanupFlags.SetLastAccessTime | CleanupFlags.SetLastWriteTime | CleanupFlags.SetChangeTime)) != 0)
        {
            ulong now = MemfsGetSystemTime();
            if (flags.HasFlag(CleanupFlags.SetLastAccessTime)) n.LastAccessTime = now;
            if (flags.HasFlag(CleanupFlags.SetLastWriteTime))  n.LastWriteTime  = now;
            if (flags.HasFlag(CleanupFlags.SetChangeTime))     n.ChangeTime     = now;
        }
        if (flags.HasFlag(CleanupFlags.SetAllocationSize))
        {
            ulong au = (ulong)ALLOCATION_UNIT;
            ulong allocSize = (n.FileSize + au - 1) / au * au;
            SetFileSizeInternal(n, allocSize, true);
        }
        if (flags.HasFlag(CleanupFlags.Delete))
        {
            lock (_mapLock)
            {
                if (!HasChild(n))
                {
                    if (_nodes.Remove(n.FileName))
                    {
                        TouchParent(n);
                        Dereference(n);
                    }
                }
            }
        }
    }

    public void Close(FileOperationInfo info)
    {
        var n = N(info);
        if (n != null) Dereference(n);
        info.Context = null;
    }

    public ValueTask<ReadDirectoryResult> ReadDirectory(string fileName, string? pattern, string? marker,
        nint buffer, uint length, FileOperationInfo info, CancellationToken ct)
    {
        var dir = N(info);
        if (dir == null || (dir.FileAttributes & (uint)FileAttributes.Directory) == 0)
            return new(ReadDirectoryResult.Error(NtStatus.ObjectPathNotFound));

        uint bt = 0;
        bool isRoot = dir.FileName == "\\";

        List<MemfsNode> children;
        MemfsNode? parent;
        lock (_mapLock)
        {
            children = ChildrenOf(dir);
            parent = isRoot ? null : ParentOf(dir);
        }

        if (!isRoot)
        {
            if (marker == null)
            {
                if (!AddDirInfoStr(dir, ".", buffer, length, &bt))
                    return new(ReadDirectoryResult.Success(bt));
            }
            if (marker == null || marker == ".")
            {
                if (parent != null && !AddDirInfoStr(parent, "..", buffer, length, &bt))
                    return new(ReadDirectoryResult.Success(bt));
                marker = null;
            }
        }

        foreach (var child in children)
        {
            string leaf = LeafOf(child.FileName);
            if (marker != null
                && MemfsFileNameComparer.CompareStatic(leaf, marker, true) <= 0)
                continue;
            if (!AddDirInfoStr(child, leaf, buffer, length, &bt))
                return new(ReadDirectoryResult.Success(bt));
        }
        WinFspFileSystem.EndDirInfo(buffer, length, &bt);
        return new(ReadDirectoryResult.Success(bt));
    }

    public int GetDirInfoByName(string dirName, string entryName, out FspDirInfo dirInfo, FileOperationInfo info)
    {
        var parent = N(info);
        if (parent == null)
        {
            dirInfo = default;
            return NtStatus.ObjectNameNotFound;
        }
        string fullPath = parent.FileName == "\\"
            ? "\\" + entryName
            : parent.FileName + "\\" + entryName;

        MemfsNode? n;
        lock (_mapLock) _nodes.TryGetValue(fullPath, out n);
        if (n == null) { dirInfo = default; return NtStatus.ObjectNameNotFound; }

        dirInfo = new FspDirInfo();
        dirInfo.FileInfo = MkInfo(n);
        dirInfo.SetFileName(LeafOf(n.FileName));
        return NtStatus.Success;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static MemfsNode? N(FileOperationInfo info) => info.Context as MemfsNode;

    private MemfsNode NewNode(string fileName)
    {
        ulong now = MemfsGetSystemTime();
        return new MemfsNode
        {
            FileName = fileName,
            CreationTime = now,
            LastAccessTime = now,
            LastWriteTime = now,
            ChangeTime = now,
            IndexNumber = (ulong)Interlocked.Increment(ref _nextIndexNumber),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FspFileInfo MkInfo(MemfsNode n) => new()
    {
        FileAttributes = n.FileAttributes,
        ReparseTag = n.ReparseTag,
        AllocationSize = n.AllocationSize,
        FileSize = n.FileSize,
        CreationTime = n.CreationTime,
        LastAccessTime = n.LastAccessTime,
        LastWriteTime = n.LastWriteTime,
        ChangeTime = n.ChangeTime,
        IndexNumber = n.IndexNumber,
        HardLinks = 0,
        EaSize = 0,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MemfsGetSystemTime()
    {
        GetSystemTimeAsFileTime(out var ft);
        return ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
    }

    [DllImport("kernel32.dll")]
    private static extern void GetSystemTimeAsFileTime(out FILETIME lpSystemTimeAsFileTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public int dwLowDateTime; public int dwHighDateTime; }

    private static string LeafOf(string fullPath)
    {
        if (fullPath.Length <= 1) return fullPath;
        int i = fullPath.LastIndexOf('\\');
        return i < 0 ? fullPath : fullPath[(i + 1)..];
    }

    private int GetParentStatus(string fileName)
    {
        if (fileName.Length <= 1) return NtStatus.ObjectNameNotFound;
        int i = fileName.LastIndexOf('\\');
        if (i <= 0) return NtStatus.ObjectNameNotFound;
        string parent = fileName[..i];
        if (parent.Length == 0) parent = "\\";
        MemfsNode? p;
        lock (_mapLock) _nodes.TryGetValue(parent, out p);
        if (p == null) return NtStatus.ObjectPathNotFound;
        if ((p.FileAttributes & (uint)FileAttributes.Directory) == 0) return NtStatus.NotADirectory;
        return NtStatus.ObjectNameNotFound;
    }

    private MemfsNode? ParentOf(MemfsNode n)
    {
        if (n.FileName == "\\") return null;
        int i = n.FileName.LastIndexOf('\\');
        string parent = i <= 0 ? "\\" : n.FileName[..i];
        _nodes.TryGetValue(parent, out var p);
        return p;
    }

    private void TouchParent(MemfsNode n)
    {
        var p = ParentOf(n);
        if (p == null) return;
        ulong now = MemfsGetSystemTime();
        p.LastAccessTime = now;
        p.LastWriteTime = now;
        p.ChangeTime = now;
    }

    private bool HasChild(MemfsNode n)
    {
        string prefix = n.FileName == "\\" ? "\\" : n.FileName;
        bool seekedPast = false;
        foreach (var kv in _nodes)
        {
            if (!seekedPast)
            {
                if (MemfsFileNameComparer.CompareStatic(kv.Key, prefix, true) <= 0) continue;
                seekedPast = true;
            }
            int j = kv.Key.LastIndexOf('\\');
            string parent = j <= 0 ? "\\" : kv.Key[..j];
            return MemfsFileNameComparer.CompareStatic(parent, prefix, true) == 0;
        }
        return false;
    }

    private List<MemfsNode> ChildrenOf(MemfsNode dir)
    {
        var res = new List<MemfsNode>();
        string dirPath = dir.FileName;
        foreach (var kv in _nodes)
        {
            if (!MemfsFileNameComparer.HasPrefix(kv.Key, dirPath, true)) continue;
            if (kv.Key.Length == dirPath.Length) continue;
            int parentEnd = kv.Key.LastIndexOf('\\');
            string parent = parentEnd <= 0 ? "\\" : kv.Key[..parentEnd];
            if (MemfsFileNameComparer.CompareStatic(parent, dirPath, true) == 0)
                res.Add(kv.Value);
        }
        return res;
    }

    private bool AddDirInfoStr(MemfsNode n, string name, nint buffer, uint length, uint* pBt)
    {
        var di = new FspDirInfo();
        di.FileInfo = MkInfo(n);
        di.SetFileName(name);
        return WinFspFileSystem.AddDirInfo(&di, buffer, length, pBt);
    }

    private void Dereference(MemfsNode n)
    {
        if (Interlocked.Decrement(ref n.RefCount) == 0)
        {
            n.FileData = null;
            n.ReparseData = null;
        }
    }
}
