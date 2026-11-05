// Mirrors MEMFS_FILE_NODE in winfsp/tst/memfs/memfs.cpp.

namespace RamDrive.Diagnostics.MemfsReference;

internal sealed class MemfsNode
{
    public string FileName = "";
    public uint FileAttributes;
    public uint ReparseTag;
    public ulong AllocationSize;
    public ulong FileSize;
    public ulong CreationTime;
    public ulong LastAccessTime;
    public ulong LastWriteTime;
    public ulong ChangeTime;
    public ulong IndexNumber;
    public byte[]? FileSecurity;
    public byte[]? FileData;
    public byte[]? ReparseData;
    public int RefCount;
    // LOCKSTEP with FileNode.BaseNode: the base node of a stream, set at creation.
    // A persistent reference (not a name lookup), so MetadataOf keeps routing through
    // the base after the base is unlinked — exactly as production's MetadataOwner does.
    public MemfsNode? BaseNode;
    // LOCKSTEP with FileNode lifetime: RefCount reaching zero only marks the node
    // dead; its FileData is freed in Close, and only after every Close has arrived
    // (the driver posts one Close per file object, and a live Cc section / memory
    // mapping pins its owning file object, deferring that owner's Close while other
    // handles' Closes arrive early — freeing at the first Close would drop pages a
    // still-mapped view can read).
    public bool Dead;
    public int CleanupCount;
    public int CloseCount;
}

// LOCKSTEP with the production adapter's FileHandleContext: per-handle state kept in
// FileOperationInfo.Context. The driver suppresses the Cleanup transact for unmodified
// handles (PostCleanupWhenModifiedOnly), so the open reference is released on the
// FIRST of Cleanup/Close that arrives.
internal sealed class HandleContext
{
    public MemfsNode Node = null!;
    public bool ReferenceReleased;
}
