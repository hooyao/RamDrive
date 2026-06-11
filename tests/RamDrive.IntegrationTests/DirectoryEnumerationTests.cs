using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;

namespace RamDrive.IntegrationTests;

/// <summary>
/// Regression tests for directory enumeration returning the synthetic "." and ".."
/// entries that NTFS and WinFsp's reference memfs emit for every non-root directory.
///
/// These MUST go through the raw Win32 <c>FindFirstFileW</c> API rather than .NET's
/// <see cref="Directory"/> helpers: when a directory enumerates to zero records, the
/// kernel answers the first <c>NtQueryDirectoryFile</c> with <c>STATUS_NO_SUCH_FILE</c>
/// (→ <c>ERROR_FILE_NOT_FOUND</c>). .NET and PowerShell SWALLOW that and return an empty
/// set, but libuv (Node.js / Electron — e.g. VS Code's updater) surfaces it as
/// <c>ENOENT</c>. The bug was a missing "." / ".." in <c>WinFspRamAdapter.ReadDirectory</c>,
/// invisible to every .NET-based torture/chaos test for exactly this reason.
/// </summary>
[Collection("RamDrive")]
[SupportedOSPlatform("windows")]
public class DirectoryEnumerationTests : IDisposable
{
    private readonly string _root;

    public DirectoryEnumerationTests(RamDriveFixture fx)
    {
        _root = Path.Combine(fx.Root, $"direnum_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void EmptyDirectory_RawEnumeration_SucceedsWithDotEntries()
    {
        // Mirrors VS Code's updater: mkdir(cachePath) immediately followed by readdir(cachePath).
        var empty = Path.Combine(_root, "vscode-stable-system-x64");
        Directory.CreateDirectory(empty);

        var entries = EnumerateRaw(empty); // throws Win32Exception(ERROR_FILE_NOT_FOUND) before the fix

        entries.Should().Contain(".");
        entries.Should().Contain("..");
    }

    [Fact]
    public void EmptyDirectory_FindFirstFile_DoesNotReturnFileNotFound()
    {
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        nint handle = FindFirstFileW(Path.Combine(empty, "*"), out _);
        int lastError = Marshal.GetLastWin32Error();
        if (handle != INVALID_HANDLE_VALUE) FindClose(handle);

        handle.Should().NotBe(INVALID_HANDLE_VALUE,
            $"FindFirstFile on an empty directory must not fail (Win32 error {lastError}); " +
            "a zero-record enumeration becomes ENOENT under libuv/Node.");
        lastError.Should().NotBe(ERROR_FILE_NOT_FOUND);
    }

    [Fact]
    public void NonEmptyDirectory_RawEnumeration_IncludesDotDotAndChild()
    {
        var dir = Path.Combine(_root, "withchild");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "f.txt"), "x");

        var entries = EnumerateRaw(dir);

        entries.Should().Contain(".");
        entries.Should().Contain("..");
        entries.Should().Contain("f.txt");
    }

    [Fact]
    public void NewlyCreatedSubdir_DotEntriesAreDirectories()
    {
        var empty = Path.Combine(_root, "attrs");
        Directory.CreateDirectory(empty);

        var attrs = EnumerateRawWithAttributes(empty);

        attrs.Should().ContainKey(".");
        attrs.Should().ContainKey("..");
        ((FileAttributes)attrs["."] & FileAttributes.Directory).Should().Be(FileAttributes.Directory);
        ((FileAttributes)attrs[".."] & FileAttributes.Directory).Should().Be(FileAttributes.Directory);
    }

    // ── raw Win32 enumeration ────────────────────────────────────────────────

    private static List<string> EnumerateRaw(string dir)
        => EnumerateRawWithAttributes(dir).Keys.ToList();

    private static Dictionary<string, uint> EnumerateRawWithAttributes(string dir)
    {
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        nint handle = FindFirstFileW(Path.Combine(dir, "*"), out var data);
        if (handle == INVALID_HANDLE_VALUE)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"FindFirstFile failed for '{dir}'");
        try
        {
            do { result[data.cFileName] = data.dwFileAttributes; }
            while (FindNextFileW(handle, out data));

            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_NO_MORE_FILES)
                throw new Win32Exception(err, $"FindNextFile failed for '{dir}'");
        }
        finally { FindClose(handle); }
        return result;
    }

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_NO_MORE_FILES = 18;
    private static readonly nint INVALID_HANDLE_VALUE = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindFirstFileW(string lpFileName, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(nint hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint hFindFile);
}
