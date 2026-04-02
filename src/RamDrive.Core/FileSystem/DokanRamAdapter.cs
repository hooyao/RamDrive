using System.Runtime.Versioning;
using System.Security.AccessControl;
using DokanNet;
using LTRData.Extensions.Native.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;

namespace RamDrive.Core.FileSystem;

/// <summary>
/// Read-write Dokan adapter backed by RamFileSystem.
/// Translates IDokanOperations2 callbacks to RamFileSystem operations.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DokanRamAdapter : IDokanOperations2
{
    private readonly RamFileSystem _fs;
    private readonly RamDriveOptions _options;
    private readonly ILogger<DokanRamAdapter> _logger;

    public DokanRamAdapter(RamFileSystem fs, IOptions<RamDriveOptions> options, ILogger<DokanRamAdapter> logger)
    {
        _fs = fs;
        _options = options.Value;
        _logger = logger;
    }

    public int DirectoryListingTimeoutResetIntervalMs => 0;

    // ==================== CreateFile ====================

    public NtStatus CreateFile(
        ReadOnlyNativeMemory<char> fileName, DokanNet.FileAccess access, FileShare share,
        FileMode mode, FileOptions options, FileAttributes attributes, ref DokanFileInfo info)
    {
        string path = fileName.Span.ToString();
        _logger.LogDebug("CreateFile: {Path} mode={Mode} access={Access} isDir={IsDir}", path, mode, access, info.IsDirectory);

        var node = _fs.FindNode(path);

        // Directory operations
        if (info.IsDirectory)
        {
            switch (mode)
            {
                case FileMode.CreateNew:
                    if (node != null) return DokanResult.FileExists;
                    var dir = _fs.CreateDirectory(path);
                    if (dir == null) return DokanResult.PathNotFound;
                    info.Context = dir;
                    return DokanResult.Success;

                case FileMode.Open:
                    if (node == null) return DokanResult.PathNotFound;
                    if (!node.IsDirectory) return DokanResult.NotADirectory;
                    info.Context = node;
                    return DokanResult.Success;

                default:
                    return DokanResult.Success;
            }
        }

        // File operations
        bool pathExists = node != null;
        bool isDirectory = node?.IsDirectory ?? false;

        if (isDirectory)
        {
            info.IsDirectory = true;
            info.Context = node;
            return DokanResult.Success;
        }

        switch (mode)
        {
            case FileMode.Open:
                if (!pathExists) return DokanResult.FileNotFound;
                info.Context = node;
                return DokanResult.Success;

            case FileMode.CreateNew:
                if (pathExists) return DokanResult.FileExists;
                var newFile = _fs.CreateFile(path);
                if (newFile == null) return DokanResult.PathNotFound;
                info.Context = newFile;
                return DokanResult.Success;

            case FileMode.Create:
                if (pathExists)
                {
                    // Truncate existing
                    node!.Content?.SetLength(0);
                    node.LastWriteTime = DateTime.UtcNow;
                    info.Context = node;
                    return DokanResult.AlreadyExists;
                }
                var created = _fs.CreateFile(path);
                if (created == null) return DokanResult.PathNotFound;
                info.Context = created;
                return DokanResult.Success;

            case FileMode.OpenOrCreate:
                if (pathExists)
                {
                    info.Context = node;
                    return DokanResult.AlreadyExists;
                }
                var opened = _fs.CreateFile(path);
                if (opened == null) return DokanResult.PathNotFound;
                info.Context = opened;
                return DokanResult.Success;

            case FileMode.Truncate:
                if (!pathExists) return DokanResult.FileNotFound;
                node!.Content?.SetLength(0);
                node.LastWriteTime = DateTime.UtcNow;
                info.Context = node;
                return DokanResult.Success;

            case FileMode.Append:
                if (pathExists)
                {
                    info.Context = node;
                    return DokanResult.Success;
                }
                var appended = _fs.CreateFile(path);
                if (appended == null) return DokanResult.PathNotFound;
                info.Context = appended;
                return DokanResult.Success;

            default:
                return DokanResult.InternalError;
        }
    }

    // ==================== ReadFile ====================

    public NtStatus ReadFile(
        ReadOnlyNativeMemory<char> fileName, NativeMemory<byte> buffer,
        out int bytesRead, long offset, ref DokanFileInfo info)
    {
        bytesRead = 0;

        var node = GetNodeFromContext(fileName, ref info);
        if (node?.Content == null) return DokanResult.FileNotFound;

        // PagingIo must not read past file length
        int requestedLength = buffer.Span.Length;
        if (info.PagingIo)
        {
            long fileLength = node.Content.Length;
            if (offset >= fileLength) return DokanResult.Success;
            requestedLength = (int)Math.Min(requestedLength, fileLength - offset);
        }

        bytesRead = node.Content.Read(offset, buffer.Span[..requestedLength]);
        node.LastAccessTime = DateTime.UtcNow;
        return DokanResult.Success;
    }

    // ==================== WriteFile ====================

    public NtStatus WriteFile(
        ReadOnlyNativeMemory<char> fileName, ReadOnlyNativeMemory<byte> buffer,
        out int bytesWritten, long offset, ref DokanFileInfo info)
    {
        bytesWritten = 0;

        var node = GetNodeFromContext(fileName, ref info);
        if (node?.Content == null) return DokanResult.FileNotFound;

        // offset == -1 means append
        if (offset == -1)
            offset = node.Content.Length;

        // PagingIo: clamp write to allocation size
        int writeLength = buffer.Span.Length;
        if (info.PagingIo)
        {
            long fileLength = node.Content.Length;
            if (offset >= fileLength) { bytesWritten = 0; return DokanResult.Success; }
            writeLength = (int)Math.Min(writeLength, fileLength - offset);
        }

        int written = node.Content.Write(offset, buffer.Span[..writeLength]);
        if (written < 0) return DokanResult.DiskFull;

        bytesWritten = written;
        node.LastWriteTime = DateTime.UtcNow;
        return DokanResult.Success;
    }

    // ==================== SetEndOfFile / SetAllocationSize ====================

    public NtStatus SetEndOfFile(ReadOnlyNativeMemory<char> fileName, long length, ref DokanFileInfo info)
    {
        var node = GetNodeFromContext(fileName, ref info);
        if (node?.Content == null) return DokanResult.FileNotFound;

        if (!node.Content.SetLength(length)) return DokanResult.DiskFull;
        node.LastWriteTime = DateTime.UtcNow;
        return DokanResult.Success;
    }

    public NtStatus SetAllocationSize(ReadOnlyNativeMemory<char> fileName, long length, ref DokanFileInfo info)
        => SetEndOfFile(fileName, length, ref info);

    // ==================== FindFiles ====================

    public NtStatus FindFiles(
        ReadOnlyNativeMemory<char> fileName, out IEnumerable<FindFileInformation> files,
        ref DokanFileInfo info)
    {
        string path = fileName.Span.ToString();
        var children = _fs.ListDirectory(path);
        if (children == null)
        {
            files = [];
            return DokanResult.PathNotFound;
        }

        files = children.Select(ToFindFileInfo);
        return DokanResult.Success;
    }

    public NtStatus FindFilesWithPattern(
        ReadOnlyNativeMemory<char> fileName, ReadOnlyNativeMemory<char> searchPattern,
        out IEnumerable<FindFileInformation> files, ref DokanFileInfo info)
    {
        string path = fileName.Span.ToString();
        string pattern = searchPattern.Span.ToString();

        var children = _fs.ListDirectory(path);
        if (children == null)
        {
            files = [];
            return DokanResult.PathNotFound;
        }

        files = children
            .Where(n => DokanHelper.DokanIsNameInExpression(pattern, n.Name, true))
            .Select(ToFindFileInfo);
        return DokanResult.Success;
    }

    // ==================== GetFileInformation ====================

    public NtStatus GetFileInformation(
        ReadOnlyNativeMemory<char> fileName, out ByHandleFileInformation fileInfo,
        ref DokanFileInfo info)
    {
        fileInfo = default;
        var node = GetNodeFromContext(fileName, ref info);
        if (node == null) return DokanResult.FileNotFound;

        fileInfo = new ByHandleFileInformation
        {
            Attributes = node.Attributes,
            CreationTime = node.CreationTime,
            LastAccessTime = node.LastAccessTime,
            LastWriteTime = node.LastWriteTime,
            Length = node.Size
        };
        return DokanResult.Success;
    }

    // ==================== Volume Info ====================

    public NtStatus GetVolumeInformation(
        NativeMemory<char> volumeLabel, out FileSystemFeatures features,
        NativeMemory<char> fileSystemName, out uint maximumComponentLength,
        ref uint volumeSerialNumber, ref DokanFileInfo info)
    {
        volumeLabel.SetString(_options.VolumeLabel);
        fileSystemName.SetString("NTFS");
        maximumComponentLength = 255;
        features = FileSystemFeatures.CasePreservedNames
                 | FileSystemFeatures.UnicodeOnDisk;
        return DokanResult.Success;
    }

    public NtStatus GetDiskFreeSpace(
        out long freeBytesAvailable, out long totalNumberOfBytes,
        out long totalNumberOfFreeBytes, ref DokanFileInfo info)
    {
        totalNumberOfBytes = _fs.TotalBytes;
        freeBytesAvailable = _fs.FreeBytes;
        totalNumberOfFreeBytes = _fs.FreeBytes;
        return DokanResult.Success;
    }

    // ==================== Delete ====================

    public NtStatus DeleteFile(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        var node = GetNodeFromContext(fileName, ref info);
        if (node == null) return DokanResult.FileNotFound;
        if (node.IsDirectory) return DokanResult.AccessDenied;
        return DokanResult.Success; // actual deletion in Cleanup
    }

    public NtStatus DeleteDirectory(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        string path = fileName.Span.ToString();
        var node = _fs.FindNode(path);
        if (node == null) return DokanResult.PathNotFound;
        if (!node.IsDirectory) return DokanResult.AccessDenied;
        if (node.Children!.Count > 0) return DokanResult.DirectoryNotEmpty;
        return DokanResult.Success; // actual deletion in Cleanup
    }

    // ==================== MoveFile ====================

    public NtStatus MoveFile(
        ReadOnlyNativeMemory<char> oldName, ReadOnlyNativeMemory<char> newName,
        bool replace, ref DokanFileInfo info)
    {
        string oldPath = oldName.Span.ToString();
        string newPath = newName.Span.ToString();

        if (_fs.Move(oldPath, newPath, replace))
            return DokanResult.Success;
        return DokanResult.FileExists;
    }

    // ==================== SetFileAttributes / SetFileTime ====================

    public NtStatus SetFileAttributes(
        ReadOnlyNativeMemory<char> fileName, FileAttributes attributes, ref DokanFileInfo info)
    {
        var node = GetNodeFromContext(fileName, ref info);
        if (node == null) return DokanResult.FileNotFound;
        node.Attributes = attributes;
        return DokanResult.Success;
    }

    public NtStatus SetFileTime(
        ReadOnlyNativeMemory<char> fileName,
        DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime,
        ref DokanFileInfo info)
    {
        var node = GetNodeFromContext(fileName, ref info);
        if (node == null) return DokanResult.FileNotFound;

        if (creationTime.HasValue) node.CreationTime = creationTime.Value;
        if (lastAccessTime.HasValue) node.LastAccessTime = lastAccessTime.Value;
        if (lastWriteTime.HasValue) node.LastWriteTime = lastWriteTime.Value;
        return DokanResult.Success;
    }

    // ==================== Lifecycle ====================

    public void Cleanup(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        if (info.DeletePending)
        {
            string path = fileName.Span.ToString();
            _fs.Delete(path);
        }

        info.Context = null;
    }

    public void CloseFile(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        info.Context = null;
    }

    public NtStatus Mounted(ReadOnlyNativeMemory<char> mountPoint, ref DokanFileInfo info)
    {
        _logger.LogInformation("Drive mounted at {MountPoint}", mountPoint.Span.ToString());
        return DokanResult.Success;
    }

    public NtStatus Unmounted(ref DokanFileInfo info)
    {
        _logger.LogInformation("Drive unmounted");
        return DokanResult.Success;
    }

    // ==================== No-op / trivial ====================

    public NtStatus FlushFileBuffers(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
        => DokanResult.Success;

    public NtStatus LockFile(ReadOnlyNativeMemory<char> fileName, long offset, long length, ref DokanFileInfo info)
        => DokanResult.Success;

    public NtStatus UnlockFile(ReadOnlyNativeMemory<char> fileName, long offset, long length, ref DokanFileInfo info)
        => DokanResult.Success;

    public NtStatus GetFileSecurity(
        ReadOnlyNativeMemory<char> fileName, out FileSystemSecurity? security,
        AccessControlSections sections, ref DokanFileInfo info)
    {
        security = null;
        return DokanResult.NotImplemented;
    }

    public NtStatus SetFileSecurity(
        ReadOnlyNativeMemory<char> fileName, FileSystemSecurity security,
        AccessControlSections sections, ref DokanFileInfo info)
        => DokanResult.NotImplemented;

    public NtStatus FindStreams(
        ReadOnlyNativeMemory<char> fileName, out IEnumerable<FindFileInformation> streams,
        ref DokanFileInfo info)
    {
        streams = [];
        return DokanResult.NotImplemented;
    }

    // ==================== Helpers ====================

    private FileNode? GetNodeFromContext(ReadOnlyNativeMemory<char> fileName, ref DokanFileInfo info)
    {
        if (info.Context is FileNode node) return node;

        string path = fileName.Span.ToString();
        var found = _fs.FindNode(path);
        if (found != null) info.Context = found;
        return found;
    }

    private static FindFileInformation ToFindFileInfo(FileNode node) => new()
    {
        FileName = node.Name.AsMemory(),
        Attributes = node.Attributes,
        CreationTime = node.CreationTime,
        LastAccessTime = node.LastAccessTime,
        LastWriteTime = node.LastWriteTime,
        Length = node.Size
    };
}
