using System.Runtime.Versioning;
using WinFsp.Native;

namespace WinFsp;

/// <summary>
/// File system operations result — NTSTATUS code paired with optional output.
/// </summary>
public readonly struct FsResult
{
    public int Status { get; init; }
    public FspFileInfo FileInfo { get; init; }

    public static FsResult Success(FspFileInfo fileInfo = default) => new()
        { Status = NtStatus.Success, FileInfo = fileInfo };
    public static FsResult Error(int status) => new() { Status = status };

    public static implicit operator FsResult(int status) => new() { Status = status };
}

/// <summary>
/// User-mode file system operations interface. Implement this to build a file system.
///
/// <para>This is a thin translation layer between the OS kernel and your code — like an HTTP controller.
/// It does NOT dictate how you design your file system internals. Store whatever you need in
/// <see cref="FileOperationInfo.Context"/> and cast it back in subsequent calls.</para>
///
/// <para><b>All I/O methods return <see cref="ValueTask{T}"/>.</b> For in-memory or local file systems,
/// return synchronously completed ValueTask (zero-allocation). For network/cloud file systems
/// use async/await naturally. The framework handles STATUS_PENDING and buffer management.</para>
///
/// <para><b>Cancellation:</b> Every async method receives a <see cref="CancellationToken"/> that is
/// automatically cancelled when the handle's Cleanup occurs (user closes the file). Honor it.</para>
///
/// <para><b>Example (RAM disk):</b></para>
/// <code>
/// class RamDriveAdapter : IFileSystem
/// {
///     private readonly RamFileSystem _fs = new();
///
///     public ValueTask&lt;CreateResult&gt; CreateFile(string fileName, ..., FileOperationInfo info, CancellationToken ct)
///     {
///         var node = _fs.CreateFile(fileName);
///         info.Context = node;
///         return new(new CreateResult(NtStatus.Success, MakeFileInfo(node)));  // sync completion
///     }
///
///     public ValueTask&lt;ReadResult&gt; ReadFile(string fileName, Memory&lt;byte&gt; buffer, ulong offset,
///         FileOperationInfo info, CancellationToken ct)
///     {
///         var node = (FileNode)info.Context!;
///         uint bytesRead = (uint)node.Content.Read((long)offset, buffer.Span);
///         return new(ReadResult.Success(bytesRead));  // sync, zero-alloc
///     }
/// }
/// </code>
///
/// <para><b>Example (network FS):</b></para>
/// <code>
/// class CloudFs : IFileSystem
/// {
///     public async ValueTask&lt;ReadResult&gt; ReadFile(string fileName, Memory&lt;byte&gt; buffer, ulong offset,
///         FileOperationInfo info, CancellationToken ct)
///     {
///         int n = await _httpClient.DownloadRangeAsync(url, offset, buffer, ct);
///         return ReadResult.Success((uint)n);  // truly async
///     }
/// }
/// </code>
/// </summary>
[SupportedOSPlatform("windows")]
public interface IFileSystem
{
    // ═══════════════════════════════════════════
    //  Lifecycle (sync — called once, not on hot path)
    // ═══════════════════════════════════════════

    /// <summary>Called just before mount. Configure host properties here.</summary>
    int Init(FileSystemHost host) => NtStatus.Success;

    /// <summary>Called after mount, before any I/O operations.</summary>
    int Mounted(FileSystemHost host) => NtStatus.Success;

    /// <summary>Called after unmount. No more operations will arrive.</summary>
    void Unmounted(FileSystemHost host) { }

    /// <summary>Called when the I/O dispatcher stops.</summary>
    void DispatcherStopped(bool normally) { }

    // ═══════════════════════════════════════════
    //  Volume (sync — fast, no I/O)
    // ═══════════════════════════════════════════

