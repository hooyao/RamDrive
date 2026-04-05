using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinFsp.Native;

/// <summary>
/// File metadata information. Matches FSP_FSCTL_FILE_INFO (72 bytes).
/// Layout from winfsp/fsctl.h lines 277-290.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FspFileInfo
{
    public uint FileAttributes;
    public uint ReparseTag;
    public ulong AllocationSize;
    public ulong FileSize;
    public ulong CreationTime;
    public ulong LastAccessTime;
    public ulong LastWriteTime;
    public ulong ChangeTime;
    public ulong IndexNumber;
    public uint HardLinks;
    public uint EaSize;
}

/// <summary>
/// Volume information. Matches FSP_FSCTL_VOLUME_INFO (88 bytes).
/// Layout from winfsp/fsctl.h lines 268-276.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FspVolumeInfo
{
    public const int VolumeLabelMaxChars = 32;

    public ulong TotalSize;
    public ulong FreeSize;
    public ushort VolumeLabelLength;
    public fixed char VolumeLabel[VolumeLabelMaxChars];

    public void SetVolumeLabel(ReadOnlySpan<char> label)
    {
        int len = Math.Min(label.Length, VolumeLabelMaxChars);
        fixed (char* p = VolumeLabel)
        {
            label[..len].CopyTo(new Span<char>(p, VolumeLabelMaxChars));
        }
        VolumeLabelLength = (ushort)(len * sizeof(char));
    }
}

/// <summary>
/// Volume parameters for FspFileSystemCreate. Matches FSP_FSCTL_VOLUME_PARAMS (504 bytes).
/// Layout from winfsp/fsctl.h lines 192-266.
///
/// The C struct uses bitfields for flags at offset 36 (32 bits) and offset 456 (V1 flags, 32 bits).
/// We represent these as plain uint fields with bit-manipulation properties.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 504)]
public unsafe struct FspVolumeParams
{
    public const int PrefixMaxChars = 192;
    public const int FileSystemNameMaxChars = 16;

    // == V0 fields ==
    [FieldOffset(0)] public ushort Version;
    [FieldOffset(2)] public ushort SectorSize;
    [FieldOffset(4)] public ushort SectorsPerAllocationUnit;
    [FieldOffset(6)] public ushort MaxComponentLength;
    [FieldOffset(8)] public ulong VolumeCreationTime;
    [FieldOffset(16)] public uint VolumeSerialNumber;
    [FieldOffset(20)] public uint TransactTimeout;
    [FieldOffset(24)] public uint IrpTimeout;
    [FieldOffset(28)] public uint IrpCapacity;
    [FieldOffset(32)] public uint FileInfoTimeout;

    /// <summary>
    /// Packed bitfield flags. Use the bool properties or SetFlag/ClearFlag helpers.
    /// Bit layout (LSB first):
    ///  0: CaseSensitiveSearch    1: CasePreservedNames    2: UnicodeOnDisk
    ///  3: PersistentAcls         4: ReparsePoints         5: ReparsePointsAccessCheck
    ///  6: NamedStreams           7: HardLinks             8: ExtendedAttributes
    ///  9: ReadOnlyVolume        10: PostCleanupWhenModifiedOnly  11: PassQueryDirectoryPattern
    /// 12: AlwaysUseDoubleBuffering  13: PassQueryDirectoryFileName  14: FlushAndPurgeOnCleanup
    /// 15: DeviceControl         16: UmFileContextIsUserContext2  17: UmFileContextIsFullContext
    /// 18: UmNoReparsePointsDirCheck  19-23: UmReservedFlags
    /// 24: AllowOpenInKernelMode  25: CasePreservedExtendedAttributes  26: WslFeatures
    /// 27: DirectoryMarkerAsNextOffset  28: RejectIrpPriorToTransact0  29: SupportsPosixUnlinkRename
    /// 30: PostDispositionWhenNecessaryOnly  31: KmReservedFlags
    /// </summary>
    [FieldOffset(36)] public uint Flags;

    [FieldOffset(40)] public fixed char Prefix[PrefixMaxChars];       // 384 bytes
    [FieldOffset(424)] public fixed char FileSystemName[FileSystemNameMaxChars]; // 32 bytes

