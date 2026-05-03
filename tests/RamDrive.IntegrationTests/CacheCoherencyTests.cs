using System.ComponentModel;
using System.Runtime.InteropServices;
using FluentAssertions;

namespace RamDrive.IntegrationTests;

internal static partial class Win32
{
    [LibraryImport("kernel32.dll", EntryPoint = "ReplaceFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReplaceFile(
        string lpReplacedFileName, string lpReplacementFileName,
        string? lpBackupFileName, uint dwReplaceFlags, nint lpExclude, nint lpReserved);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

    public const uint MOVEFILE_REPLACE_EXISTING = 0x1;
    public const uint MOVEFILE_WRITE_THROUGH = 0x8;
}

/// <summary>
/// Cache coherency tests — exercise the LevelDB / SQLite atomic-rename pattern that
/// breaks when WinFsp's kernel FileInfo cache (FileInfoTimeout=MAX) is not
/// invalidated after a MoveFile that replaces an existing target.
///
/// Reproduces the Chromium / leveldb "CURRENT file does not end with newline" crash
/// observed when mounting a Chromium user-data-dir on the RamDrive.
/// </summary>
[Collection("RamDrive")]
public class CacheCoherencyTests(RamDriveFixture fx) : IDisposable
{
    private readonly string _dir = Path.Combine(fx.Root, $"cc_{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>
    /// Exact LevelDB CURRENT-file pattern:
    ///   1. target exists as a 0-byte file (forces kernel to cache size=0)
    ///   2. write tmp with new content
    ///   3. MoveFileEx(tmp -> target, REPLACE_EXISTING)
    ///   4. open target and read — must see the NEW content, not stale 0 bytes
    /// </summary>
    [Fact]
    public void Rename_Replace_Then_Read_Sees_New_Content()
    {
        Directory.CreateDirectory(_dir);
        string target = Path.Combine(_dir, "CURRENT");
        string tmp = Path.Combine(_dir, "dbtmp");

        // 1. Create empty target and prime the kernel's FileInfo cache (size=0)
        File.WriteAllBytes(target, []);
        File.ReadAllBytes(target).Should().BeEmpty();

        // 2. Write new content to tmp
        byte[] expected = "MANIFEST-000001\n"u8.ToArray();
        File.WriteAllBytes(tmp, expected);

        // 3. Atomic rename, replacing the cached-empty target
        File.Move(tmp, target, overwrite: true);

        // 4. Read target — kernel cache must NOT serve stale size=0
        byte[] actual = File.ReadAllBytes(target);
        actual.Should().Equal(expected,
            "MoveFile that replaces an existing target must invalidate the kernel FileInfo cache");
    }

    /// <summary>
    /// Tighter loop — many rename-replace cycles, each immediately read back.
    /// Mirrors how Chromium / leveldb churns CURRENT during DB open + manifest rotation.
    /// </summary>
    [Fact]
    public void Rename_Replace_Loop_Always_Reads_Latest()
    {
        Directory.CreateDirectory(_dir);
        string target = Path.Combine(_dir, "CURRENT");
        File.WriteAllBytes(target, []); // prime size=0 cache

        for (int i = 1; i <= 100; i++)
        {
            string tmp = Path.Combine(_dir, $"tmp_{i}");
            byte[] payload = System.Text.Encoding.ASCII.GetBytes($"MANIFEST-{i:D6}\n");
            File.WriteAllBytes(tmp, payload);
            File.Move(tmp, target, overwrite: true);

            byte[] read = File.ReadAllBytes(target);
            read.Should().Equal(payload, $"iteration {i}: rename-then-read must see new content");
        }
    }

    /// <summary>
    /// Variant: target initially has some data, replaced by a file of DIFFERENT size.
    /// Catches caches keyed on (size, mtime) rather than just size.
    /// </summary>
    [Fact]
    public void Rename_Replace_Different_Size_Then_Read()
    {
        Directory.CreateDirectory(_dir);
        string target = Path.Combine(_dir, "CURRENT");
        string tmp = Path.Combine(_dir, "dbtmp");

        File.WriteAllBytes(target, new byte[64]);          // 64-byte stale target
        File.ReadAllBytes(target).Length.Should().Be(64);   // prime cache

        byte[] expected = new byte[16];
        for (int i = 0; i < expected.Length; i++) expected[i] = (byte)(i + 1);
        File.WriteAllBytes(tmp, expected);
        File.Move(tmp, target, overwrite: true);

        byte[] actual = File.ReadAllBytes(target);
        actual.Should().Equal(expected);
    }

    /// <summary>
    /// Verifies that the file size reported by GetFileInformation (used for
    /// FileStream.Length and ReadAllBytes' buffer sizing) matches the new content
    /// immediately after the rename.
    /// </summary>
    [Fact]
    public void Rename_Replace_FileSize_Matches_New_Content()
    {
        Directory.CreateDirectory(_dir);
        string target = Path.Combine(_dir, "CURRENT");
        string tmp = Path.Combine(_dir, "dbtmp");

        File.WriteAllBytes(target, []);                     // 0-byte stale
        new FileInfo(target).Length.Should().Be(0);          // prime cache

        byte[] expected = new byte[16];
        File.WriteAllBytes(tmp, expected);
        File.Move(tmp, target, overwrite: true);

        new FileInfo(target).Length.Should().Be(16,
            "file size queried after rename-replace must reflect the new node");
    }

    /// <summary>
    /// Chromium's leveldb port uses ReplaceFileW (not MoveFileEx) on Windows.
    /// Semantics differ: ReplaceFileW preserves the destination's metadata streams
    /// and ACLs, internally swapping the file content via a different code path.
    /// This is the EXACT call that produces the "CURRENT does not end with newline"
    /// crash when launching Chromium with --user-data-dir on this RAM drive.
    /// </summary>
    [Fact]
    public void ReplaceFileW_LevelDB_Pattern_Then_Read()
    {
        Directory.CreateDirectory(_dir);
        string target = Path.Combine(_dir, "CURRENT");
        string tmp = Path.Combine(_dir, "CURRENT.tmp");

        File.WriteAllBytes(target, []);
        File.ReadAllBytes(target).Should().BeEmpty(); // prime kernel cache size=0

        byte[] expected = "MANIFEST-000001\n"u8.ToArray();
        File.WriteAllBytes(tmp, expected);

        bool ok = Win32.ReplaceFile(target, tmp, null, 0, 0, 0);
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), $"ReplaceFile failed");