    /// <summary>Get volume total/free size and label.</summary>
    int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel);

    /// <summary>Set volume label.</summary>
    int SetVolumeLabel(string volumeLabel, out ulong totalSize, out ulong freeSize)
    {
        totalSize = 0; freeSize = 0; return NtStatus.InvalidDeviceRequest;
    }

    // ═══════════════════════════════════════════
    //  Name lookup (sync — called before Create/Open for access checks)
    // ═══════════════════════════════════════════

    /// <summary>
    /// Check if a path exists and return its attributes. MUST implement.
    /// Called before every Create/Open for access checks.
    /// </summary>
    int GetFileSecurityByName(string fileName, out uint fileAttributes, ref byte[]? securityDescriptor);

    // ═══════════════════════════════════════════
    //  Create / Open / Overwrite
    // ═══════════════════════════════════════════

    /// <summary>
    /// Create a new file or directory.
    /// Set info.Context and info.IsDirectory for use in subsequent calls.
    /// </summary>
    ValueTask<CreateResult> CreateFile(
        string fileName, uint createOptions, uint grantedAccess,
        uint fileAttributes, byte[]? securityDescriptor, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct);

    /// <summary>Open an existing file or directory. Set info.Context.</summary>
    ValueTask<CreateResult> OpenFile(
        string fileName, uint createOptions, uint grantedAccess,
        FileOperationInfo info, CancellationToken ct);

    /// <summary>Overwrite (truncate) an existing open file.</summary>
    ValueTask<FsResult> OverwriteFile(
        uint fileAttributes, bool replaceFileAttributes, ulong allocationSize,
        FileOperationInfo info, CancellationToken ct)
        => new(FsResult.Error(NtStatus.InvalidDeviceRequest));

    // ═══════════════════════════════════════════
    //  Read / Write / Flush
    // ═══════════════════════════════════════════

    /// <summary>
    /// Read file data into the buffer.
    /// For sync FS: use buffer.Span directly and return completed ValueTask.
    /// For async FS: await your I/O, write into buffer, return result.
    /// </summary>
    ValueTask<ReadResult> ReadFile(
        string fileName, Memory<byte> buffer, ulong offset,
        FileOperationInfo info, CancellationToken ct);

    /// <summary>
    /// Write file data from the buffer.
    /// </summary>
    /// <param name="writeToEndOfFile">If true, append to end (ignore offset).</param>
    /// <param name="constrainedIo">If true, do not extend file size (paging I/O).</param>
    ValueTask<WriteResult> WriteFile(
        string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
        bool writeToEndOfFile, bool constrainedIo,
        FileOperationInfo info, CancellationToken ct);

    /// <summary>Flush file or volume data (null fileName = volume flush).</summary>
    ValueTask<FsResult> FlushFileBuffers(
        string? fileName, FileOperationInfo info, CancellationToken ct)
        => new(FsResult.Success());

    // ═══════════════════════════════════════════
    //  Metadata
    // ═══════════════════════════════════════════

    /// <summary>Get file/directory metadata.</summary>
    ValueTask<FsResult> GetFileInformation(
        string fileName, FileOperationInfo info, CancellationToken ct);

    /// <summary>Set attributes/timestamps. 0 = don't change. unchecked((uint)-1) attributes = don't change.</summary>
    ValueTask<FsResult> SetFileAttributes(
        string fileName,
        uint fileAttributes, ulong creationTime, ulong lastAccessTime,
        ulong lastWriteTime, ulong changeTime,
        FileOperationInfo info, CancellationToken ct)
        => new(FsResult.Error(NtStatus.InvalidDeviceRequest));

    /// <summary>Set file size or allocation size.</summary>
    ValueTask<FsResult> SetFileSize(
        string fileName, ulong newSize, bool setAllocationSize,
        FileOperationInfo info, CancellationToken ct)
        => new(FsResult.Error(NtStatus.InvalidDeviceRequest));

    // ═══════════════════════════════════════════
    //  Delete / Move
    // ═══════════════════════════════════════════

    /// <summary>Check if this file/directory can be deleted.</summary>
    ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct);

    /// <summary>Rename or move a file/directory.</summary>
    ValueTask<int> MoveFile(
        string fileName, string newFileName, bool replaceIfExists,
        FileOperationInfo info, CancellationToken ct)
        => new(NtStatus.InvalidDeviceRequest);

    // ═══════════════════════════════════════════
    //  Lifecycle (sync — cannot fail, no async needed)
    // ═══════════════════════════════════════════

    /// <summary>Last handle closed. Perform delete if flagged. Cannot report errors.</summary>
    void Cleanup(string? fileName, FileOperationInfo info, CleanupFlags flags) { }

    /// <summary>All references released. Clean up info.Context here.</summary>
    void Close(FileOperationInfo info) { }

    // ═══════════════════════════════════════════
    //  Directory
    // ═══════════════════════════════════════════

    /// <summary>Enumerate directory entries into native buffer via FspApi helpers.</summary>
    ValueTask<ReadDirectoryResult> ReadDirectory(
        string fileName, string? pattern, string? marker,
        nint buffer, uint length,
        FileOperationInfo info, CancellationToken ct);

    // ═══════════════════════════════════════════
    //  Security
    // ═══════════════════════════════════════════

    int GetFileSecurity(string fileName, ref byte[]? securityDescriptor, FileOperationInfo info)
        => NtStatus.InvalidDeviceRequest;

    int SetFileSecurity(string fileName, uint securityInformation, byte[] modificationDescriptor, FileOperationInfo info)
        => NtStatus.InvalidDeviceRequest;

    // ═══════════════════════════════════════════
    //  Reparse / Streams / EA / IOCTL
    // ═══════════════════════════════════════════

    int GetReparsePoint(string fileName, ref byte[]? reparseData, FileOperationInfo info)
        => NtStatus.InvalidDeviceRequest;
    int SetReparsePoint(string fileName, byte[] reparseData, FileOperationInfo info)
        => NtStatus.InvalidDeviceRequest;
    int DeleteReparsePoint(string fileName, byte[] reparseData, FileOperationInfo info)
        => NtStatus.InvalidDeviceRequest;

    int GetStreamInfo(string fileName, nint buffer, uint length, out uint bytesTransferred, FileOperationInfo info)
    { bytesTransferred = 0; return NtStatus.InvalidDeviceRequest; }

    int GetEa(string fileName, nint ea, uint eaLength, out uint bytesTransferred, FileOperationInfo info)
    { bytesTransferred = 0; return NtStatus.InvalidDeviceRequest; }
    int SetEa(string fileName, nint ea, uint eaLength, out FspFileInfo fileInfo, FileOperationInfo info)
    { fileInfo = default; return NtStatus.InvalidDeviceRequest; }

    /// <summary>Custom DeviceIoControl. Requires DeviceControl=true on host.</summary>
    int DeviceControl(string fileName, uint controlCode,
        ReadOnlySpan<byte> input, Span<byte> output, out uint bytesTransferred, FileOperationInfo info)
    { bytesTransferred = 0; return NtStatus.InvalidDeviceRequest; }

    int GetDirInfoByName(string dirName, string entryName, out FspDirInfo dirInfo, FileOperationInfo info)
    { dirInfo = default; return NtStatus.InvalidDeviceRequest; }

    // ═══════════════════════════════════════════
    //  Exception handler
    // ═══════════════════════════════════════════

    /// <summary>Called on unhandled callback exceptions.</summary>
    int ExceptionHandler(Exception ex) => NtStatus.UnexpectedIoError;
}

/// <summary>Result of CreateFile / OpenFile.</summary>
public readonly struct CreateResult
{
    public int Status { get; init; }
    public FspFileInfo FileInfo { get; init; }
    public string? NormalizedName { get; init; }

    public CreateResult(int status, FspFileInfo fileInfo = default, string? normalizedName = null)
    {
        Status = status;
        FileInfo = fileInfo;
        NormalizedName = normalizedName;
    }

    public static CreateResult Error(int status) => new(status);
}
