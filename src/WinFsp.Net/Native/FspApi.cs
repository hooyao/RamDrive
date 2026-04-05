using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinFsp.Native;

/// <summary>
/// P/Invoke declarations for winfsp-x64.dll public API functions.
/// All functions use __cdecl calling convention.
/// DLL is resolved via NativeLibrary with registry fallback.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class FspApi
{
    private const string DllName = "winfsp-x64";

    public const string FspFsctlDiskDeviceName = @"\Device\WinFsp.Disk";
    public const string FspFsctlNetDeviceName = @"\Device\WinFsp.Net";

    /// <summary>Device path for disk-style file systems (drive letter mount).</summary>
    public const string DiskDevicePath = @"WinFsp.Disk";
    /// <summary>Device path for network-style file systems (UNC mount).</summary>
    public const string NetDevicePath = @"WinFsp.Net";

    // ── FileSystem lifecycle ──

    [LibraryImport(DllName, EntryPoint = "FspFileSystemPreflight", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int FspFileSystemPreflight(string devicePath, string? mountPoint);

    [LibraryImport(DllName, EntryPoint = "FspFileSystemCreate", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int FspFileSystemCreate(
        string devicePath,
        nint volumeParams,
        nint fileSystemInterface,
        out nint pFileSystem);

    [LibraryImport(DllName, EntryPoint = "FspFileSystemDelete")]
    internal static partial void FspFileSystemDelete(nint fileSystem);

    [LibraryImport(DllName, EntryPoint = "FspFileSystemSetMountPointEx", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int FspFileSystemSetMountPointEx(
        nint fileSystem, string? mountPoint, nint securityDescriptor);

    [LibraryImport(DllName, EntryPoint = "FspFileSystemRemoveMountPoint")]
    internal static partial void FspFileSystemRemoveMountPoint(nint fileSystem);

    [LibraryImport(DllName, EntryPoint = "FspFileSystemStartDispatcher")]
    internal static partial int FspFileSystemStartDispatcher(nint fileSystem, uint threadCount);

    [LibraryImport(DllName, EntryPoint = "FspFileSystemStopDispatcher")]
    internal static partial void FspFileSystemStopDispatcher(nint fileSystem);

    // ── Async response ──

    [LibraryImport(DllName, EntryPoint = "FspFileSystemSendResponse")]
    internal static unsafe partial void FspFileSystemSendResponse(nint fileSystem, FspTransactRsp* response);

    // ── Operation context ──

    [LibraryImport(DllName, EntryPoint = "FspFileSystemGetOperationContext")]
    internal static unsafe partial FspOperationContext* FspFileSystemGetOperationContext();

    // ── Mount point ──

    [LibraryImport(DllName, EntryPoint = "FspFileSystemMountPointF")]
    internal static partial nint FspFileSystemMountPoint(nint fileSystem);

    // ── Guard strategy ──

    [LibraryImport(DllName, EntryPoint = "FspFileSystemSetOperationGuardStrategyF")]
    internal static partial void FspFileSystemSetOperationGuardStrategy(nint fileSystem, int guardStrategy);

    // ── Debug log ──

    [LibraryImport(DllName, EntryPoint = "FspFileSystemSetDebugLogF")]
    internal static partial void FspFileSystemSetDebugLog(nint fileSystem, uint debugLog);

    [LibraryImport(DllName, EntryPoint = "FspDebugLogSetHandle")]
    internal static partial void FspDebugLogSetHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetStdHandle")]
    internal static partial nint GetStdHandle(uint nStdHandle);

    internal const uint STD_ERROR_HANDLE = unchecked((uint)(-12));

    // ── DirInfo helpers ──

    [LibraryImport(DllName, EntryPoint = "FspFileSystemAddDirInfo")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static unsafe partial bool FspFileSystemAddDirInfo(
        FspDirInfo* dirInfo, nint buffer, uint length, uint* pBytesTransferred);

    /// <summary>Signal end of directory enumeration.</summary>
    internal static unsafe bool FspFileSystemEndDirInfo(nint buffer, uint length, uint* pBytesTransferred)
        => FspFileSystemAddDirInfo(null, buffer, length, pBytesTransferred);

    // ── StreamInfo helpers ──

    [LibraryImport(DllName, EntryPoint = "FspFileSystemAddStreamInfo")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static unsafe partial bool FspFileSystemAddStreamInfo(
        FspStreamInfo* streamInfo, nint buffer, uint length, uint* pBytesTransferred);

    internal static unsafe bool FspFileSystemEndStreamInfo(nint buffer, uint length, uint* pBytesTransferred)
        => FspFileSystemAddStreamInfo(null, buffer, length, pBytesTransferred);

    // ── Notify ──

    [LibraryImport(DllName, EntryPoint = "FspFileSystemNotifyBegin")]
    internal static partial int FspFileSystemNotifyBegin(nint fileSystem, uint timeout);

    [LibraryImport(DllName, EntryPoint = "FspFileSystemNotifyEnd")]
    internal static partial int FspFileSystemNotifyEnd(nint fileSystem);

    // ── Utility ──

    [LibraryImport(DllName, EntryPoint = "FspNtStatusFromWin32")]
    internal static partial int FspNtStatusFromWin32(uint win32Error);

    [LibraryImport(DllName, EntryPoint = "FspWin32FromNtStatus")]
    internal static partial uint FspWin32FromNtStatus(int status);

    [LibraryImport(DllName, EntryPoint = "FspVersion")]
    internal static partial int FspVersion(out uint pVersion);

    // ── Security descriptor helpers ──

    [LibraryImport(DllName, EntryPoint = "FspSetSecurityDescriptor")]
    internal static partial int FspSetSecurityDescriptor(
        nint inputDescriptor, uint securityInformation, nint modificationDescriptor,
        out nint pSecurityDescriptor);

    [LibraryImport(DllName, EntryPoint = "FspDeleteSecurityDescriptor")]
    internal static partial void FspDeleteSecurityDescriptor(nint securityDescriptor, nint createFunc);

    // ── Process ID ──

    [LibraryImport(DllName, EntryPoint = "FspFileSystemOperationProcessIdF")]
    internal static partial uint FspFileSystemOperationProcessId();

    // ── DLL resolution ──

    static FspApi()
    {
        NativeLibrary.SetDllImportResolver(typeof(FspApi).Assembly, ResolveFspDll);
    }

    private static nint ResolveFspDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != DllName)
            return 0;

        // Try standard search paths first
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out nint handle))
            return handle;

        // Fallback: read WinFsp installation directory from registry
        string? installDir = Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\WinFsp",
            "InstallDir", null) as string;

        if (installDir != null)
        {
            string dllPath = Path.Combine(installDir, "bin", "winfsp-x64.dll");
            if (NativeLibrary.TryLoad(dllPath, out handle))
                return handle;
        }

        return 0;
    }

    // ── UserContext access ──

    /// <summary>
    /// Read the UserContext field from an FSP_FILE_SYSTEM struct.
    /// FSP_FILE_SYSTEM layout: UINT16 Version (offset 0) + padding + PVOID UserContext (offset 8 on x64).
    /// Verified by FSP_FSCTL_STATIC_ASSERT(792 == sizeof(FSP_FILE_SYSTEM)) in winfsp.h.
    /// </summary>
    internal static unsafe nint GetUserContext(nint fileSystem)
        => *(nint*)((byte*)fileSystem + nint.Size); // offset 8 on x64 (sizeof(nint) for alignment after UINT16)

    internal static unsafe void SetUserContext(nint fileSystem, nint value)
        => *(nint*)((byte*)fileSystem + nint.Size) = value;
}
