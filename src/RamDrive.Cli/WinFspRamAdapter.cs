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
        if (_options.EnableKernelCache)
            host.FileInfoTimeout = unchecked((uint)(-1));
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = true;
        host.FileSystemName = "NTFS";
        return NtStatus.Success;
    }

    public int Mounted(FileSystemHost host)
    {
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
        // allocationSize is a pre-reservation hint — do not set logical file size.
        // File grows via WriteFile.

        if (replaceFileAttributes && fileAttributes != 0)
            node.Attributes = (FileAttributes)fileAttributes;
        else if (fileAttributes != 0)
            node.Attributes |= (FileAttributes)fileAttributes;

        node.LastWriteTime = DateTime.UtcNow;
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
            _fs.Delete(fileName);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FspFileInfo MakeFileInfo(FileNode node) => new()
    {
        FileAttributes = (uint)node.Attributes,
        FileSize = (ulong)node.Size,
        AllocationSize = (ulong)node.Size, // RAM disk — allocation = logical
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
