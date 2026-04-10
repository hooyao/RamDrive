using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using WinFsp.Native;

namespace RamDrive.IntegrationTests;

/// <summary>
/// Shared fixture: boots a WinFsp mount via UNC path, shared across all tests in the collection.
/// Unmounts on dispose. Safe on crash — no drive letter zombie.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RamDriveFixture : IDisposable
{
    public string Root { get; }
    public long CapacityMb { get; } = 512;

    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;
    private readonly FileSystemHost _host;

    public RamDriveFixture()
    {
        var options = new RamDriveOptions
        {
            CapacityMb = CapacityMb,
            PageSizeKb = 64,
            EnableKernelCache = true,
            VolumeLabel = "IntegrationTest",
        };

        _pool = new PagePool(new OptionsWrapper<RamDriveOptions>(options), NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);
        var adapter = new TestAdapter(_fs, options);
        _host = new FileSystemHost(adapter);
        _host.Prefix = $@"\winfsp-tests\itest-{Environment.ProcessId}";

        int result = _host.Mount(null);
        if (result < 0)
            throw new InvalidOperationException($"WinFsp mount failed: 0x{result:X8}. Is WinFsp installed?");

        Root = _host.MountPoint!;
        if (!Root.EndsWith('\\')) Root += @"\";
    }

    public void CleanRoot()
    {
        foreach (var d in Directory.GetDirectories(Root))
            try { Directory.Delete(d, true); } catch { }
        foreach (var f in Directory.GetFiles(Root))
            try { File.Delete(f); } catch { }
    }

    public void Dispose()
    {
        _host.Dispose();
        _fs.Dispose();
        _pool.Dispose();
    }
}

[CollectionDefinition("RamDrive")]
public class RamDriveCollection : ICollectionFixture<RamDriveFixture>;

