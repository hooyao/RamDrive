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

    /// <summary>
    /// Optional file-scoped trace: when non-null, every callback that touches a
    /// path matching this filter writes a structured event line to TraceLog.
    /// Set via env var RAMDRIVE_TRACE_PATH (substring match, case-insensitive).
    /// </summary>
    public static List<string> TraceLog { get; } = [];
    private static string? _traceFilter;
    public static void SetTraceFilter(string? substring) { _traceFilter = substring; lock (TraceLog) TraceLog.Clear(); }
    internal static void Trace(string op, string? path, string? extra = null)
    {
        var f = _traceFilter;
        if (f == null) return;
        if (path != null && path.Contains(f, StringComparison.OrdinalIgnoreCase))
            lock (TraceLog) TraceLog.Add($"{DateTime.UtcNow:HH:mm:ss.ffffff}  {op,-22}  {path}{(extra == null ? "" : "  | " + extra)}");
    }

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
            // Pin to worst-case (permanent) cache lifetime so missing FspFileSystemNotify
            // calls produce stale-cache test failures in CI rather than only against
            // real Chromium with the production default. See specs/cache-invalidation.
            FileInfoTimeoutMs = uint.MaxValue,
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
    private FileSystemHost? _host;

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

    public int Mounted(FileSystemHost host) { _host = host; return NtStatus.Success; }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void Notify(uint filter, uint action, string path)
    {
        // Off-thread, fire-and-forget — see WinFspRamAdapter.Notify for rationale
        // (avoids dispatcher-pool deadlock under concurrent dir delete).
        var host = _host;
        if (host == null) return;
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            var (host, filter, action, path) = state;
            host.Notify(filter, action, path);
        }, (host, filter, action, path), preferLocal: false);
    }

    public int Init(FileSystemHost host)
    {
        host.SectorSize = 4096;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        if (_options.EnableKernelCache)
            host.FileInfoTimeout = _options.FileInfoTimeoutMs;
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
            RamDriveFixture.Trace("CreateFile-Dir", fileName, "ok");
            Notify(FileNotify.ChangeDirName, FileNotify.ActionAdded, fileName);
            return new(new CreateResult(NtStatus.Success, MkInfo(dir)));
        }
        var file = _fs.CreateFile(fileName, sd);
        if (file == null)
        {
            if (_fs.FindNode(fileName) != null) { RamDriveFixture.Trace("CreateFile-Collision", fileName, ""); return new(CreateResult.Error(NtStatus.ObjectNameCollision)); }
            RamDriveFixture.Trace("CreateFile-PathNF", fileName, ""); return new(CreateResult.Error(NtStatus.ObjectPathNotFound));
        }
        if (fa != 0) file.Attributes = (FileAttributes)fa;
        info.Context = file; info.IsDirectory = false;
        RamDriveFixture.Trace("CreateFile", fileName, $"alloc={alloc}");
        Notify(FileNotify.ChangeFileName, FileNotify.ActionAdded, fileName);
        return new(new CreateResult(NtStatus.Success, MkInfo(file)));
    }

    public ValueTask<CreateResult> OpenFile(string fileName, uint co, uint ga,
        FileOperationInfo info, CancellationToken ct)
    {
        var n = _fs.FindNode(fileName);
        if (n == null) { RamDriveFixture.Trace("OpenFile-NF", fileName, ""); return new(CreateResult.Error(NtStatus.ObjectNameNotFound)); }
        info.Context = n; info.IsDirectory = n.IsDirectory;
        RamDriveFixture.Trace("OpenFile", fileName, $"len={n.Content?.Length ?? -1}");
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
        if (info.FileName is { } path)
            Notify(FileNotify.ChangeSize | FileNotify.ChangeLastWrite, FileNotify.ActionModified, path);
        return new(FsResult.Success(MkInfo(n)));
    }

    public ValueTask<ReadResult> ReadFile(string fileName, Memory<byte> buffer, ulong offset,
        FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info); if (n?.Content == null) return new(ReadResult.Error(NtStatus.ObjectNameNotFound));
        long len = n.Content.Length;
        if ((long)offset >= len) { RamDriveFixture.Trace("ReadFile-EOF", fileName, $"off={offset} len={len}"); return new(ReadResult.EndOfFile()); }
        int toRead = (int)Math.Min(buffer.Length, len - (long)offset);
        int read = n.Content.Read((long)offset, buffer.Span[..toRead]);
        n.LastAccessTime = DateTime.UtcNow;
        RamDriveFixture.Trace("ReadFile", fileName, $"off={offset} bufLen={buffer.Length} fileLen={len} read={read}");
        return new(ReadResult.Success((uint)read));
    }

    public ValueTask<WriteResult> WriteFile(string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool wteof, bool cio, FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info); if (n?.Content == null) return new(WriteResult.Error(NtStatus.ObjectNameNotFound));
        long wo = wteof ? n.Content.Length : (long)offset;
        long beforeLen = n.Content.Length;
        int wl = buffer.Length;
        if (cio) { long fl = n.Content.Length; if (wo >= fl) { RamDriveFixture.Trace("WriteFile-NOOP", fileName, $"cio=1 wo={wo} fl={fl} buf={buffer.Length}"); return new(WriteResult.Success(0, MkInfo(n))); } wl = (int)Math.Min(wl, fl - wo); }
        int written = n.Content.Write(wo, buffer.Span[..wl]);
        if (written < 0) return new(WriteResult.Error(NtStatus.DiskFull));
        n.LastWriteTime = DateTime.UtcNow;
        RamDriveFixture.Trace("WriteFile", fileName, $"cio={(cio?1:0)} wteof={(wteof?1:0)} off={offset} buf={buffer.Length} wl={wl} written={written} lenBefore={beforeLen} lenAfter={n.Content.Length}");
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
        Notify(FileNotify.ChangeAttributes | FileNotify.ChangeLastWrite, FileNotify.ActionModified, fileName);
        return new(FsResult.Success(MkInfo(n)));
    }

    public ValueTask<FsResult> SetFileSize(string fileName, ulong sz, bool alloc,
        FileOperationInfo info, CancellationToken ct)
    {
        var n = N(info); if (n?.Content == null) return new(FsResult.Error(NtStatus.ObjectNameNotFound));
        long beforeLen = n.Content.Length;
        if (alloc)
        {
            long additional = (long)sz - n.Size;
            if (additional > 0 && additional > _fs.FreeBytes)
                return new(FsResult.Error(NtStatus.DiskFull));
            RamDriveFixture.Trace("SetAllocSize", fileName, $"sz={sz}");
            return new(FsResult.Success(MkInfo(n)));
        }
        if (!n.Content.SetLength((long)sz)) return new(FsResult.Error(NtStatus.DiskFull));
        n.LastWriteTime = DateTime.UtcNow;
        RamDriveFixture.Trace("SetFileSize", fileName, $"sz={sz} lenBefore={beforeLen} lenAfter={n.Content.Length}");
        Notify(FileNotify.ChangeSize | FileNotify.ChangeLastWrite, FileNotify.ActionModified, fileName);
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
    {
        var nBefore = N(info);
        long lenBefore = nBefore?.Content?.Length ?? -1;
        bool ok = _fs.Move(fileName, newFileName, replace);
        var nAfter = _fs.FindNode(newFileName);
        long lenAfter = nAfter?.Content?.Length ?? -1;
        RamDriveFixture.Trace("MoveFile", fileName, $"-> {newFileName} replace={replace} ok={ok} sourceLenBefore={lenBefore} targetLenAfter={lenAfter} sameNode={ReferenceEquals(nBefore,nAfter)}");
        if (ok)
        {
            Notify(FileNotify.ChangeFileName, FileNotify.ActionRenamedOldName, fileName);
            Notify(FileNotify.ChangeFileName, FileNotify.ActionRenamedNewName, newFileName);
        }
        return new(ok ? NtStatus.Success : NtStatus.ObjectNameCollision);
    }

    public void Cleanup(string? fileName, FileOperationInfo info, CleanupFlags flags)
    {
        if (flags.HasFlag(CleanupFlags.Delete) && fileName != null)
        {
            bool wasDir = info.IsDirectory;
            _fs.Delete(fileName);
            Notify(wasDir ? FileNotify.ChangeDirName : FileNotify.ChangeFileName, FileNotify.ActionRemoved, fileName);
        }
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
