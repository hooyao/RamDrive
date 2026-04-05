// HelloFs — Minimal WinFSP file system using the low-level WinFsp.Net API.
// Mounts a read-only drive with a single file: \hello.txt containing "example".

using System.Runtime.InteropServices;
using WinFsp;
using WinFsp.Native;

unsafe
{
    var fs = new WinFspFileSystem();

    fs.VolumeParams.SectorSize = 512;
    fs.VolumeParams.SectorsPerAllocationUnit = 1;
    fs.VolumeParams.MaxComponentLength = 255;
    fs.VolumeParams.VolumeCreationTime = HelloFs.Now;
    fs.VolumeParams.VolumeSerialNumber = 0x12345678;
    fs.VolumeParams.CasePreservedNames = true;
    fs.VolumeParams.UnicodeOnDisk = true;
    fs.VolumeParams.ReadOnlyVolume = true;
    fs.VolumeParams.UmFileContextIsFullContext = true;
    fs.VolumeParams.SetFileSystemName("HelloFs");

    // All callbacks via delegate — [UnmanagedCallersOnly] doesn't work on WinFSP dispatcher threads
    HelloFs.PinDelegates();
    nint* s = (nint*)fs.InterfacePtr;
    s[0]  = HelloFs.Ptrs.GetVolumeInfo;
    s[2]  = HelloFs.Ptrs.GetSecurityByName;
    s[3]  = HelloFs.Ptrs.Create;       // Required by WinFSP (Create or CreateEx must be non-null)
    s[4]  = HelloFs.Ptrs.Open;
    s[5]  = HelloFs.Ptrs.Overwrite;    // Required by WinFSP (Overwrite or OverwriteEx must be non-null)
    s[6]  = HelloFs.Ptrs.Cleanup;
    s[7]  = HelloFs.Ptrs.Close;
    s[8]  = HelloFs.Ptrs.Read;
    s[11] = HelloFs.Ptrs.GetFileInfo;
    s[18] = HelloFs.Ptrs.ReadDirectory;

    WinFspFileSystem.SetDebugLogToStderr();

    int result = fs.Mount("M:", debugLog: uint.MaxValue);
    if (result < 0)
    {
        Console.Error.WriteLine($"Mount failed: 0x{result:X8}");
        return 1;
    }

    Console.WriteLine($"Mounted at {fs.MountPoint}");

    // Self-test
    try
    {
        Console.WriteLine($"Directory.Exists(M:\\): {System.IO.Directory.Exists(@"M:\")}");
        var files = System.IO.Directory.GetFiles(@"M:\");
        Console.WriteLine($"Files: [{string.Join(", ", files)}]");
        if (files.Length > 0)
        {
            string content = System.IO.File.ReadAllText(files[0]);
            Console.WriteLine($"Content of {files[0]}: \"{content}\"");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Self-test error: {ex.GetType().Name}: {ex.Message}");
    }

    if (Console.IsInputRedirected)
    {
        Console.WriteLine("Running for 30 seconds...");
        Thread.Sleep(30_000);
    }
    else
    {
        Console.WriteLine("Press Enter to unmount...");
        Console.ReadLine();
    }

    fs.Unmount();
    return 0;
}

// ═════════════════════════════════════════
//  Callbacks — all via delegate (not [UnmanagedCallersOnly])
// ═════════════════════════════════════════

static unsafe class HelloFs
{
    internal const string FileName = "hello.txt";
    internal static readonly byte[] ContentBytes = "example"u8.ToArray();
    internal static readonly ulong Now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
    const uint AttrDir = 0x10;
    const uint AttrNormal = 0x80;
    const uint AttrReadOnly = 0x01;

    // ── Delegate types matching WinFSP C function signatures ──

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetVolumeInfoDelegate(nint fileSystem, FspVolumeInfo* pVolumeInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetSecurityByNameDelegate(nint fileSystem, char* fileName, uint* pFileAttributes, nint securityDescriptor, nuint* pSecurityDescriptorSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int CreateDelegate(nint fileSystem, char* fileName, uint createOptions, uint grantedAccess, uint fileAttributes, nint securityDescriptor, ulong allocationSize, FspFullContext* fullContext, FspFileInfo* pFileInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int OpenDelegate(nint fileSystem, char* fileName, uint createOptions, uint grantedAccess, FspFullContext* fullContext, FspFileInfo* pFileInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int OverwriteDelegate(nint fileSystem, FspFullContext* fullContext, uint fileAttributes, byte replaceFileAttributes, ulong allocationSize, FspFileInfo* pFileInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CleanupDelegate(nint fileSystem, FspFullContext* fullContext, char* fileName, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CloseDelegate(nint fileSystem, FspFullContext* fullContext);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ReadDelegate(nint fileSystem, FspFullContext* fullContext, nint buffer, ulong offset, uint length, uint* pBytesTransferred);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetFileInfoDelegate(nint fileSystem, FspFullContext* fullContext, FspFileInfo* pFileInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ReadDirectoryDelegate(nint fileSystem, FspFullContext* fullContext, char* pattern, char* marker, nint buffer, uint length, uint* pBytesTransferred);

    // ── Pinned delegates + function pointers ──

    internal struct FuncPtrs
    {
        public nint GetVolumeInfo, GetSecurityByName, Create, Open, Overwrite, Cleanup, Close, Read, GetFileInfo, ReadDirectory;
    }

    internal static FuncPtrs Ptrs;

    internal static void PinDelegates()
    {
        Pin(new GetVolumeInfoDelegate(OnGetVolumeInfo), out Ptrs.GetVolumeInfo);
        Pin(new GetSecurityByNameDelegate(OnGetSecurityByName), out Ptrs.GetSecurityByName);
        Pin(new CreateDelegate(OnCreate), out Ptrs.Create);
        Pin(new OpenDelegate(OnOpen), out Ptrs.Open);
        Pin(new OverwriteDelegate(OnOverwrite), out Ptrs.Overwrite);
        Pin(new CleanupDelegate(OnCleanup), out Ptrs.Cleanup);
        Pin(new CloseDelegate(OnClose), out Ptrs.Close);
        Pin(new ReadDelegate(OnRead), out Ptrs.Read);
        Pin(new GetFileInfoDelegate(OnGetFileInfo), out Ptrs.GetFileInfo);
        Pin(new ReadDirectoryDelegate(OnReadDirectory), out Ptrs.ReadDirectory);
    }

    static void Pin<T>(T d, out nint ptr) where T : Delegate
    {
        GCHandle.Alloc(d);
        ptr = Marshal.GetFunctionPointerForDelegate(d);
    }

    // ── Callback implementations ──

    static int OnGetVolumeInfo(nint fs, FspVolumeInfo* pVi)
    {
        pVi->TotalSize = 1024 * 1024;
        pVi->FreeSize = 0;
        pVi->SetVolumeLabel("HelloFs");
        return NtStatus.Success;
    }

    static int OnGetSecurityByName(nint fs, char* fileName, uint* pAttr, nint sd, nuint* pSdSize)
    {
        string name = new(fileName);

        if (name == "\\")
        {
            if (pAttr != null) *pAttr = AttrDir;
        }
        else if (name == "\\" + FileName)
        {
            if (pAttr != null) *pAttr = AttrNormal | AttrReadOnly;
        }
        else
        {
            return NtStatus.ObjectNameNotFound;
        }

        if (pSdSize != null) *pSdSize = 0;
        return NtStatus.Success;
    }

    static int OnOpen(nint fs, char* fileName, uint co, uint ga, FspFullContext* ctx, FspFileInfo* pFi)
    {
        string name = new(fileName);

        if (name == "\\")
        {
            FillDirInfo(pFi);
            ctx->UserContext = 0;
            return NtStatus.Success;
        }

        if (name == "\\" + FileName)
        {
            FillFileInfo(pFi);
            ctx->UserContext = 1;
            return NtStatus.Success;
        }

        return NtStatus.ObjectNameNotFound;
    }

    // Create — read-only FS, reject new file creation but opening existing is handled by Open
    static int OnCreate(nint fs, char* fileName, uint co, uint ga, uint fa, nint sd, ulong alloc,
        FspFullContext* ctx, FspFileInfo* pFi)
    {
        return NtStatus.ObjectNameNotFound; // cannot create new files
    }

    // Overwrite — read-only FS, reject
    static int OnOverwrite(nint fs, FspFullContext* ctx, uint fa, byte rfa, ulong alloc, FspFileInfo* pFi)
    {
        *pFi = default;
        return NtStatus.MediaWriteProtected;
    }

    static void OnClose(nint fs, FspFullContext* ctx) { }
    static void OnCleanup(nint fs, FspFullContext* ctx, char* fn, uint flags) { }

    static int OnRead(nint fs, FspFullContext* ctx, nint buffer, ulong offset, uint length, uint* pBt)
    {
        if (ctx->UserContext != 1) return NtStatus.InvalidDeviceRequest;

        if (offset >= (ulong)ContentBytes.Length)
        {
            *pBt = 0;
            return NtStatus.EndOfFile;
        }

        uint available = (uint)((ulong)ContentBytes.Length - offset);
        uint toRead = Math.Min(length, available);

        fixed (byte* src = ContentBytes)
            Buffer.MemoryCopy(src + offset, (void*)buffer, length, toRead);

        *pBt = toRead;
        return NtStatus.Success;
    }

    static int OnGetFileInfo(nint fs, FspFullContext* ctx, FspFileInfo* pFi)
    {
        if (ctx->UserContext == 0) FillDirInfo(pFi);
        else FillFileInfo(pFi);
        return NtStatus.Success;
    }

    static int OnReadDirectory(nint fs, FspFullContext* ctx, char* pattern, char* marker,
        nint buffer, uint length, uint* pBt)
    {
        if (ctx->UserContext != 0) { *pBt = 0; return NtStatus.Success; }

        string? markerStr = marker != null ? new string(marker) : null;

        if (markerStr == null || string.Compare(markerStr, FileName, StringComparison.OrdinalIgnoreCase) < 0)
        {
            var dirInfo = new FspDirInfo();
            FillFileInfo(&dirInfo.FileInfo);
            dirInfo.SetFileName(FileName);
            if (!WinFspFileSystem.AddDirInfo(&dirInfo, buffer, length, pBt))
                return NtStatus.Success;
        }

        WinFspFileSystem.EndDirInfo(buffer, length, pBt);
        return NtStatus.Success;
    }

    // ── Helpers ──

    static void FillFileInfo(FspFileInfo* fi)
    {
        fi->FileAttributes = AttrNormal | AttrReadOnly;
        fi->FileSize = (ulong)ContentBytes.Length;
        fi->AllocationSize = 512;
        fi->CreationTime = Now;
        fi->LastAccessTime = Now;
        fi->LastWriteTime = Now;
        fi->ChangeTime = Now;
        fi->IndexNumber = 1;
    }

    static void FillDirInfo(FspFileInfo* fi)
    {
        fi->FileAttributes = AttrDir;
        fi->CreationTime = Now;
        fi->LastAccessTime = Now;
        fi->LastWriteTime = Now;
        fi->ChangeTime = Now;
    }
}
