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
}
