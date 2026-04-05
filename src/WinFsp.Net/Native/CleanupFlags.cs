namespace WinFsp.Native;

/// <summary>
/// Flags passed to the Cleanup callback indicating what actions to perform.
/// </summary>
[Flags]
public enum CleanupFlags : uint
{
    None = 0,
    Delete = 0x01,
    SetAllocationSize = 0x02,
    SetArchiveBit = 0x10,
    SetLastAccessTime = 0x20,
    SetLastWriteTime = 0x40,
    SetChangeTime = 0x80,
}