    // == V1 extension fields (offset 456) ==
    /// <summary>
    /// V1 additional flags.
    /// Bit layout:
    ///  0: VolumeInfoTimeoutValid  1: DirInfoTimeoutValid  2: SecurityTimeoutValid
    ///  3: StreamInfoTimeoutValid  4: EaTimeoutValid  5-31: KmAdditionalReservedFlags
    /// </summary>
    [FieldOffset(456)] public uint AdditionalFlags;
    [FieldOffset(460)] public uint VolumeInfoTimeout;
    [FieldOffset(464)] public uint DirInfoTimeout;
    [FieldOffset(468)] public uint SecurityTimeout;
    [FieldOffset(472)] public uint StreamInfoTimeout;
    [FieldOffset(476)] public uint EaTimeout;
    [FieldOffset(480)] public uint FsextControlCode;
    // [484] Reserved32[1] = 4 bytes
    // [488] Reserved64[2] = 16 bytes
    // Total: 488 + 16 = 504 ✓

    // ── Flag helpers ──

    public bool CaseSensitiveSearch { get => GetFlag(0); set => SetFlag(0, value); }
    public bool CasePreservedNames { get => GetFlag(1); set => SetFlag(1, value); }
    public bool UnicodeOnDisk { get => GetFlag(2); set => SetFlag(2, value); }
    public bool PersistentAcls { get => GetFlag(3); set => SetFlag(3, value); }
    public bool ReparsePoints { get => GetFlag(4); set => SetFlag(4, value); }
    public bool ReparsePointsAccessCheck { get => GetFlag(5); set => SetFlag(5, value); }
    public bool NamedStreams { get => GetFlag(6); set => SetFlag(6, value); }
    public bool HardLinks { get => GetFlag(7); set => SetFlag(7, value); }
    public bool ExtendedAttributes { get => GetFlag(8); set => SetFlag(8, value); }
    public bool ReadOnlyVolume { get => GetFlag(9); set => SetFlag(9, value); }
    public bool PostCleanupWhenModifiedOnly { get => GetFlag(10); set => SetFlag(10, value); }
    public bool PassQueryDirectoryPattern { get => GetFlag(11); set => SetFlag(11, value); }
    public bool AlwaysUseDoubleBuffering { get => GetFlag(12); set => SetFlag(12, value); }
    public bool PassQueryDirectoryFileName { get => GetFlag(13); set => SetFlag(13, value); }
    public bool FlushAndPurgeOnCleanup { get => GetFlag(14); set => SetFlag(14, value); }
    public bool DeviceControl { get => GetFlag(15); set => SetFlag(15, value); }
    public bool UmFileContextIsUserContext2 { get => GetFlag(16); set => SetFlag(16, value); }
    public bool UmFileContextIsFullContext { get => GetFlag(17); set => SetFlag(17, value); }
    public bool AllowOpenInKernelMode { get => GetFlag(24); set => SetFlag(24, value); }
    public bool CasePreservedExtendedAttributes { get => GetFlag(25); set => SetFlag(25, value); }
    public bool WslFeatures { get => GetFlag(26); set => SetFlag(26, value); }
    public bool DirectoryMarkerAsNextOffset { get => GetFlag(27); set => SetFlag(27, value); }
    public bool RejectIrpPriorToTransact0 { get => GetFlag(28); set => SetFlag(28, value); }
    public bool SupportsPosixUnlinkRename { get => GetFlag(29); set => SetFlag(29, value); }
    public bool PostDispositionWhenNecessaryOnly { get => GetFlag(30); set => SetFlag(30, value); }

