using WinFsp.Native;

namespace WinFsp;

/// <summary>
/// Result of an async or sync Read operation.
/// </summary>
public readonly struct ReadResult
{
    public int Status { get; init; }
    public uint BytesTransferred { get; init; }

    public static ReadResult Success(uint bytesTransferred) => new()
    {
        Status = NtStatus.Success,
        BytesTransferred = bytesTransferred,
    };

    public static ReadResult EndOfFile() => new() { Status = NtStatus.EndOfFile };
    public static ReadResult Error(int status) => new() { Status = status };
}

/// <summary>
/// Result of an async or sync Write operation.
/// </summary>
public readonly struct WriteResult
{
    public int Status { get; init; }
    public uint BytesTransferred { get; init; }
    public FspFileInfo FileInfo { get; init; }

    public static WriteResult Success(uint bytesTransferred, FspFileInfo fileInfo) => new()
    {
        Status = NtStatus.Success,
        BytesTransferred = bytesTransferred,
        FileInfo = fileInfo,
    };

    public static WriteResult Error(int status) => new() { Status = status };
}

/// <summary>
/// Result of an async or sync ReadDirectory operation.
/// </summary>
public readonly struct ReadDirectoryResult
{
    public int Status { get; init; }
    public uint BytesTransferred { get; init; }

    public static ReadDirectoryResult Success(uint bytesTransferred) => new()
    {
        Status = NtStatus.Success,
        BytesTransferred = bytesTransferred,
    };

    public static ReadDirectoryResult Error(int status) => new() { Status = status };
}
