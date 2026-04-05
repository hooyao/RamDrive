namespace WinFsp.Native;

/// <summary>
/// Common NTSTATUS codes used by WinFSP file system operations.
/// </summary>
public static class NtStatus
{
    public const int Success = 0x00000000;
    public const int Pending = 0x00000103;
    public const int BufferOverflow = unchecked((int)0x80000005);
    public const int NoMoreFiles = unchecked((int)0x80000006);

    public const int NotImplemented = unchecked((int)0xC0000002);
    public const int InvalidDeviceRequest = unchecked((int)0xC0000010);
    public const int EndOfFile = unchecked((int)0xC0000011);
    public const int MoreProcessingRequired = unchecked((int)0xC0000016);
    public const int AccessDenied = unchecked((int)0xC0000022);
    public const int BufferTooSmall = unchecked((int)0xC0000023);
    public const int ObjectNameInvalid = unchecked((int)0xC0000033);
    public const int ObjectNameNotFound = unchecked((int)0xC0000034);
    public const int ObjectNameCollision = unchecked((int)0xC0000035);
    public const int ObjectPathNotFound = unchecked((int)0xC000003A);
    public const int SharingViolation = unchecked((int)0xC0000043);
    public const int DeletionPending = unchecked((int)0xC0000056);
    public const int CannotDelete = unchecked((int)0xC0000121);
    public const int InsufficientResources = unchecked((int)0xC000009A);
    public const int DiskFull = unchecked((int)0xC000007F);
    public const int DirectoryNotEmpty = unchecked((int)0xC0000101);
    public const int NotADirectory = unchecked((int)0xC0000103);
    public const int FileIsADirectory = unchecked((int)0xC00000BA);
    public const int NotAReparse = unchecked((int)0xC0000275);
    public const int InternalError = unchecked((int)0xC00000E5);
    public const int UnexpectedIoError = unchecked((int)0xC00000E9);
    public const int Reparse = unchecked((int)0x00000104);
    public const int NoSuchFile = unchecked((int)0xC000000F);
    public const int MediaWriteProtected = unchecked((int)0xC00000A2);
    public const int Cancelled = unchecked((int)0xC0000120);
}
