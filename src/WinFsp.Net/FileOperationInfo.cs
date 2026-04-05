using WinFsp.Native;

namespace WinFsp;

/// <summary>
/// Per-handle state passed to every file system operation. Analogous to Dokan's DokanFileInfo.
///
/// The framework creates this when a file is opened (Create/Open) and passes it to all
/// subsequent operations on that handle until Close.
///
/// <para><b>Context property:</b> Store anything you need here — a FileNode, a Stream,
/// a database handle, or any object. The framework never inspects it. You set it in
/// CreateFile/OpenFile, use it in Read/Write/etc., and clean it up in Close.</para>
///
/// <para><b>Cancellation:</b> The <see cref="CancellationToken"/> is automatically cancelled
/// when the handle's Cleanup is called (last CloseHandle). Use it in async operations to
/// abort pending work when the user cancels or the handle is closed.</para>
/// </summary>
public sealed class FileOperationInfo
{
    private CancellationTokenSource? _cts;

    /// <summary>
    /// User-defined context object. Set this in CreateFile to associate state with the open handle.
    /// The framework passes this same instance to all subsequent operations (Read, Write, Close, etc.).
    /// The framework never reads or modifies this value.
    /// </summary>
    public object? Context { get; set; }

    /// <summary>Whether this handle refers to a directory.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>The process ID of the calling process.</summary>
    public uint ProcessId { get; internal set; }

    /// <summary>
    /// Cancellation token for this handle. Automatically cancelled on Cleanup (last handle close).
    /// Pass this to async operations so they abort when the handle is closed or the user cancels.
    /// </summary>
    public CancellationToken CancellationToken => (_cts ??= new CancellationTokenSource()).Token;

    /// <summary>
    /// Cancel all pending async operations on this handle.
    /// Called by the framework during Cleanup, before invoking the user's Cleanup method.
    /// </summary>
    internal void CancelPendingOperations()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Dispose the CancellationTokenSource. Called by the framework during Close.
    /// </summary>
    internal void DisposeResources()
    {
        _cts?.Dispose();
        _cts = null;
    }
}