/// <summary>
/// Minimal IFileSystem for integration testing. Self-contained, no RamDrive.Cli dependency.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class TestAdapter : IFileSystem
{
    private readonly RamFileSystem _fs;
    private readonly RamDriveOptions _options;

    private const string RootSddl = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)";

    public TestAdapter(RamFileSystem fs, RamDriveOptions options)
    {
        _fs = fs; _options = options;
        var sd = new RawSecurityDescriptor(RootSddl);
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        _fs.SetRootSecurityDescriptor(bytes);
    }

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

    public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
    {
        totalSize = (ulong)_fs.TotalBytes; freeSize = (ulong)_fs.FreeBytes;
        volumeLabel = _options.VolumeLabel; return NtStatus.Success;
    }

    public int GetFileSecurityByName(string fileName, out uint fileAttributes, ref byte[]? securityDescriptor)
    {
        var node = _fs.FindNode(fileName);
        if (node == null) { fileAttributes = 0; return NtStatus.ObjectNameNotFound; }
        fileAttributes = (uint)node.Attributes; securityDescriptor = node.SecurityDescriptor;
        return NtStatus.Success;
    }

    public ValueTask<CreateResult> CreateFile(string fileName, uint co, uint ga, uint fa,
        byte[]? sd, ulong alloc, FileOperationInfo info, CancellationToken ct)
    {
        bool isDir = (co & (uint)CreateOptions.FileDirectoryFile) != 0;
        if (isDir)
        {
            var dir = _fs.CreateDirectory(fileName, sd);
            if (dir == null) return new(CreateResult.Error(NtStatus.ObjectNameCollision));
            info.Context = dir; info.IsDirectory = true;
            return new(new CreateResult(NtStatus.Success, MkInfo(dir)));
        }
        var file = _fs.CreateFile(fileName, sd);
        if (file == null)
        {
            if (_fs.FindNode(fileName) != null) return new(CreateResult.Error(NtStatus.ObjectNameCollision));
            return new(CreateResult.Error(NtStatus.ObjectPathNotFound));
        }
        if (fa != 0) file.Attributes = (FileAttributes)fa;
        info.Context = file; info.IsDirectory = false;
        return new(new CreateResult(NtStatus.Success, MkInfo(file)));
    }

    public ValueTask<CreateResult> OpenFile(string fileName, uint co, uint ga,
        FileOperationInfo info, CancellationToken ct)
    {
        var n = _fs.FindNode(fileName);
        if (n == null) return new(CreateResult.Error(NtStatus.ObjectNameNotFound));
        info.Context = n; info.IsDirectory = n.IsDirectory;
        return new(new CreateResult(NtStatus.Success, MkInfo(n)));
    }

    public ValueTask<FsResult> OverwriteFile(uint fa, bool replace, ulong alloc,
        FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info); if (n?.Content == null) return new(FsResult.Error(NtStatus.ObjectNameNotFound));
        n.Content.SetLength(0);
        if (replace && fa != 0) n.Attributes = (FileAttributes)fa;
        else if (fa != 0) n.Attributes |= (FileAttributes)fa;
        n.LastWriteTime = DateTime.UtcNow;
        return new(FsResult.Success(MkInfo(n)));
    }

    public ValueTask<ReadResult> ReadFile(string fileName, Memory<byte> buffer, ulong offset,
        FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info); if (n?.Content == null) return new(ReadResult.Error(NtStatus.ObjectNameNotFound));
        long len = n.Content.Length;
        if ((long)offset >= len) return new(ReadResult.EndOfFile());
        int toRead = (int)Math.Min(buffer.Length, len - (long)offset);
        int read = n.Content.Read((long)offset, buffer.Span[..toRead]);
        n.LastAccessTime = DateTime.UtcNow;
        return new(ReadResult.Success((uint)read));
    }

    public ValueTask<WriteResult> WriteFile(string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool wteof, bool cio, FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info); if (n?.Content == null) return new(WriteResult.Error(NtStatus.ObjectNameNotFound));
        long wo = wteof ? n.Content.Length : (long)offset;
        int wl = buffer.Length;
        if (cio) { long fl = n.Content.Length; if (wo >= fl) return new(WriteResult.Success(0, MkInfo(n))); wl = (int)Math.Min(wl, fl - wo); }
        int written = n.Content.Write(wo, buffer.Span[..wl]);
        if (written < 0) return new(WriteResult.Error(NtStatus.DiskFull));
        n.LastWriteTime = DateTime.UtcNow;
        return new(WriteResult.Success((uint)written, MkInfo(n)));
    }

    public ValueTask<FsResult> GetFileInformation(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info); if (n == null) return new(FsResult.Error(NtStatus.ObjectNameNotFound));
        return new(FsResult.Success(MkInfo(n)));
    }

    public ValueTask<FsResult> SetFileAttributes(string fileName, uint fa, ulong ct2, ulong lat,
        ulong lwt, ulong cht, FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info); if (n == null) return new(FsResult.Error(NtStatus.ObjectNameNotFound));
        if (fa != unchecked((uint)(-1)) && fa != 0) n.Attributes = (FileAttributes)fa;
        if (ct2 != 0) n.CreationTime = DateTime.FromFileTimeUtc((long)ct2);
        if (lat != 0) n.LastAccessTime = DateTime.FromFileTimeUtc((long)lat);
        if (lwt != 0) n.LastWriteTime = DateTime.FromFileTimeUtc((long)lwt);
        return new(FsResult.Success(MkInfo(n)));
    }

    public ValueTask<FsResult> SetFileSize(string fileName, ulong sz, bool alloc,
        FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info); if (n?.Content == null) return new(FsResult.Error(NtStatus.ObjectNameNotFound));
        if (alloc)
        {
            long additional = (long)sz - n.Size;
            if (additional > 0 && additional > _fs.FreeBytes)
                return new(FsResult.Error(NtStatus.DiskFull));
            return new(FsResult.Success(MkInfo(n)));
        }
        if (!n.Content.SetLength((long)sz)) return new(FsResult.Error(NtStatus.DiskFull));
        n.LastWriteTime = DateTime.UtcNow;
        return new(FsResult.Success(MkInfo(n)));
    }

    public int GetFileSecurity(string fileName, ref byte[]? securityDescriptor, FileOperationInfo info)
    {
        var n = N(info); if (n == null) return NtStatus.ObjectNameNotFound;
        securityDescriptor = n.SecurityDescriptor; return NtStatus.Success;
    }

    public int SetFileSecurity(string fileName, uint securityInformation, byte[] modificationDescriptor, FileOperationInfo info)
    {
        var n = N(info); if (n == null) return NtStatus.ObjectNameNotFound;
        var existing = n.SecurityDescriptor != null ? new RawSecurityDescriptor(n.SecurityDescriptor, 0) : new RawSecurityDescriptor(RootSddl);
        var modification = new RawSecurityDescriptor(modificationDescriptor, 0);
        if ((securityInformation & 1) != 0) existing.Owner = modification.Owner;
        if ((securityInformation & 2) != 0) existing.Group = modification.Group;
        if ((securityInformation & 4) != 0) existing.DiscretionaryAcl = modification.DiscretionaryAcl;
        if ((securityInformation & 8) != 0) existing.SystemAcl = modification.SystemAcl;
        var result = new byte[existing.BinaryLength]; existing.GetBinaryForm(result, 0);
        n.SecurityDescriptor = result; return NtStatus.Success;
    }

    public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info); if (n == null) return new(NtStatus.ObjectNameNotFound);
        if (n.IsDirectory && n.Children!.Count > 0) return new(NtStatus.DirectoryNotEmpty);
        return new(NtStatus.Success);
    }

    public ValueTask<int> MoveFile(string fileName, string newFileName, bool replace,
        FileOperationInfo info, CancellationToken ct)
        => new(_fs.Move(fileName, newFileName, replace) ? NtStatus.Success : NtStatus.ObjectNameCollision);

    public void Cleanup(string? fileName, FileOperationInfo info, CleanupFlags flags)
    {
        if (flags.HasFlag(CleanupFlags.Delete) && fileName != null) _fs.Delete(fileName);
        var n = N(info); if (n == null) return;
        if (flags.HasFlag(CleanupFlags.SetLastWriteTime)) n.LastWriteTime = DateTime.UtcNow;
        if (flags.HasFlag(CleanupFlags.SetLastAccessTime)) n.LastAccessTime = DateTime.UtcNow;
        if (flags.HasFlag(CleanupFlags.SetChangeTime)) n.LastWriteTime = DateTime.UtcNow;
    }

    public void Close(FileOperationInfo info) => info.Context = null;

    public ValueTask<ReadDirectoryResult> ReadDirectory(string fileName, string? pattern, string? marker,
        nint buffer, uint length, FileOperationInfo info, CancellationToken ct)
    {
        var children = _fs.ListDirectory(fileName);
        if (children == null) return new(ReadDirectoryResult.Error(NtStatus.ObjectPathNotFound));
        uint bt = 0;
        foreach (var child in children)
        {
            if (marker != null && string.Compare(child.Name, marker, StringComparison.OrdinalIgnoreCase) <= 0) continue;
            var di = new FspDirInfo(); di.FileInfo = MkInfo(child); di.SetFileName(child.Name);
            if (!WinFspFileSystem.AddDirInfo(&di, buffer, length, &bt))
                return new(ReadDirectoryResult.Success(bt));
        }
        WinFspFileSystem.EndDirInfo(buffer, length, &bt);
        return new(ReadDirectoryResult.Success(bt));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static FileNode? N(FileOperationInfo i) => i.Context as FileNode;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static FspFileInfo MkInfo(FileNode n) => new()
    {
        FileAttributes = (uint)n.Attributes, FileSize = (ulong)n.Size, AllocationSize = (ulong)n.Size,
        CreationTime = (ulong)n.CreationTime.ToFileTimeUtc(), LastAccessTime = (ulong)n.LastAccessTime.ToFileTimeUtc(),
        LastWriteTime = (ulong)n.LastWriteTime.ToFileTimeUtc(), ChangeTime = (ulong)n.LastWriteTime.ToFileTimeUtc(),
    };
}