        byte[] actual = File.ReadAllBytes(target);
        actual.Should().Equal(expected,
            "ReplaceFileW must invalidate the kernel FileInfo cache for the destination");
    }

    /// <summary>
    /// LevelDB on a fresh DB: CURRENT does not exist yet. The sequence is
    /// SetCurrentFile → WriteStringToFile(dbtmp) → RenameFile(dbtmp, CURRENT).
    /// The kernel may have negative-cached "CURRENT does not exist" from an
    /// earlier probe (leveldb does check whether CURRENT exists before recovery).
    /// </summary>
    [Fact]
    public void Rename_To_NonExistent_Target_After_Negative_Probe()
    {
        Directory.CreateDirectory(_dir);
        string target = Path.Combine(_dir, "CURRENT");
        string tmp = Path.Combine(_dir, "dbtmp");

        // Negative probe — leveldb does this via ::GetFileAttributesEx
        File.Exists(target).Should().BeFalse();

        byte[] expected = "MANIFEST-000001\n"u8.ToArray();
        File.WriteAllBytes(tmp, expected);

        bool ok = Win32.MoveFileEx(tmp, target, Win32.MOVEFILE_REPLACE_EXISTING);
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "MoveFileEx failed");

        // Open and read — buffered, sequential, exactly like leveldb's SequentialFile
        using var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Length.Should().Be(expected.Length, "size after rename to never-cached target must reflect new node");
        var buf = new byte[expected.Length];
        int n = fs.Read(buf, 0, buf.Length);
        n.Should().Be(expected.Length);
        buf.Should().Equal(expected);
    }

    /// <summary>
    /// Use GetFileAttributesEx (BasicInfo) — distinct kernel cache class from
    /// FileStandardInfo. Prime with empty target, rename-replace, then query size
    /// via attributes. Catches WinFsp's per-info-class cache divergence.
    /// </summary>
    [Fact]
    public void Rename_Replace_Then_GetFileAttributesEx_Size()
    {
        Directory.CreateDirectory(_dir);
        string target = Path.Combine(_dir, "CURRENT");
        string tmp = Path.Combine(_dir, "dbtmp");

        File.WriteAllBytes(target, []);
        new FileInfo(target).Length.Should().Be(0); // prime

        byte[] expected = new byte[16];
        File.WriteAllBytes(tmp, expected);
        Win32.MoveFileEx(tmp, target, Win32.MOVEFILE_REPLACE_EXISTING).Should().BeTrue();

        // Force a fresh attributes query via a NEW FileInfo instance
        var fi = new FileInfo(target);
        fi.Refresh();
        fi.Length.Should().Be(16);
    }
}