    // V1 flag helpers
    public bool VolumeInfoTimeoutValid { get => GetV1Flag(0); set => SetV1Flag(0, value); }
    public bool DirInfoTimeoutValid { get => GetV1Flag(1); set => SetV1Flag(1, value); }
    public bool SecurityTimeoutValid { get => GetV1Flag(2); set => SetV1Flag(2, value); }
    public bool StreamInfoTimeoutValid { get => GetV1Flag(3); set => SetV1Flag(3, value); }
    public bool EaTimeoutValid { get => GetV1Flag(4); set => SetV1Flag(4, value); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool GetFlag(int bit) => (Flags & (1u << bit)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetFlag(int bit, bool value)
    {
        if (value) Flags |= (1u << bit);
        else Flags &= ~(1u << bit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool GetV1Flag(int bit) => (AdditionalFlags & (1u << bit)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetV1Flag(int bit, bool value)
    {
        if (value) AdditionalFlags |= (1u << bit);
        else AdditionalFlags &= ~(1u << bit);
    }

    public void SetPrefix(ReadOnlySpan<char> value)
    {
        int len = Math.Min(value.Length, PrefixMaxChars - 1);
        fixed (char* p = Prefix)
        {
            value[..len].CopyTo(new Span<char>(p, PrefixMaxChars));
            p[len] = '\0';
        }
    }

    public void SetFileSystemName(ReadOnlySpan<char> value)
    {
        int len = Math.Min(value.Length, FileSystemNameMaxChars - 1);
        fixed (char* p = FileSystemName)
        {
            value[..len].CopyTo(new Span<char>(p, FileSystemNameMaxChars));
            p[len] = '\0';
        }
    }

    public readonly bool IsPrefixEmpty()
    {
        fixed (char* p = Prefix)
            return p[0] == '\0';
    }
}

/// <summary>
/// Full context for FullContext mode. Matches FSP_FSCTL_TRANSACT_FULL_CONTEXT (16 bytes).
/// Layout from winfsp/fsctl.h lines 331-335.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FspFullContext
{
    public ulong UserContext;
    public ulong UserContext2;
}

/// <summary>
/// Directory entry info header. Matches FSP_FSCTL_DIR_INFO (variable size).
/// Layout from winfsp/fsctl.h lines 299-310.
/// The FileNameBuf follows immediately after the Padding field.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FspDirInfo
{
    public const int FileNameBufMaxChars = 255;

    public ushort Size;
    public FspFileInfo FileInfo;
    public ulong NextOffset;
    private ulong _padding1;
    private ulong _padding2;
    public fixed char FileNameBuf[FileNameBufMaxChars];

    public void SetFileName(ReadOnlySpan<char> name)
    {
        int len = Math.Min(name.Length, FileNameBufMaxChars);
        fixed (char* p = FileNameBuf)
        {
            name[..len].CopyTo(new Span<char>(p, FileNameBufMaxChars));
        }
        Size = (ushort)(FileNameBufOffset + len * sizeof(char));
    }

    internal static readonly int FileNameBufOffset =
        (int)Marshal.OffsetOf<FspDirInfo>(nameof(FileNameBuf));
}

/// <summary>
/// Stream info header. Matches FSP_FSCTL_STREAM_INFO (variable size).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FspStreamInfo
{
    public const int StreamNameBufMaxChars = 255;

    public ushort Size;
    public ulong StreamSize;
    public ulong StreamAllocationSize;
    public fixed char StreamNameBuf[StreamNameBufMaxChars];

    public void SetStreamName(ReadOnlySpan<char> name)
    {
        int len = Math.Min(name.Length, StreamNameBufMaxChars);
        fixed (char* p = StreamNameBuf)
        {
            name[..len].CopyTo(new Span<char>(p, StreamNameBufMaxChars));
        }
        Size = (ushort)(StreamNameBufOffset + len * sizeof(char));
    }

    internal static readonly int StreamNameBufOffset =
        (int)Marshal.OffsetOf<FspStreamInfo>(nameof(StreamNameBuf));
}

/// <summary>
/// Operation context. Matches FSP_FILE_SYSTEM_OPERATION_CONTEXT.
/// Provides access to the Request and Response of the current operation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct FspOperationContext
{
    public FspTransactReq* Request;
    public FspTransactRsp* Response;
}

/// <summary>
/// Transaction request header. We only define the fields needed for async operations.
/// The full structure is 88 bytes but we only need Hint (at offset 8).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 88)]
public struct FspTransactReq
{
    [FieldOffset(0)] public ushort Version;
    [FieldOffset(2)] public ushort Size;
    [FieldOffset(4)] public uint Kind;
    [FieldOffset(8)] public ulong Hint;
}

/// <summary>
/// Transaction response header. Used for async completions via FspFileSystemSendResponse.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
public struct FspTransactRsp
{
    [FieldOffset(0)] public ushort Version;
    [FieldOffset(2)] public ushort Size;
    [FieldOffset(4)] public uint Kind;
    [FieldOffset(8)] public ulong Hint;
    [FieldOffset(16)] public uint IoStatusInformation;
    [FieldOffset(20)] public int IoStatusStatus;
    [FieldOffset(24)] public FspFileInfo FileInfo;
}

/// <summary>
/// Transaction kinds for identifying async operation types.
/// </summary>
public static class FspTransactKind
{
    public const uint Read = 5;
    public const uint Write = 6;
    public const uint QueryDirectory = 14;
}
