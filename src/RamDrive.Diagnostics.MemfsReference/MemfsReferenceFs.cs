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
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
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

    // SECURITY_INFORMATION bit 0x1 == OWNER_SECURITY_INFORMATION.
    private const uint OWNER_SECURITY_INFORMATION = 0x00000001;
    // WinFsp.Native's NtStatus does not define this. Value confirmed by winfsp's own .NET binding
    // (winfsp src/dotnet/FileSystemBase+Const.cs:319): STATUS_INVALID_OWNER = 0xC000005A.
    private const int STATUS_INVALID_OWNER = unchecked((int)0xC000005A);

    private readonly long _capacityBytes;
    private readonly ulong _maxFileSize;
    private readonly SortedDictionary<string, MemfsNode> _nodes;
    private readonly object _mapLock = new();
    private long _nextIndexNumber = 1;
    private string _volumeLabel = "MEMFS";

    // Cached root SD bytes — used as the defensive fallback "current SD" in SetFileSecurity so the
    // owner-change helper has a stable signature matching the production adapter.
    private readonly byte[] _defaultRootSdBytes;

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
        _defaultRootSdBytes = sdBytes;
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
        // LOCKSTEP with WinFspRamAdapter: a named stream reports its base's attributes and
        // security descriptor (one per file on NTFS); the Directory bit never propagates.
        var meta = MetadataOf(n);
        fileAttributes = ReferenceEquals(meta, n)
            ? meta.FileAttributes
            : meta.FileAttributes & ~(uint)FileAttributes.Directory;
        securityDescriptor = meta.FileSecurity;
        return NtStatus.Success;
    }

    public ValueTask<SecurityByNameResult> GetFileSecurityByName(string fileName, bool getSecurityDescriptor, CancellationToken ct)
    {
        byte[]? sd = null;
        int status = GetFileSecurityByName(fileName, out uint attributes, ref sd);
        return new(status == NtStatus.Success
            ? SecurityByNameResult.Success(attributes, getSecurityDescriptor ? sd : null)
            : SecurityByNameResult.Error(status));
    }

    public int GetReparsePointByName(string fileName, bool isDirectory, out byte[]? data)
    {
        lock (_mapLock)
        {
            _nodes.TryGetValue(fileName, out var node);
            data = node?.ReparseData;
            return node == null ? NtStatus.ObjectNameNotFound : data == null ? NtStatus.NotAReparse : NtStatus.Success;
        }
    }

    public int GetReparsePoint(string fileName, ref byte[]? data, FileOperationInfo info)
    {
        lock (_mapLock)
        {
            var node = N(info);
            data = node?.ReparseData;
            return node == null ? NtStatus.ObjectNameNotFound : data == null ? NtStatus.NotAReparse : NtStatus.Success;
        }
    }

    public int SetReparsePoint(string fileName, byte[] data, FileOperationInfo info)
    {
        var node = N(info);
        if (node == null) return NtStatus.ObjectNameNotFound;
        int status = ValidateReparseData(data, (node.FileAttributes & 0x10) != 0, false);
        if (status != 0) return status;
        lock (_mapLock)
        {
            if (HasChild(node)) return NtStatus.DirectoryNotEmpty;
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (node.ReparseData != null && node.ReparseTag != tag) return unchecked((int)0xC0000277);
            if (node.FileSize != 0) return unchecked((int)0xC0000278);
            node.ReparseData = (byte[])data.Clone();
            node.ReparseTag = tag;
            node.FileAttributes |= (uint)FileAttributes.ReparsePoint;
            return NtStatus.Success;
        }
    }

    public int DeleteReparsePoint(string fileName, byte[] data, FileOperationInfo info)
    {
        var node = N(info);
        if (node == null) return NtStatus.ObjectNameNotFound;
        int status = ValidateReparseData(data, (node.FileAttributes & 0x10) != 0, true);
        if (status != 0) return status;
        lock (_mapLock)
        {
            if (node.ReparseData == null) return NtStatus.NotAReparse;
            if (node.ReparseTag != BinaryPrimitives.ReadUInt32LittleEndian(data)) return unchecked((int)0xC0000277);
            node.ReparseData = null;
            node.ReparseTag = 0;
            node.FileAttributes &= ~(uint)FileAttributes.ReparsePoint;
            return NtStatus.Success;
        }
    }

    private static int ValidateReparseData(byte[] data, bool isDirectory, bool deleting)
    {
        const int invalidData = unchecked((int)0xC0000278), invalidTag = unchecked((int)0xC0000276);
        if (data.Length < 8 || data.Length > 16384 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) != data.Length - 8 ||
            (deleting && data.Length != 8)) return invalidData;
        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (tag != 0xA0000003 && tag != 0xA000000C) return invalidTag;
        if (deleting) return NtStatus.Success;
        if (tag == 0xA0000003 && !isDirectory) return NtStatus.InvalidParameter;
        int start = tag == 0xA0000003 ? 16 : 20;
        if (data.Length < start) return invalidData;
        if (start == 20 && (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)) & ~1u) != 0) return invalidData;
        int a = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        int b = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
        int c = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
        int d = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
        if (b == 0 || ((a | b | c | d) & 1) != 0 || a + b > data.Length - start || c + d > data.Length - start)
            return invalidData;
        if (tag == 0xA0000003 && b > 16384 - 20 &&
            System.Text.Encoding.Unicode.GetString(data.AsSpan(start + a, b))
                .StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase)) return invalidData;
        return NtStatus.Success;
    }

    public int GetFileSecurity(string fileName, ref byte[]? securityDescriptor, FileOperationInfo info)
    {
        var n = N(info);
        if (n == null) return NtStatus.ObjectNameNotFound;
        // LOCKSTEP: a named stream's security descriptor is the base's single SD.
        securityDescriptor = MetadataOf(n).FileSecurity;
        return NtStatus.Success;
    }

    public int SetFileSecurity(string fileName, uint securityInformation, byte[] modificationDescriptor, FileOperationInfo info)
    {
        var n = N(info);
        if (n == null) return NtStatus.ObjectNameNotFound;

        // LOCKSTEP: setting security through a stream handle changes the base's single SD.
        var meta = MetadataOf(n);
        byte[] currentSd = meta.FileSecurity ?? _defaultRootSdBytes;
        var modification = new RawSecurityDescriptor(modificationDescriptor, 0);

        // LOCKSTEP with WinFspRamAdapter.SetFileSecurity: NTFS rejects owner reassignment by a
        // non-privileged caller (STATUS_INVALID_OWNER, atomic). Both adapters MUST return the same
        // NTSTATUS for the same input or Comparators.CompareStatus("SetFileSecurity") throws.
        if (IsRejectedOwnerChange(securityInformation, currentSd, modification))
            return STATUS_INVALID_OWNER;

        var existing = new RawSecurityDescriptor(currentSd, 0);
        if ((securityInformation & 1) != 0) existing.Owner = modification.Owner;
        if ((securityInformation & 2) != 0) existing.Group = modification.Group;
        if ((securityInformation & 4) != 0) existing.DiscretionaryAcl = modification.DiscretionaryAcl;
        if ((securityInformation & 8) != 0) existing.SystemAcl = modification.SystemAcl;
        var result = new byte[existing.BinaryLength];
        existing.GetBinaryForm(result, 0);
        meta.FileSecurity = result;
        return NtStatus.Success;
    }

    // LOCKSTEP: identical rule in WinFspRamAdapter.IsRejectedOwnerChange. Reject any owner *change*
    // (OWNER bit set AND new owner SID differs from current) to approximate NTFS owner-assignment
    // privilege, which WinFsp cannot enforce (no caller token at SetSecurity). See spec
    // security-owner-enforcement.
    private static bool IsRejectedOwnerChange(uint securityInformation, byte[] currentSd, RawSecurityDescriptor modification)
    {
        if ((securityInformation & OWNER_SECURITY_INFORMATION) == 0) return false;
        SecurityIdentifier? newOwner = modification.Owner;
        if (newOwner == null) return false;
        SecurityIdentifier? curOwner = new RawSecurityDescriptor(currentSd, 0).Owner;
        return !Equals(newOwner, curOwner);
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
            // LOCKSTEP with FileNode.BaseNode: a stream node captures its base at creation
            // so the shared-metadata routing (MetadataOf) survives the base being unlinked.
            int colon = LeafOf(fileName).IndexOf(':');
            if (colon >= 0)
            {
                int back = fileName.LastIndexOf('\\');
                string baseKey = back < 0 ? "\\" : fileName[..(back + 1 + colon)];
                _nodes.TryGetValue(baseKey, out n.BaseNode);
            }
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

            info.Context = new HandleContext { Node = n };
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
        info.Context = new HandleContext { Node = n };
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

        // LOCKSTEP with WinFspRamAdapter.OverwriteFile: attributes and timestamps are the
        // shared (base) metadata.
        var meta = MetadataOf(n);
        if (replaceFileAttributes)
            meta.FileAttributes = fileAttributes | (uint)FileAttributes.Archive;
        else
            meta.FileAttributes |= fileAttributes | (uint)FileAttributes.Archive;

        n.FileSize = 0;
        ulong now = MemfsGetSystemTime();
        meta.LastAccessTime = meta.LastWriteTime = meta.ChangeTime = now;

        // LOCKSTEP: NTFS removes the file's other data streams when the MAIN stream is
        // truncated on open (TRUNCATE_EXISTING); a stream still open by a handle (RefCount
        // > 1: one per namespace entry plus one per open context) survives.
        if (LeafOf(n.FileName).IndexOf(':') < 0)
        {
            string prefix = n.FileName == "\\" ? "\\:" : n.FileName + ":";
            lock (_mapLock)
            {
                var closed = new List<MemfsNode>();
                foreach (var kv in _nodes)
                    if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && kv.Value.RefCount <= 1)
                        closed.Add(kv.Value);
                foreach (var stream in closed)
                {
                    _nodes.Remove(stream.FileName);
                    TouchParent(stream);
                    Dereference(stream);
                }
            }
        }
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
        // LOCKSTEP: common metadata (attributes, timestamps) is the base's for a stream.
        var meta = MetadataOf(n);
        // LOCKSTEP with the production FileNode.Attributes setter: a SetFileAttributes
        // call can never set or clear the ReparsePoint bit (reparse state changes only
        // through the reparse point operations).
        if (fileAttributes != unchecked((uint)-1))
            meta.FileAttributes = (fileAttributes & ~(uint)FileAttributes.ReparsePoint) | (meta.FileAttributes & (uint)FileAttributes.ReparsePoint);
        if (creationTime    != 0) meta.CreationTime    = creationTime;
        if (lastAccessTime  != 0) meta.LastAccessTime  = lastAccessTime;
        if (lastWriteTime   != 0) meta.LastWriteTime   = lastWriteTime;
        if (changeTime      != 0) meta.ChangeTime      = changeTime;
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
        var hctx = info.Context as HandleContext;
        if (hctx == null) return;
        var n = hctx.Node;

        // LOCKSTEP: attributes and timestamps are the shared (base) metadata.
        var meta = MetadataOf(n);
        if (flags.HasFlag(CleanupFlags.SetArchiveBit))
        {
            if ((meta.FileAttributes & (uint)FileAttributes.Directory) == 0)
                meta.FileAttributes |= (uint)FileAttributes.Archive;
        }
        if ((flags & (CleanupFlags.SetLastAccessTime | CleanupFlags.SetLastWriteTime | CleanupFlags.SetChangeTime)) != 0)
        {
            ulong now = MemfsGetSystemTime();
            if (flags.HasFlag(CleanupFlags.SetLastAccessTime)) meta.LastAccessTime = now;
            if (flags.HasFlag(CleanupFlags.SetLastWriteTime))  meta.LastWriteTime  = now;
            if (flags.HasFlag(CleanupFlags.SetChangeTime))     meta.ChangeTime     = now;
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

        // LOCKSTEP with WinFspRamAdapter.Cleanup: count the Cleanup, then release the
        // open reference exactly once (Close releases it for handles whose Cleanup the
        // driver suppressed — unmodified handles). The node's data is NOT freed here
        // (Dereference only marks it dead); Close frees it.
        Interlocked.Increment(ref n.CleanupCount);
        ReleaseHandleReference(hctx);
    }

    public void Close(FileOperationInfo info)
    {
        var hctx = info.Context as HandleContext;
        if (hctx != null)
        {
            var n = hctx.Node;
            // LOCKSTEP with WinFspRamAdapter.Close: release the reference for handles
            // whose Cleanup was suppressed, then finalize the node.
            ReleaseHandleReference(hctx);
            Interlocked.Increment(ref n.CloseCount);
            // Free the data only when the node is dead AND every Close has arrived —
            // a pending Close may be deferred by a live Cc section / memory mapping
            // (only the section owner's Close is deferred; other handles' Closes
            // arrive early, with the section still alive).
            if (n.Dead && n.CloseCount >= n.CleanupCount)
            {
                n.FileData = null;
                n.ReparseData = null;
            }
        }
        info.Context = null;
    }

    // Release this handle's open reference exactly once: on the Cleanup transact when
    // the driver delivers one, otherwise on Close (the driver suppresses the Cleanup
    // transact for unmodified handles, PostCleanupWhenModifiedOnly).
    private void ReleaseHandleReference(HandleContext hctx)
    {
        if (hctx.ReferenceReleased) return;
        hctx.ReferenceReleased = true;
        Dereference(hctx.Node);
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

    public ValueTask<DirInfoByNameResult> GetDirInfoByName(string dirName, string entryName, FileOperationInfo info, CancellationToken ct)
    {
        int status = GetDirInfoByName(dirName, entryName, out var dirInfo, info);
        return new(status == NtStatus.Success ? DirInfoByNameResult.Success(dirInfo) : DirInfoByNameResult.Error(status));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static MemfsNode? N(FileOperationInfo info) => (info.Context as HandleContext)?.Node;

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

    // LOCKSTEP with WinFspRamAdapter.MakeFileInfo: common metadata (attributes,
    // timestamps, file id) is shared with the base object; content and size are not.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private FspFileInfo MkInfo(MemfsNode n)
    {
        var meta = MetadataOf(n);
        // LOCKSTEP: a stream handle never carries the base's Directory bit.
        uint attributes = ReferenceEquals(meta, n)
            ? meta.FileAttributes
            : meta.FileAttributes & ~(uint)FileAttributes.Directory;
        return new()
        {
            FileAttributes = attributes,
            // LOCKSTEP with production MakeFileInfo: the reparse tag is shared metadata
            // (one MFT record per file) — a stream reports its BASE's tag.
            ReparseTag = meta.ReparseTag,
            AllocationSize = n.AllocationSize,
            FileSize = n.FileSize,
            CreationTime = meta.CreationTime,
            LastAccessTime = meta.LastAccessTime,
            LastWriteTime = meta.LastWriteTime,
            ChangeTime = meta.ChangeTime,
            IndexNumber = meta.IndexNumber,
            HardLinks = 0,
            EaSize = 0,
        };
    }

    /// <summary>
    /// The node holding the shared metadata (attributes, security descriptor, timestamps,
    /// file id) of <paramref name="n"/>: the base object for a named-stream node
    /// (LOCKSTEP with <c>FileNode.MetadataOwner</c> in the production adapter — NTFS keeps
    /// one MFT record per file). The base is a persistent reference captured at creation
    /// (<see cref="MemfsNode.BaseNode"/>), so it stays valid after the base is unlinked.
    /// </summary>
    private MemfsNode MetadataOf(MemfsNode n) => n.BaseNode ?? n;

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

    // Instance method: MkInfo routes a stream's common metadata through its base node.
    private bool AddDirInfoStr(MemfsNode n, string name, nint buffer, uint length, uint* pBt)
    {
        var di = new FspDirInfo();
        di.FileInfo = MkInfo(n);
        di.SetFileName(name);
        return WinFspFileSystem.AddDirInfo(&di, buffer, length, pBt);
    }

    private void Dereference(MemfsNode n)
    {
        // LOCKSTEP with FileNode lifetime (see MemfsNode.Dead): reaching zero only
        // marks the node dead. Its data is freed in Close, because the WinFsp FSD
        // defers the Close transact until every reference to the file node (handles
        // and Cc sections / memory mappings) is gone, and it preserves data for
        // memory-mapped I/O on a deleted file after Cleanup.
        if (Interlocked.Decrement(ref n.RefCount) == 0)
            n.Dead = true;
    }
}
