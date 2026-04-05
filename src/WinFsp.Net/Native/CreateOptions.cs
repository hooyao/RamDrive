namespace WinFsp.Native;

/// <summary>
/// NtCreateFile CreateOptions flags relevant to user-mode file systems.
/// </summary>
[Flags]
public enum CreateOptions : uint
{
    None = 0,
    FileDirectoryFile = 0x00000001,
    FileWriteThrough = 0x00000002,
    FileSequentialOnly = 0x00000004,
    FileNoIntermediateBuffering = 0x00000008,
    FileNoEaKnowledge = 0x00000200,
    FileOpenForBackupIntent = 0x00004000,
    FileDeleteOnClose = 0x00001000,
    FileOpenReparsePoint = 0x00200000,
}
