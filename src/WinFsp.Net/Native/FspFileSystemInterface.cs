using System.Runtime.InteropServices;

namespace WinFsp.Native;

/// <summary>
/// FSP_FILE_SYSTEM_INTERFACE: a struct of 64 function pointer slots that define all callbacks
/// a user-mode file system can implement.
///
/// Layout from winfsp/winfsp.h lines 193-1093.
/// Total size: 64 * sizeof(nint) = 512 bytes on x64.
///
/// Slots set to null (default nint = 0) cause WinFSP to automatically return STATUS_INVALID_DEVICE_REQUEST.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FspFileSystemInterface
{
    // Slot  0: GetVolumeInfo
    public delegate* unmanaged[Cdecl]<nint, FspVolumeInfo*, int> GetVolumeInfo;
    // Slot  1: SetVolumeLabel
    public delegate* unmanaged[Cdecl]<nint, char*, FspVolumeInfo*, int> SetVolumeLabel;
    // Slot  2: GetSecurityByName
    public delegate* unmanaged[Cdecl]<nint, char*, uint*, nint, nuint*, int> GetSecurityByName;
    // Slot  3: Create
    public delegate* unmanaged[Cdecl]<nint, char*, uint, uint, uint, nint, ulong,
        FspFullContext*, FspFileInfo*, int> Create;
    // Slot  4: Open
    public delegate* unmanaged[Cdecl]<nint, char*, uint, uint,
        FspFullContext*, FspFileInfo*, int> Open;
    // Slot  5: Overwrite
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, uint, byte, ulong,
        FspFileInfo*, int> Overwrite;
    // Slot  6: Cleanup
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, char*, uint, void> Cleanup;
    // Slot  7: Close
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, void> Close;
    // Slot  8: Read
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, nint, ulong, uint, uint*, int> Read;
    // Slot  9: Write
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, nint, ulong, uint, byte, byte,
        uint*, FspFileInfo*, int> Write;
    // Slot 10: Flush
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, FspFileInfo*, int> Flush;
    // Slot 11: GetFileInfo
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, FspFileInfo*, int> GetFileInfo;
    // Slot 12: SetBasicInfo
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, uint, ulong, ulong, ulong, ulong,
        FspFileInfo*, int> SetBasicInfo;
    // Slot 13: SetFileSize
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, ulong, byte, FspFileInfo*, int> SetFileSize;
    // Slot 14: CanDelete
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, char*, int> CanDelete;
    // Slot 15: Rename
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, char*, char*, byte, int> Rename;
    // Slot 16: GetSecurity
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, nint, nuint*, int> GetSecurity;
    // Slot 17: SetSecurity
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, uint, nint, int> SetSecurity;
    // Slot 18: ReadDirectory
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, char*, char*, nint, uint, uint*, int> ReadDirectory;
    // Slot 19: ResolveReparsePoints
    public nint ResolveReparsePoints;
    // Slot 20: GetReparsePoint
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, char*, nint, nuint*, int> GetReparsePoint;
    // Slot 21: SetReparsePoint
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, char*, nint, nuint, int> SetReparsePoint;
    // Slot 22: DeleteReparsePoint
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, char*, nint, nuint, int> DeleteReparsePoint;
    // Slot 23: GetStreamInfo
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, nint, uint, uint*, int> GetStreamInfo;
    // Slot 24: GetDirInfoByName
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, char*, FspDirInfo*, int> GetDirInfoByName;
    // Slot 25: Control (IOCTL)
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, uint, nint, uint, nint, uint, uint*, int> Control;
    // Slot 26: SetDelete
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, char*, byte, int> SetDelete;
    // Slot 27: CreateEx
    public delegate* unmanaged[Cdecl]<nint, char*, uint, uint, uint, nint, ulong, nint, uint, byte,
        FspFullContext*, FspFileInfo*, int> CreateEx;
    // Slot 28: OverwriteEx
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, uint, byte, ulong, nint, uint,
        FspFileInfo*, int> OverwriteEx;
    // Slot 29: GetEa
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, nint, uint, uint*, int> GetEa;
    // Slot 30: SetEa
    public delegate* unmanaged[Cdecl]<nint, FspFullContext*, nint, uint, FspFileInfo*, int> SetEa;
    // Slot 31: Obsolete0
    public nint Obsolete0;
    // Slot 32: DispatcherStopped
    public delegate* unmanaged[Cdecl]<nint, byte, void> DispatcherStopped;
    // Slots 33-63: Reserved (31 slots)
    // nint is not valid for fixed buffers; use ulong (same size on x64)
    private fixed ulong _reserved[31];
}
