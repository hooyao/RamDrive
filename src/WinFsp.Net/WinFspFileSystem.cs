using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinFsp.Native;

namespace WinFsp;

/// <summary>
/// Low-level WinFSP file system wrapper. Thin shell around the native FSP_FILE_SYSTEM.
///
/// <para><b>Usage:</b></para>
/// <code>
/// var fs = new WinFspFileSystem();
/// fs.VolumeParams.SectorSize = 4096;
/// fs.VolumeParams.SetFileSystemName("MyFS");
/// fs.VolumeParams.CasePreservedNames = true;
///
/// // Fill function pointers directly
/// fs.Interface.GetVolumeInfo = &amp;MyCallbacks.OnGetVolumeInfo;
/// fs.Interface.Read = &amp;MyCallbacks.OnRead;
///
/// fs.UserContext = (nint)GCHandle.ToIntPtr(myHandle);
/// fs.Mount("Z:");
/// // ...
/// fs.Unmount();
/// </code>
///
/// <para>For a friendlier async API, use <see cref="IFileSystem"/> + <see cref="FileSystemHost"/>.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WinFspFileSystem : IDisposable
{
    private nint _fileSystemPtr;
    private readonly nint _interfacePtr;
    private readonly nint _volumeParamsPtr;

    public WinFspFileSystem()
    {
        // Allocate and zero the interface struct (512 bytes on x64)
        _interfacePtr = Marshal.AllocHGlobal(sizeof(FspFileSystemInterface));
        NativeMemory.Clear((void*)_interfacePtr, (nuint)sizeof(FspFileSystemInterface));

        // Allocate VolumeParams (504 bytes)
        _volumeParamsPtr = Marshal.AllocHGlobal(sizeof(FspVolumeParams));
        NativeMemory.Clear((void*)_volumeParamsPtr, (nuint)sizeof(FspVolumeParams));
        VolumeParams.Version = 504;
        // No flags set by default — user chooses FileContext mode
    }

    ~WinFspFileSystem() => Dispose();

    /// <summary>
    /// The 64-slot function pointer table. Fill your [UnmanagedCallersOnly] method addresses here before Mount().
    /// Null slots cause WinFSP to return STATUS_INVALID_DEVICE_REQUEST for that operation.
    /// </summary>
    public ref FspFileSystemInterface Interface => ref *(FspFileSystemInterface*)_interfacePtr;

    /// <summary>Volume configuration. Set before Mount().</summary>
    public ref FspVolumeParams VolumeParams => ref *(FspVolumeParams*)_volumeParamsPtr;

    /// <summary>
    /// User-defined pointer stored in FSP_FILE_SYSTEM.UserContext (offset 8 on x64).
    /// Set before Mount(). Retrieve in callbacks via FspApi.GetUserContext(fileSystem).
    /// </summary>
    public nint UserContext { get; set; }

    /// <summary>Raw pointer to the Interface struct for direct slot manipulation.</summary>
    public nint InterfacePtr => _interfacePtr;
    public nint Handle => _fileSystemPtr;

    /// <summary>Mount the file system.</summary>
    /// <param name="mountPoint">Drive letter ("X:") or directory. Null = auto-assign.</param>
    /// <param name="threadCount">Dispatcher threads. 0 = default.</param>
    /// <param name="synchronized">If true, coarse-grained locking.</param>
    /// <param name="debugLog">0 = off, uint.MaxValue = all.</param>
    public int Mount(string? mountPoint, uint threadCount = 0,
        bool synchronized = false, uint debugLog = 0)
    {
        if (_fileSystemPtr != 0)
            return NtStatus.InvalidDeviceRequest; // already mounted

        string devicePath = VolumeParams.IsPrefixEmpty() ? FspApi.DiskDevicePath : FspApi.NetDevicePath;
        Console.Error.WriteLine($"[WinFspFileSystem] _interfacePtr=0x{_interfacePtr:X}");
        Console.Error.WriteLine($"[WinFspFileSystem] first 8 bytes at _interfacePtr: 0x{*(nint*)_interfacePtr:X}");
        int result = FspApi.FspFileSystemCreate(devicePath, _volumeParamsPtr, _interfacePtr, out _fileSystemPtr);
        if (result < 0)
        {
            Console.Error.WriteLine($"[WinFspFileSystem] FspFileSystemCreate failed: 0x{result:X8} (devicePath={devicePath})");
            return result;
        }
        Console.Error.WriteLine($"[WinFspFileSystem] FspFileSystemCreate OK, handle=0x{_fileSystemPtr:X}");

        // Verify: read back the Interface pointer that WinFSP stored
        // FSP_FILE_SYSTEM layout (x64):
        //   Version(2) + pad(6) + UserContext(8) + VolumeName[64](128) + VolumeHandle(8)
        //   + EnterOp(8) + LeaveOp(8) + Operations[22](176) + Interface(8)
        //   = 8 + 8 + 128 + 8 + 8 + 8 + 176 = 344
        nint storedInterface = *(nint*)((byte*)_fileSystemPtr + 344);
        Console.Error.WriteLine($"[WinFspFileSystem] stored Interface ptr at offset 344 = 0x{storedInterface:X}");
        Console.Error.WriteLine($"[WinFspFileSystem] our _interfacePtr = 0x{_interfacePtr:X}");
        Console.Error.WriteLine($"[WinFspFileSystem] match: {storedInterface == _interfacePtr}");
        if (storedInterface != _interfacePtr)
        {
            // Scan the struct for our pointer
            for (int i = 0; i < 792; i += 8)
            {
                nint val = *(nint*)((byte*)_fileSystemPtr + i);
                if (val == _interfacePtr)
                    Console.Error.WriteLine($"[WinFspFileSystem] FOUND _interfacePtr at offset {i}!");
            }
            // Also read what's at offset 728 and dump its first few words
            nint ptrAt728 = *(nint*)((byte*)_fileSystemPtr + 728);
            Console.Error.WriteLine($"[WinFspFileSystem] ptr@728 = 0x{ptrAt728:X}");
            if (ptrAt728 != 0)
            {
                for (int i = 0; i < 10; i++)
                {
                    nint word = *(nint*)((byte*)ptrAt728 + i * 8);
                    Console.Error.WriteLine($"[WinFspFileSystem]   [{i}] = 0x{word:X}");
                }
            }
        }

        // Store user context
        if (UserContext != 0)
            FspApi.SetUserContext(_fileSystemPtr, UserContext);

        FspApi.FspFileSystemSetOperationGuardStrategy(_fileSystemPtr, synchronized ? 1 : 0);
        FspApi.FspFileSystemSetDebugLog(_fileSystemPtr, debugLog);

        result = FspApi.FspFileSystemSetMountPointEx(_fileSystemPtr, mountPoint, 0);
        if (result < 0)
        {
            Console.Error.WriteLine($"[WinFspFileSystem] SetMountPoint failed: 0x{result:X8} (mountPoint={mountPoint})");
            DestroyFs();
            return result;
        }
        Console.Error.WriteLine($"[WinFspFileSystem] SetMountPoint OK");

        result = FspApi.FspFileSystemStartDispatcher(_fileSystemPtr, threadCount);
        if (result < 0)
        {
            Console.Error.WriteLine($"[WinFspFileSystem] StartDispatcher failed: 0x{result:X8}");
            DestroyFs();
            return result;
        }
        Console.Error.WriteLine($"[WinFspFileSystem] StartDispatcher OK");

        return NtStatus.Success;
    }

    /// <summary>Unmount, stop dispatcher, and release native resources.</summary>
    public void Unmount()
    {
        if (_fileSystemPtr != 0)
        {
            FspApi.FspFileSystemStopDispatcher(_fileSystemPtr);
            DestroyFs();
        }
    }

    /// <summary>Get the mount point (null if not mounted).</summary>
    public string? MountPoint
    {
        get
        {
            if (_fileSystemPtr == 0) return null;
            nint ptr = FspApi.FspFileSystemMountPoint(_fileSystemPtr);
            return ptr != 0 ? Marshal.PtrToStringUni(ptr) : null;
        }
    }

    /// <summary>Send an async response for STATUS_PENDING operations.</summary>
    public void SendResponse(ref FspTransactRsp response)
    {
        fixed (FspTransactRsp* p = &response)
            FspApi.FspFileSystemSendResponse(_fileSystemPtr, p);
    }

    /// <summary>Get the operation context for the current callback (thread-local).</summary>
    public static FspOperationContext* GetOperationContext()
        => FspApi.FspFileSystemGetOperationContext();

    /// <summary>Redirect WinFSP debug log output to stderr. Call before Mount().</summary>
    public static void SetDebugLogToStderr()
        => FspApi.FspDebugLogSetHandle(FspApi.GetStdHandle(FspApi.STD_ERROR_HANDLE));

    // ═══════════════════════════════════════════
    //  Static helpers for directory enumeration
    // ═══════════════════════════════════════════

    /// <summary>Add a directory entry to the enumeration buffer.</summary>
    /// <returns>true if added, false if buffer full.</returns>
    public static bool AddDirInfo(FspDirInfo* dirInfo, nint buffer, uint length, uint* pBytesTransferred)
        => FspApi.FspFileSystemAddDirInfo(dirInfo, buffer, length, pBytesTransferred);

    /// <summary>Signal end of directory enumeration.</summary>
    public static void EndDirInfo(nint buffer, uint length, uint* pBytesTransferred)
        => FspApi.FspFileSystemEndDirInfo(buffer, length, pBytesTransferred);

    /// <summary>Add a stream info entry to the enumeration buffer.</summary>
    public static bool AddStreamInfo(FspStreamInfo* streamInfo, nint buffer, uint length, uint* pBytesTransferred)
        => FspApi.FspFileSystemAddStreamInfo(streamInfo, buffer, length, pBytesTransferred);

    /// <summary>Signal end of stream enumeration.</summary>
    public static void EndStreamInfo(nint buffer, uint length, uint* pBytesTransferred)
        => FspApi.FspFileSystemEndStreamInfo(buffer, length, pBytesTransferred);

    public void Dispose()
    {
        if (_fileSystemPtr != 0)
        {
            FspApi.FspFileSystemStopDispatcher(_fileSystemPtr);
            DestroyFs();
        }
        if (_interfacePtr != 0) Marshal.FreeHGlobal(_interfacePtr);
        if (_volumeParamsPtr != 0) Marshal.FreeHGlobal(_volumeParamsPtr);
        GC.SuppressFinalize(this);
    }

    private void DestroyFs()
    {
        if (_fileSystemPtr != 0)
        {
            FspApi.FspFileSystemDelete(_fileSystemPtr);
            _fileSystemPtr = 0;
        }
    }
}
