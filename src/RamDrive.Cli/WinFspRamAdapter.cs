using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using WinFsp.Native;

namespace RamDrive.Cli;

/// <summary>
/// WinFsp adapter backed by RamFileSystem. Implements <see cref="IFileSystem"/>
/// for use with <see cref="FileSystemHost"/>.
///
/// Design: zero managed heap allocation on hot path (Read/Write/GetFileInfo etc).
/// All ValueTask returns are synchronous-completed (no Task boxing).
/// FileNode is cached in <see cref="FileOperationInfo.Context"/>.
///
/// <para>
/// <b>Cache-invalidation matrix</b> — every path-mutating callback below sends an
/// <c>FspFileSystemNotify</c> via <see cref="FileSystemHost.Notify(uint, uint, string)"/>
/// after the user-mode mutation commits. This keeps the WinFsp kernel <c>FileInfo</c>
/// cache coherent with <see cref="RamFileSystem"/> state, even when
/// <see cref="RamDriveOptions.FileInfoTimeoutMs"/> is large or
/// <see cref="uint.MaxValue"/>. See <c>specs/cache-invalidation/spec.md</c>.
/// </para>
/// <list type="table">
///   <listheader><term>Callback</term><description>Filter / Action</description></listheader>
///   <item><term>CreateFile (file)</term><description>ChangeFileName / ActionAdded</description></item>
///   <item><term>CreateFile (dir)</term><description>ChangeDirName / ActionAdded</description></item>
///   <item><term>OverwriteFile</term><description>ChangeSize|ChangeLastWrite / ActionModified</description></item>
///   <item><term>SetFileSize (logical)</term><description>ChangeSize|ChangeLastWrite / ActionModified</description></item>
///   <item><term>SetFileAttributes</term><description>ChangeAttributes|ChangeLastWrite / ActionModified</description></item>
///   <item><term>MoveFile</term><description>(old) ChangeFileName/ActionRenamedOldName + (new) ChangeFileName/ActionRenamedNewName</description></item>
///   <item><term>Cleanup(Delete) (file)</term><description>ChangeFileName / ActionRemoved</description></item>
///   <item><term>Cleanup(Delete) (dir)</term><description>ChangeDirName / ActionRemoved</description></item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WinFspRamAdapter : IFileSystem
{
    /// <summary>
    /// Default root security descriptor (same as WinFsp memfs):
    /// Owner=Administrators, Group=Administrators, DACL grants full access to SYSTEM, Administrators, Everyone.
    /// </summary>
    private const string RootSddl = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)";

    private readonly RamFileSystem _fs;
    private readonly RamDriveOptions _options;
    private readonly ILogger<WinFspRamAdapter> _logger;
    private FileSystemHost? _host;

    public WinFspRamAdapter(RamFileSystem fs, IOptions<RamDriveOptions> options, ILogger<WinFspRamAdapter> logger)
    {
        _fs = fs;
        _options = options.Value;
        _logger = logger;

        // Set root directory security descriptor so WinFsp enforces ACLs
        var sd = new RawSecurityDescriptor(RootSddl);
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        _fs.SetRootSecurityDescriptor(bytes);
    }

    // ═══════════════════════════════════════════
    //  Lifecycle
    // ═══════════════════════════════════════════

    /// <summary>
    /// All I/O is purely in-memory — ValueTask always completes synchronously.
    /// This enables the zero-copy fast path in FileSystemHost.
    /// </summary>
    public bool SynchronousIo => true;

    public int Init(FileSystemHost host)
    {
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        // Notifications keep the kernel cache coherent; FileInfoTimeoutMs is defence in depth.
        // EnableKernelCache=false forces 0 (no cache) regardless of FileInfoTimeoutMs — backout switch.
        host.FileInfoTimeout = _options.EnableKernelCache ? _options.FileInfoTimeoutMs : 0u;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = true;
        host.FileSystemName = "NTFS";
        return NtStatus.Success;
    }

    public int Mounted(FileSystemHost host)
    {
        _host = host;
        _logger.LogInformation("Drive mounted at {MountPoint}", host.MountPoint);
        return NtStatus.Success;
    }

    public void Unmounted(FileSystemHost host)
    {
        _logger.LogInformation("Drive unmounted");
    }

    // ═══════════════════════════════════════════
    //  Volume
    // ═══════════════════════════════════════════

    public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
    {
        totalSize = (ulong)_fs.TotalBytes;
        freeSize = (ulong)_fs.FreeBytes;
        volumeLabel = _options.VolumeLabel;
        return NtStatus.Success;
    }

    // ═══════════════════════════════════════════
    //  Name lookup (sync, before Create/Open)
    // ═══════════════════════════════════════════

    public int GetFileSecurityByName(string fileName, out uint fileAttributes, ref byte[]? securityDescriptor)
    {
        var node = _fs.FindNode(fileName);
        if (node == null)
        {
            fileAttributes = 0;
            return NtStatus.ObjectNameNotFound;
        }

        fileAttributes = (uint)node.Attributes;
        securityDescriptor = node.SecurityDescriptor;
        return NtStatus.Success;
    }

    // ═══════════════════════════════════════════
    //  Create / Open / Overwrite
    // ═══════════════════════════════════════════

    public ValueTask<CreateResult> CreateFile(
        string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[]? securityDescriptor, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        bool isDir = (createOptions & (uint)CreateOptions.FileDirectoryFile) != 0;

        if (isDir)
        {
            var dir = _fs.CreateDirectory(fileName, securityDescriptor);
            if (dir == null)
                return V(CreateResult.Error(NtStatus.ObjectNameCollision));

            info.Context = dir;
            info.IsDirectory = true;
            // Cache invalidation: defeat negative cache for callers that probed this name
            // before it existed (leveldb, SQLite, etc. routinely do this).
            Notify(FileNotify.ChangeDirName, FileNotify.ActionAdded, fileName);
            return V(new CreateResult(NtStatus.Success, MakeFileInfo(dir)));
        }

        var file = _fs.CreateFile(fileName, securityDescriptor);
        if (file == null)
        {
            // Distinguish: parent not found vs name collision
            if (_fs.FindNode(fileName) != null)
                return V(CreateResult.Error(NtStatus.ObjectNameCollision));
            return V(CreateResult.Error(NtStatus.ObjectPathNotFound));
        }

        // allocationSize is a hint for pre-allocation, not logical file size.
        // Do not set _length — the file starts at size 0 and grows via WriteFile.

        if (fileAttributes != 0)
            file.Attributes = (FileAttributes)fileAttributes;

        info.Context = file;
        info.IsDirectory = false;
        // Cache invalidation: see comment above for the directory branch.
        Notify(FileNotify.ChangeFileName, FileNotify.ActionAdded, fileName);
        return V(new CreateResult(NtStatus.Success, MakeFileInfo(file)));
    }

    public ValueTask<CreateResult> OpenFile(
        string fileName, uint createOptions, uint grantedAccess,
        FileOperationInfo info, CancellationToken ct)
    {
        var node = _fs.FindNode(fileName);
        if (node == null)
            return V(CreateResult.Error(NtStatus.ObjectNameNotFound));

        info.Context = node;
        info.IsDirectory = node.IsDirectory;
        return V(new CreateResult(NtStatus.Success, MakeFileInfo(node)));
    }

    public ValueTask<FsResult> OverwriteFile(
        uint fileAttributes, bool replaceFileAttributes, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        var node = Node(info);
        if (node?.Content == null)
            return V(FsResult.Error(NtStatus.ObjectNameNotFound));

        node.Content.SetLength(0);

        // Early capacity check: if the caller hints at the final file size,
        // fail fast before the copy begins rather than mid-write.
        if (allocationSize > 0 && (long)allocationSize > _fs.FreeBytes)
            return V(FsResult.Error(NtStatus.DiskFull));

        if (replaceFileAttributes && fileAttributes != 0)
            node.Attributes = (FileAttributes)fileAttributes;
        else if (fileAttributes != 0)
            node.Attributes |= (FileAttributes)fileAttributes;

        node.LastWriteTime = DateTime.UtcNow;
        // Cache invalidation: size went to 0; subsequent reads must not see the pre-overwrite cached size.
        if (info.FileName is { } path)
            Notify(FileNotify.ChangeSize | FileNotify.ChangeLastWrite, FileNotify.ActionModified, path);
        return V(FsResult.Success(MakeFileInfo(node)));
    }

    // ═══════════════════════════════════════════
    //  Read / Write / Flush
    // ═══════════════════════════════════════════

    public ValueTask<ReadResult> ReadFile(
        string fileName, Memory<byte> buffer, ulong offset,
        FileOperationInfo info, CancellationToken ct)
    {
        var node = Node(info);
        if (node?.Content == null)
            return V(ReadResult.Error(NtStatus.ObjectNameNotFound));

        long fileLength = node.Content.Length;
        if ((long)offset >= fileLength)
            return V(ReadResult.EndOfFile());

        int toRead = (int)Math.Min(buffer.Length, fileLength - (long)offset);
        int bytesRead = node.Content.Read((long)offset, buffer.Span[..toRead]);
        node.LastAccessTime = DateTime.UtcNow;
        return V(ReadResult.Success((uint)bytesRead));
    }

    public ValueTask<WriteResult> WriteFile(
        string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool writeToEndOfFile, bool constrainedIo,
        FileOperationInfo info, CancellationToken ct)
    {
        var node = Node(info);
        if (node?.Content == null)
            return V(WriteResult.Error(NtStatus.ObjectNameNotFound));

        long writeOffset = writeToEndOfFile ? node.Content.Length : (long)offset;

        int writeLength = buffer.Length;
        if (constrainedIo)
        {
            long fileLength = node.Content.Length;
            if (writeOffset >= fileLength)
                return V(WriteResult.Success(0, MakeFileInfo(node)));
            writeLength = (int)Math.Min(writeLength, fileLength - writeOffset);
        }

        int written = node.Content.Write(writeOffset, buffer.Span[..writeLength]);
        if (written < 0)
            return V(WriteResult.Error(NtStatus.DiskFull));

        node.LastWriteTime = DateTime.UtcNow;
        return V(WriteResult.Success((uint)written, MakeFileInfo(node)));
    }

    public ValueTask<FsResult> FlushFileBuffers(
        string? fileName, FileOperationInfo info, CancellationToken ct)
        => V(FsResult.Success());

    // ═══════════════════════════════════════════
    //  Metadata
    // ═══════════════════════════════════════════

    public ValueTask<FsResult> GetFileInformation(
        string fileName, FileOperationInfo info, CancellationToken ct)
    {
        var node = Node(info);
        if (node == null)
            return V(FsResult.Error(NtStatus.ObjectNameNotFound));

        return V(FsResult.Success(MakeFileInfo(node)));
    }

    public ValueTask<FsResult> SetFileAttributes(
        string fileName,
        uint fileAttributes, ulong creationTime, ulong lastAccessTime,
        ulong lastWriteTime, ulong changeTime,
        FileOperationInfo info, CancellationToken ct)
    {
        var node = Node(info);
        if (node == null)
            return V(FsResult.Error(NtStatus.ObjectNameNotFound));

        // unchecked((uint)-1) means "don't change"
        if (fileAttributes != unchecked((uint)(-1)) && fileAttributes != 0)
            node.Attributes = (FileAttributes)fileAttributes;

        if (creationTime != 0) node.CreationTime = DateTime.FromFileTimeUtc((long)creationTime);
        if (lastAccessTime != 0) node.LastAccessTime = DateTime.FromFileTimeUtc((long)lastAccessTime);
        if (lastWriteTime != 0) node.LastWriteTime = DateTime.FromFileTimeUtc((long)lastWriteTime);
        // changeTime: we don't track separately

        Notify(FileNotify.ChangeAttributes | FileNotify.ChangeLastWrite, FileNotify.ActionModified, fileName);
        return V(FsResult.Success(MakeFileInfo(node)));
    }

    public ValueTask<FsResult> SetFileSize(
        string fileName, ulong newSize, bool setAllocationSize,
        FileOperationInfo info, CancellationToken ct)
    {
        var node = Node(info);
        if (node?.Content == null)
            return V(FsResult.Error(NtStatus.ObjectNameNotFound));

        if (setAllocationSize)
        {
            // SetAllocationSize is called before writes (e.g., during file copy).
            // Check if the requested allocation fits — this lets the OS report DISK_FULL
            // before starting the copy. No pages are reserved; actual allocation happens on Write.
            long additional = (long)newSize - node.Size;
            if (additional > 0 && additional > _fs.FreeBytes)
                return V(FsResult.Error(NtStatus.DiskFull));
            return V(FsResult.Success(MakeFileInfo(node)));
        }

        if (!node.Content.SetLength((long)newSize))
            return V(FsResult.Error(NtStatus.DiskFull));

        node.LastWriteTime = DateTime.UtcNow;
        Notify(FileNotify.ChangeSize | FileNotify.ChangeLastWrite, FileNotify.ActionModified, fileName);
        return V(FsResult.Success(MakeFileInfo(node)));
    }

    // ═══════════════════════════════════════════
    //  Security
    // ═══════════════════════════════════════════

    public int GetFileSecurity(string fileName, ref byte[]? securityDescriptor, FileOperationInfo info)
    {
        var node = Node(info);
        if (node == null)
            return NtStatus.ObjectNameNotFound;

        securityDescriptor = node.SecurityDescriptor;
        return NtStatus.Success;
    }

    public int SetFileSecurity(string fileName, uint securityInformation, byte[] modificationDescriptor, FileOperationInfo info)
    {
        var node = Node(info);
        if (node == null)
            return NtStatus.ObjectNameNotFound;

        var existing = node.SecurityDescriptor != null
            ? new RawSecurityDescriptor(node.SecurityDescriptor, 0)
            : new RawSecurityDescriptor(RootSddl);
        var modification = new RawSecurityDescriptor(modificationDescriptor, 0);

        // Merge based on SECURITY_INFORMATION flags
        if ((securityInformation & 1) != 0) existing.Owner = modification.Owner;       // OWNER_SECURITY_INFORMATION
        if ((securityInformation & 2) != 0) existing.Group = modification.Group;       // GROUP_SECURITY_INFORMATION
        if ((securityInformation & 4) != 0) existing.DiscretionaryAcl = modification.DiscretionaryAcl; // DACL_SECURITY_INFORMATION
        if ((securityInformation & 8) != 0) existing.SystemAcl = modification.SystemAcl;               // SACL_SECURITY_INFORMATION

        var result = new byte[existing.BinaryLength];
        existing.GetBinaryForm(result, 0);
        node.SecurityDescriptor = result;
        return NtStatus.Success;
    }

    // ═══════════════════════════════════════════
    //  Delete / Move
    // ═══════════════════════════════════════════

    public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        var node = Node(info);
        if (node == null)
            return V(NtStatus.ObjectNameNotFound);

        if (node.IsDirectory && node.Children!.Count > 0)
            return V(NtStatus.DirectoryNotEmpty);

        return V(NtStatus.Success);
    }

    public ValueTask<int> MoveFile(
        string fileName, string newFileName, bool replaceIfExists,
        FileOperationInfo info, CancellationToken ct)
    {
        if (_fs.Move(fileName, newFileName, replaceIfExists))
        {
            // Update cached node — name changed
            var node = Node(info);
            if (node != null) info.Context = node; // still valid reference
            // Cache invalidation MUST happen after the lock is released (Move returned).
            // Without ActionRenamedNewName the kernel's negative cache for newFileName
            // (populated by leveldb's pre-rename existence probe) survives the rename and
            // a subsequent ReadFile returns 0 bytes — see fix-leveldb-cache-coherency.
            Notify(FileNotify.ChangeFileName, FileNotify.ActionRenamedOldName, fileName);
            Notify(FileNotify.ChangeFileName, FileNotify.ActionRenamedNewName, newFileName);
            return V(NtStatus.Success);
        }
        return V(NtStatus.ObjectNameCollision);
    }

    // ═══════════════════════════════════════════
    //  Lifecycle (sync)
    // ═══════════════════════════════════════════

    public void Cleanup(string? fileName, FileOperationInfo info, CleanupFlags flags)
    {
        if (flags.HasFlag(CleanupFlags.Delete) && fileName != null)
        {
            // Capture IsDirectory BEFORE Delete disposes the node.
            bool wasDir = info.IsDirectory;
            _fs.Delete(fileName);
            Notify(wasDir ? FileNotify.ChangeDirName : FileNotify.ChangeFileName,
                FileNotify.ActionRemoved, fileName);
        }

        var node = Node(info);
        if (node == null) return;

        if (flags.HasFlag(CleanupFlags.SetLastWriteTime))
            node.LastWriteTime = DateTime.UtcNow;
        if (flags.HasFlag(CleanupFlags.SetLastAccessTime))
            node.LastAccessTime = DateTime.UtcNow;
        if (flags.HasFlag(CleanupFlags.SetChangeTime))
            node.LastWriteTime = DateTime.UtcNow; // reuse LastWriteTime for ChangeTime
    }

    public void Close(FileOperationInfo info)
    {
        info.Context = null;
    }

    // ═══════════════════════════════════════════
    //  Directory
    // ═══════════════════════════════════════════

    public ValueTask<ReadDirectoryResult> ReadDirectory(
        string fileName, string? pattern, string? marker,
        nint buffer, uint length,
        FileOperationInfo info, CancellationToken ct)
    {
        var children = _fs.ListDirectory(fileName);
        if (children == null)
            return V(ReadDirectoryResult.Error(NtStatus.ObjectPathNotFound));

        uint bytesTransferred = 0;

        foreach (var child in children)
        {
            // marker: skip entries <= marker (sorted by name, case-insensitive)
            if (marker != null && string.Compare(child.Name, marker, StringComparison.OrdinalIgnoreCase) <= 0)
                continue;

            // pattern filter (null = match all)
            // WinFsp passes pattern when PassQueryDirectoryPattern is set; typically null for us
            // Skip pattern matching — let WinFsp handle it internally

            var dirInfo = new FspDirInfo();
            dirInfo.FileInfo = MakeFileInfo(child);
            dirInfo.SetFileName(child.Name);

            if (!WinFspFileSystem.AddDirInfo(&dirInfo, buffer, length, &bytesTransferred))
                return V(ReadDirectoryResult.Success(bytesTransferred)); // buffer full
        }

        WinFspFileSystem.EndDirInfo(buffer, length, &bytesTransferred);
        return V(ReadDirectoryResult.Success(bytesTransferred));
    }

    // ═══════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FileNode? Node(FileOperationInfo info)
        => info.Context as FileNode;

    /// <summary>
    /// Send an FspFileSystemNotify to invalidate the WinFsp kernel FileInfo cache for
    /// <paramref name="path"/>. Failures are intentionally swallowed: the originating IRP
    /// MUST succeed even if cache invalidation fails — the user-mode mutation already
    /// committed and stale cache is bounded by <c>FileInfoTimeoutMs</c>.
    /// Per <c>specs/cache-invalidation/spec.md</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Notify(uint filter, uint action, string path)
    {
        if (_host == null) return;
        int status = _host.Notify(filter, action, path);
        if (status < 0 && _logger.IsEnabled(LogLevel.Trace))
            _logger.LogTrace("FspFileSystemNotify({Path}, filter=0x{Filter:X}, action={Action}) returned 0x{Status:X8}",
                path, filter, action, status);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FspFileInfo MakeFileInfo(FileNode node) => new()
    {
        FileAttributes = (uint)node.Attributes,
        FileSize = (ulong)node.Size,
        AllocationSize = (ulong)node.AllocatedBytes, // actual pages, not logical size
        CreationTime = ToFileTime(node.CreationTime),
        LastAccessTime = ToFileTime(node.LastAccessTime),
        LastWriteTime = ToFileTime(node.LastWriteTime),
        ChangeTime = ToFileTime(node.LastWriteTime), // reuse LastWriteTime
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ToFileTime(DateTime dt)
        => (ulong)dt.ToFileTimeUtc();

    // Zero-alloc ValueTask wrappers — synchronous completion, no Task boxing
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask<CreateResult> V(CreateResult r) => new(r);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask<FsResult> V(FsResult r) => new(r);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask<ReadResult> V(ReadResult r) => new(r);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask<WriteResult> V(WriteResult r) => new(r);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask<ReadDirectoryResult> V(ReadDirectoryResult r) => new(r);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ValueTask<int> V(int r) => new(r);
}
