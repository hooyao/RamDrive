using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;

namespace RamDrive.IntegrationTests;

internal static partial class FileIdWin32
{
    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandle(nint hFile, out BY_HANDLE_FILE_INFORMATION info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);
}

/// <summary>
/// Verifies the <c>file-id-uniqueness</c> capability at the mounted-FS layer. The motivating bug:
/// every file reported <c>(VolumeSerialNumber, file id) = (0, 0)</c>, so <c>std::filesystem::copy_file</c>
/// (and Win32 same-volume copy fast paths) treated any two distinct files as the same file and
/// rejected a copy with <c>file_exists</c> — the dotTrace ETW-collector deploy failure
/// (<c>copy: file exists ... jetbrainsproc_&lt;GUID&gt; (generic:17)</c>). Written from the real-NTFS
/// oracle: distinct files have distinct, non-zero file ids, and a same-volume copy into a fresh
/// directory succeeds.
/// </summary>
[Collection("RamDrive")]
[SupportedOSPlatform("windows")]
public class FileIdUniquenessTests(RamDriveFixture fx) : IDisposable
{
    private readonly string _dir = Path.Combine(fx.Root, $"fileid_{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static (uint vol, ulong id) FileId(string path)
    {
        nint h = FileIdWin32.CreateFile(path, FileIdWin32.FILE_READ_ATTRIBUTES,
            FileIdWin32.FILE_SHARE_READ | FileIdWin32.FILE_SHARE_WRITE | FileIdWin32.FILE_SHARE_DELETE,
            0, FileIdWin32.OPEN_EXISTING, FileIdWin32.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), $"open {path}");
        try
        {
            if (!FileIdWin32.GetFileInformationByHandle(h, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetFileInformationByHandle {path}");
            ulong id = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return (info.VolumeSerialNumber, id);
        }
        finally { FileIdWin32.CloseHandle(h); }
    }

    [Fact]
    public void DistinctFiles_HaveDistinctNonZeroFileIds()
    {
        Directory.CreateDirectory(_dir);
        string a = Path.Combine(_dir, "a.bin");
        string b = Path.Combine(_dir, "b.bin");
        File.WriteAllText(a, "aaa");
        File.WriteAllText(b, "bbb");

        var (volA, idA) = FileId(a);
        var (volB, idB) = FileId(b);

        // The file id is the discriminator std::filesystem::copy_file uses to decide whether two
        // paths are the same file. It MUST be non-zero and unique per file. (The volume serial that
        // GetFileInformationByHandle reports depends on the mount type — Mount Manager drive-letter
        // mounts surface VolumeParams.VolumeSerialNumber, UNC-prefix mounts may report 0 — so we
        // only require that both files on the same mount agree on it, not that it is non-zero.)
        idA.Should().NotBe(0u, "a zero file id makes distinct files look identical to copy_file");
        idB.Should().NotBe(0u);
        idA.Should().NotBe(idB, "two distinct files must report distinct file ids");
        volA.Should().Be(volB, "files on the same volume must share the volume serial");
    }

    [Fact]
    public void SameVolumeCopy_OfFileIntoFreshDirectory_Succeeds()
    {
        // This is the exact dotTrace deploy step: a source file is copied into a freshly-created
        // directory on the SAME volume. Pre-fix this failed because the (0,0) file ids made the
        // copy think source == destination.
        Directory.CreateDirectory(_dir);
        string src = Path.Combine(_dir, "collector.bin");
        File.WriteAllBytes(src, new byte[] { 1, 2, 3, 4 });

        string destDir = Path.Combine(_dir, $"jbproc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(destDir);
        string dest = Path.Combine(destDir, "collector.bin");

        var act = () => File.Copy(src, dest, overwrite: false);
        act.Should().NotThrow("a same-volume copy of distinct files must not be rejected as file_exists");

        File.Exists(dest).Should().BeTrue();
        File.ReadAllBytes(dest).Should().Equal(1, 2, 3, 4);
    }
}
