using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinFsp.Native;

namespace WinFsp;

/// <summary>
/// High-level file system host. Accepts an <see cref="IFileSystem"/> implementation and
/// handles all WinFSP plumbing: mounting, callback dispatch, async STATUS_PENDING management,
/// buffer pooling, context tracking, and cancellation.
///
/// Built on top of <see cref="WinFspFileSystem"/> (low-level API).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class FileSystemHost : IDisposable
{
    private readonly IFileSystem _fs;
    private WinFspFileSystem? _rawFs;
    private GCHandle _selfHandle;
    private readonly UnmanagedBufferPool _bufferPool = new();

    public FileSystemHost(IFileSystem fileSystem)
    {
        _fs = fileSystem;
    }

    ~FileSystemHost() => Dispose(false);

    // ═══════════════════════════════════════════
    //  Configuration (set before Mount)
    // ═══════════════════════════════════════════

    public ushort SectorSize { get; set; } = 4096;
    public ushort SectorsPerAllocationUnit { get; set; } = 1;
    public ushort MaxComponentLength { get; set; } = 255;
    public ulong VolumeCreationTime { get; set; }
    public uint VolumeSerialNumber { get; set; }
    public uint FileInfoTimeout { get; set; }
    public bool CaseSensitiveSearch { get; set; }
    public bool CasePreservedNames { get; set; } = true;
    public bool UnicodeOnDisk { get; set; } = true;
    public bool PersistentAcls { get; set; } = true;
    public bool ReparsePoints { get; set; }
    public bool ReparsePointsAccessCheck { get; set; }
    public bool NamedStreams { get; set; }
    public bool ExtendedAttributes { get; set; }
    public bool ReadOnlyVolume { get; set; }
    public bool PostCleanupWhenModifiedOnly { get; set; } = true;
    public bool PassQueryDirectoryPattern { get; set; }
    public bool PassQueryDirectoryFileName { get; set; }
    public bool FlushAndPurgeOnCleanup { get; set; }
    public bool DeviceControl { get; set; }
    public bool AllowOpenInKernelMode { get; set; }
    public bool WslFeatures { get; set; }
    public bool RejectIrpPriorToTransact0 { get; set; }
    public bool SupportsPosixUnlinkRename { get; set; }
    public bool PostDispositionWhenNecessaryOnly { get; set; }
    public string? Prefix { get; set; }
    public string FileSystemName { get; set; } = "NTFS";

    // ═══════════════════════════════════════════
    //  Mount / Unmount
    // ═══════════════════════════════════════════

    public int Mount(string? mountPoint, bool synchronized = false, uint debugLog = 0)
        => MountEx(mountPoint, 0, synchronized, debugLog);

    public int MountEx(string? mountPoint, uint threadCount,
        bool synchronized = false, uint debugLog = 0)
    {
        // 1. Init callback
        int result;
        try { result = _fs.Init(this); }
        catch (Exception ex) { return HandleException(ex); }
        if (result < 0) return result;

        // 2. Create low-level FS and configure VolumeParams
        _rawFs = new WinFspFileSystem();
        ConfigureVolumeParams(ref _rawFs.VolumeParams);
        PopulateInterface(ref _rawFs.Interface);

        // Store ourselves in UserContext so callbacks can find us
        _selfHandle = GCHandle.Alloc(this);
        _rawFs.UserContext = GCHandle.ToIntPtr(_selfHandle);

        // 3. Mount
        result = _rawFs.Mount(mountPoint, threadCount, synchronized, debugLog);
        if (result < 0) { Cleanup(); return result; }

        // 4. Mounted callback
        try { result = _fs.Mounted(this); }
        catch (Exception ex) { result = HandleException(ex); }
        if (result < 0) { Cleanup(); return result; }

        return NtStatus.Success;
    }

    public void Unmount() => Dispose();

    public string? MountPoint => _rawFs?.MountPoint;
    public IFileSystem FileSystem => _fs;

    // ═══════════════════════════════════════════
    //  IDisposable
    // ═══════════════════════════════════════════

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_rawFs != null)
        {
            _rawFs.Unmount();
            if (disposing)
            {
                try { _fs.Unmounted(this); } catch { }
            }
            Cleanup();
        }
    }

    private void Cleanup()
    {
        _rawFs?.Dispose();
        _rawFs = null;
        if (_selfHandle.IsAllocated) _selfHandle.Free();
        _bufferPool.Dispose();
    }

    // ═══════════════════════════════════════════
    //  Per-handle state
    // ═══════════════════════════════════════════

    private sealed class HandleState
    {
        public readonly FileOperationInfo Info = new();
        public string? FileName;
    }

    // ═══════════════════════════════════════════
    //  Callback helpers
    // ═══════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FileSystemHost Self(nint fileSystem)
        => (FileSystemHost)GCHandle.FromIntPtr(FspApi.GetUserContext(fileSystem)).Target!;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HandleState H(FspFullContext* ctx)
    {
        if (ctx->UserContext2 != 0)
            return (HandleState)GCHandle.FromIntPtr((nint)ctx->UserContext2).Target!;
        return null!;
    }

    private static void SetHandle(FspFullContext* ctx, HandleState hs)
    {
        var handle = GCHandle.Alloc(hs);
        ctx->UserContext2 = (ulong)(nint)GCHandle.ToIntPtr(handle);
    }

    private static void FreeHandle(FspFullContext* ctx)
    {
        if (ctx->UserContext2 != 0)
        {
            GCHandle.FromIntPtr((nint)ctx->UserContext2).Free();
            ctx->UserContext2 = 0;
        }
    }

    private int HandleException(Exception ex)
    {
        try { return _fs.ExceptionHandler(ex); }
        catch { return NtStatus.UnexpectedIoError; }
    }

    // ═══════════════════════════════════════════
    //  VolumeParams configuration
    // ═══════════════════════════════════════════

    private void ConfigureVolumeParams(ref FspVolumeParams vp)
    {
        vp.SectorSize = SectorSize;
        vp.SectorsPerAllocationUnit = SectorsPerAllocationUnit;
        vp.MaxComponentLength = MaxComponentLength;
        vp.VolumeCreationTime = VolumeCreationTime;
        vp.VolumeSerialNumber = VolumeSerialNumber;
        vp.FileInfoTimeout = FileInfoTimeout;
        vp.CaseSensitiveSearch = CaseSensitiveSearch;
        vp.CasePreservedNames = CasePreservedNames;
        vp.UnicodeOnDisk = UnicodeOnDisk;
        vp.PersistentAcls = PersistentAcls;
        vp.ReparsePoints = ReparsePoints;
        vp.ReparsePointsAccessCheck = ReparsePointsAccessCheck;
        vp.NamedStreams = NamedStreams;
        vp.ExtendedAttributes = ExtendedAttributes;
        vp.ReadOnlyVolume = ReadOnlyVolume;
        vp.PostCleanupWhenModifiedOnly = PostCleanupWhenModifiedOnly;
        vp.PassQueryDirectoryPattern = PassQueryDirectoryPattern;
        vp.PassQueryDirectoryFileName = PassQueryDirectoryFileName;
        vp.FlushAndPurgeOnCleanup = FlushAndPurgeOnCleanup;
        vp.DeviceControl = DeviceControl;
        vp.AllowOpenInKernelMode = AllowOpenInKernelMode;
        vp.WslFeatures = WslFeatures;
        vp.RejectIrpPriorToTransact0 = RejectIrpPriorToTransact0;
        vp.SupportsPosixUnlinkRename = SupportsPosixUnlinkRename;
        vp.PostDispositionWhenNecessaryOnly = PostDispositionWhenNecessaryOnly;
        vp.UmFileContextIsFullContext = true; // always FullContext for handle tracking
        if (Prefix != null) vp.SetPrefix(Prefix);
        vp.SetFileSystemName(FileSystemName);
    }

    // ═══════════════════════════════════════════
    //  Populate function pointers
    // ═══════════════════════════════════════════

    private static void PopulateInterface(ref FspFileSystemInterface iface)
    {
        iface.GetVolumeInfo = &OnGetVolumeInfo;
        iface.SetVolumeLabel = &OnSetVolumeLabel;
        iface.GetSecurityByName = &OnGetSecurityByName;
        iface.Create = &OnCreate;
        iface.Open = &OnOpen;
        iface.Overwrite = &OnOverwrite;
        iface.Cleanup = &OnCleanup;
        iface.Close = &OnClose;
        iface.Read = &OnRead;
        iface.Write = &OnWrite;
        iface.Flush = &OnFlush;
        iface.GetFileInfo = &OnGetFileInfo;
        iface.SetBasicInfo = &OnSetBasicInfo;
        iface.SetFileSize = &OnSetFileSize;
        iface.CanDelete = &OnCanDelete;
        iface.Rename = &OnRename;
        iface.GetSecurity = &OnGetSecurity;
        iface.SetSecurity = &OnSetSecurity;
        iface.ReadDirectory = &OnReadDirectory;
        iface.GetReparsePoint = &OnGetReparsePoint;
        iface.SetReparsePoint = &OnSetReparsePoint;
        iface.DeleteReparsePoint = &OnDeleteReparsePoint;
        iface.GetStreamInfo = &OnGetStreamInfo;
        iface.GetDirInfoByName = &OnGetDirInfoByName;
        iface.Control = &OnControl;
        iface.GetEa = &OnGetEa;
        iface.SetEa = &OnSetEa;
        iface.DispatcherStopped = &OnDispatcherStopped;
    }

    // ═══════════════════════════════════════════
    //  [UnmanagedCallersOnly] Callbacks
    //  Each: Self(fs) → IFileSystem method → NTSTATUS or STATUS_PENDING
    // ═══════════════════════════════════════════

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetVolumeInfo(nint fs, FspVolumeInfo* pVi)
    {
        var self = Self(fs);
        try
        {
            int r = self._fs.GetVolumeInfo(out ulong total, out ulong free, out string label);
            if (r >= 0) { pVi->TotalSize = total; pVi->FreeSize = free; pVi->SetVolumeLabel(label); }
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetVolumeLabel(nint fs, char* label, FspVolumeInfo* pVi)
    {
        var self = Self(fs);
        try
        {
            string s = new(label);
            int r = self._fs.SetVolumeLabel(s, out ulong total, out ulong free);
            if (r >= 0) { pVi->TotalSize = total; pVi->FreeSize = free; pVi->SetVolumeLabel(s); }
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetSecurityByName(nint fs, char* fileName, uint* pAttr, nint sd, nuint* pSdSize)
    {
        var self = Self(fs);
        try
        {
            string name = new(fileName);
            byte[]? sdBytes = pSdSize != null ? [] : null;
            int r = self._fs.GetFileSecurityByName(name, out uint attr, ref sdBytes);
            if (r >= 0 || r == NtStatus.Reparse)
            {
                if (pAttr != null) *pAttr = attr;
                if (pSdSize != null) CopySecurityDescriptor(sdBytes, sd, pSdSize);
            }
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnCreate(nint fs, char* fileName, uint co, uint ga, uint fa, nint sd, ulong alloc,
        FspFullContext* ctx, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = new HandleState { FileName = new string(fileName) };
            byte[]? sdBytes = MakeSecurityDescriptor(sd);
            var task = self._fs.CreateFile(hs.FileName, co, ga, fa, sdBytes, alloc, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) { SetHandle(ctx, hs); *pFi = r.FileInfo; }
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnOpen(nint fs, char* fileName, uint co, uint ga, FspFullContext* ctx, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = new HandleState { FileName = new string(fileName) };
            var task = self._fs.OpenFile(hs.FileName, co, ga, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) { SetHandle(ctx, hs); *pFi = r.FileInfo; }
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnOverwrite(nint fs, FspFullContext* ctx, uint fa, byte rfa, ulong alloc, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var task = self._fs.OverwriteFile(fa, rfa != 0, alloc, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) *pFi = r.FileInfo;
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnCleanup(nint fs, FspFullContext* ctx, char* fileName, uint flags)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            hs.Info.CancelPendingOperations();
            string? name = fileName != null ? new string(fileName) : null;
            self._fs.Cleanup(name ?? hs.FileName, hs.Info, (CleanupFlags)flags);
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnClose(nint fs, FspFullContext* ctx)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            self._fs.Close(hs.Info);
            hs.Info.DisposeResources();
        }
        catch { }
        finally { FreeHandle(ctx); }
    }

    // ── Read/Write: unified ValueTask → sync or STATUS_PENDING ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnRead(nint fs, FspFullContext* ctx, nint buffer, ulong offset, uint length, uint* pBt)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var owned = self._bufferPool.RentAsMemory((int)length);
            var task = self._fs.ReadFile(hs.FileName!, owned.Memory, offset, hs.Info, hs.Info.CancellationToken);

            if (task.IsCompletedSuccessfully)
            {
                var r = task.Result;
                if (r.Status >= 0 && r.BytesTransferred > 0)
                    owned.CopyTo(new Span<byte>((void*)buffer, (int)length), (int)r.BytesTransferred);
                owned.Dispose();
                *pBt = r.BytesTransferred;
                return r.Status;
            }

            // Async path → STATUS_PENDING
            var opCtx = FspApi.FspFileSystemGetOperationContext();
            var response = opCtx->Response;
            task.AsTask().ContinueWith(t =>
            {
                try
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        var r = t.Result;
                        if (r.Status >= 0 && r.BytesTransferred > 0)
                            owned.CopyTo(new Span<byte>((void*)buffer, (int)length), (int)r.BytesTransferred);
                        response->IoStatusStatus = r.Status;
                        response->IoStatusInformation = r.BytesTransferred;
                    }
                    else if (t.IsCanceled)
                    {
                        response->IoStatusStatus = NtStatus.Cancelled;
                        response->IoStatusInformation = 0;
                    }
                    else
                    {
                        response->IoStatusStatus = NtStatus.UnexpectedIoError;
                        response->IoStatusInformation = 0;
                    }
                    FspApi.FspFileSystemSendResponse(fs, response);
                }
                catch
                {
                    response->IoStatusStatus = NtStatus.UnexpectedIoError;
                    response->IoStatusInformation = 0;
                    FspApi.FspFileSystemSendResponse(fs, response);
                }
                finally { owned.Dispose(); }
            }, TaskContinuationOptions.ExecuteSynchronously);
            *pBt = 0;
            return NtStatus.Pending;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnWrite(nint fs, FspFullContext* ctx, nint buffer, ulong offset, uint length,
        byte wteof, byte cio, uint* pBt, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var owned = self._bufferPool.RentAsMemory((int)length);
            owned.CopyFrom(new ReadOnlySpan<byte>((void*)buffer, (int)length));

            var task = self._fs.WriteFile(hs.FileName!, owned.Memory, offset,
                wteof != 0, cio != 0, hs.Info, hs.Info.CancellationToken);

            if (task.IsCompletedSuccessfully)
            {
                var r = task.Result;
                owned.Dispose();
                *pBt = r.BytesTransferred;
                if (r.Status >= 0) *pFi = r.FileInfo;
                return r.Status;
            }

            var opCtx = FspApi.FspFileSystemGetOperationContext();
            var response = opCtx->Response;
            task.AsTask().ContinueWith(t =>
            {
                try
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        var r = t.Result;
                        response->IoStatusStatus = r.Status;
                        response->IoStatusInformation = r.BytesTransferred;
                        if (r.Status >= 0) response->FileInfo = r.FileInfo;
                    }
                    else if (t.IsCanceled) { response->IoStatusStatus = NtStatus.Cancelled; }
                    else { response->IoStatusStatus = NtStatus.UnexpectedIoError; }
                    FspApi.FspFileSystemSendResponse(fs, response);
                }
                catch
                {
                    response->IoStatusStatus = NtStatus.UnexpectedIoError;
                    FspApi.FspFileSystemSendResponse(fs, response);
                }
                finally { owned.Dispose(); }
            }, TaskContinuationOptions.ExecuteSynchronously);
            *pBt = 0;
            *pFi = default;
            return NtStatus.Pending;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    // ── ReadDirectory: same pattern ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnReadDirectory(nint fs, FspFullContext* ctx, char* pattern, char* marker,
        nint buffer, uint length, uint* pBt)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            string? pat = pattern != null ? new string(pattern) : null;
            string? mark = marker != null ? new string(marker) : null;
            var task = self._fs.ReadDirectory(hs.FileName!, pat, mark, buffer, length,
                hs.Info, hs.Info.CancellationToken);

            if (task.IsCompletedSuccessfully)
            {
                var r = task.Result;
                *pBt = r.BytesTransferred;
                return r.Status;
            }

            var opCtx = FspApi.FspFileSystemGetOperationContext();
            var response = opCtx->Response;
            task.AsTask().ContinueWith(t =>
            {
                try
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        response->IoStatusStatus = t.Result.Status;
                        response->IoStatusInformation = t.Result.BytesTransferred;
                    }
                    else if (t.IsCanceled) { response->IoStatusStatus = NtStatus.Cancelled; }
                    else { response->IoStatusStatus = NtStatus.UnexpectedIoError; }
                    FspApi.FspFileSystemSendResponse(fs, response);
                }
                catch
                {
                    response->IoStatusStatus = NtStatus.UnexpectedIoError;
                    FspApi.FspFileSystemSendResponse(fs, response);
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
            *pBt = 0;
            return NtStatus.Pending;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    // ── Simple sync callbacks ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnFlush(nint fs, FspFullContext* ctx, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = ctx->UserContext2 != 0 ? H(ctx) : null;
            var info = hs?.Info ?? new FileOperationInfo();
            var task = self._fs.FlushFileBuffers(hs?.FileName, info, info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) *pFi = r.FileInfo;
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetFileInfo(nint fs, FspFullContext* ctx, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var task = self._fs.GetFileInformation(hs.FileName!, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) *pFi = r.FileInfo;
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetBasicInfo(nint fs, FspFullContext* ctx, uint fa, ulong ct, ulong lat, ulong lwt, ulong cht, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var task = self._fs.SetFileAttributes(hs.FileName!, fa, ct, lat, lwt, cht, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) *pFi = r.FileInfo;
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetFileSize(nint fs, FspFullContext* ctx, ulong sz, byte alloc, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var task = self._fs.SetFileSize(hs.FileName!, sz, alloc != 0, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r.Status >= 0) *pFi = r.FileInfo;
            return r.Status;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnCanDelete(nint fs, FspFullContext* ctx, char* fn)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var task = self._fs.CanDelete(new string(fn), hs.Info, hs.Info.CancellationToken);
            return task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnRename(nint fs, FspFullContext* ctx, char* fn, char* newFn, byte replace)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            string newName = new(newFn);
            var task = self._fs.MoveFile(new string(fn), newName, replace != 0, hs.Info, hs.Info.CancellationToken);
            int r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();
            if (r >= 0) hs.FileName = newName;
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    // ── Security ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetSecurity(nint fs, FspFullContext* ctx, nint sd, nuint* pSdSize)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            byte[]? sdBytes = pSdSize != null ? [] : null;
            int r = self._fs.GetFileSecurity(hs.FileName!, ref sdBytes, hs.Info);
            if (r >= 0 && pSdSize != null) CopySecurityDescriptor(sdBytes, sd, pSdSize);
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetSecurity(nint fs, FspFullContext* ctx, uint si, nint md)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            byte[] mdBytes = MakeSecurityDescriptor(md) ?? [];
            return self._fs.SetFileSecurity(hs.FileName!, si, mdBytes, hs.Info);
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    // ── Reparse ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetReparsePoint(nint fs, FspFullContext* ctx, char* fn, nint buf, nuint* pSz)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            byte[]? data = null;
            int r = self._fs.GetReparsePoint(new string(fn), ref data, hs.Info);
            if (r >= 0) CopyReparseData(data, buf, pSz);
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetReparsePoint(nint fs, FspFullContext* ctx, char* fn, nint buf, nuint sz)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            byte[] data = new byte[(int)sz];
            Marshal.Copy(buf, data, 0, data.Length);
            return self._fs.SetReparsePoint(new string(fn), data, hs.Info);
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnDeleteReparsePoint(nint fs, FspFullContext* ctx, char* fn, nint buf, nuint sz)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            byte[] data = new byte[(int)sz];
            Marshal.Copy(buf, data, 0, data.Length);
            return self._fs.DeleteReparsePoint(new string(fn), data, hs.Info);
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    // ── Streams / EA / IOCTL / Misc ──

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetStreamInfo(nint fs, FspFullContext* ctx, nint buf, uint len, uint* pBt)
    {
        var self = Self(fs);
        try { var hs = H(ctx); int r = self._fs.GetStreamInfo(hs.FileName!, buf, len, out uint bt, hs.Info); *pBt = bt; return r; }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetDirInfoByName(nint fs, FspFullContext* ctx, char* fn, FspDirInfo* pDi)
    {
        var self = Self(fs);
        try { var hs = H(ctx); int r = self._fs.GetDirInfoByName(hs.FileName!, new string(fn), out var di, hs.Info); if (r >= 0) *pDi = di; return r; }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnControl(nint fs, FspFullContext* ctx, uint cc, nint inBuf, uint inLen, nint outBuf, uint outLen, uint* pBt)
    {
        var self = Self(fs);
        try
        {
            var hs = H(ctx);
            var input = new ReadOnlySpan<byte>((void*)inBuf, (int)inLen);
            var output = new Span<byte>((void*)outBuf, (int)outLen);
            int r = self._fs.DeviceControl(hs.FileName!, cc, input, output, out uint bt, hs.Info);
            *pBt = bt;
            return r;
        }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnGetEa(nint fs, FspFullContext* ctx, nint ea, uint len, uint* pBt)
    {
        var self = Self(fs);
        try { var hs = H(ctx); int r = self._fs.GetEa(hs.FileName!, ea, len, out uint bt, hs.Info); *pBt = bt; return r; }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static int OnSetEa(nint fs, FspFullContext* ctx, nint ea, uint len, FspFileInfo* pFi)
    {
        var self = Self(fs);
        try { var hs = H(ctx); int r = self._fs.SetEa(hs.FileName!, ea, len, out var fi, hs.Info); if (r >= 0) *pFi = fi; return r; }
        catch (Exception ex) { return self.HandleException(ex); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnDispatcherStopped(nint fs, byte normally)
    {
        try { Self(fs)._fs.DispatcherStopped(normally != 0); } catch { }
    }

    // ═══════════════════════════════════════════
    //  Security descriptor helpers
    // ═══════════════════════════════════════════

    private static void CopySecurityDescriptor(byte[]? sd, nint buffer, nuint* pSize)
    {
        if (sd != null && sd.Length > 0)
        {
            if ((int)*pSize < sd.Length) { *pSize = (nuint)sd.Length; return; }
            *pSize = (nuint)sd.Length;
            if (buffer != 0) Marshal.Copy(sd, 0, buffer, sd.Length);
        }
        else { *pSize = 0; }
    }

    private static byte[]? MakeSecurityDescriptor(nint ptr)
    {
        if (ptr == 0) return null;
        int len = (int)GetSecurityDescriptorLength(ptr);
        byte[] sd = new byte[len];
        Marshal.Copy(ptr, sd, 0, len);
        return sd;
    }

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityDescriptorLength(nint pSecurityDescriptor);

    private static void CopyReparseData(byte[]? data, nint buffer, nuint* pSize)
    {
        if (data != null && data.Length > 0)
        {
            if ((int)*pSize < data.Length) { *pSize = (nuint)data.Length; return; }
            *pSize = (nuint)data.Length;
            if (buffer != 0) Marshal.Copy(data, 0, buffer, data.Length);
        }
        else { *pSize = 0; }
    }
}
