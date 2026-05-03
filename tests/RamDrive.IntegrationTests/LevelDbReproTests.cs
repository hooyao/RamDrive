using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using FluentAssertions;
using RamDrive.Core.Configuration;
using WinFsp.Native;
using Xunit.Abstractions;
using RamDriveCore = RamDrive.Core;

namespace RamDrive.IntegrationTests;

internal static partial class LevelDbWin32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3, CREATE_ALWAYS = 2, OPEN_ALWAYS = 4;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(nint hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, nint lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(nint hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, nint lpOverlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushFileBuffers(nint hFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);
    public const uint MOVEFILE_REPLACE_EXISTING = 1;

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct FILE_RENAME_INFO
    {
        // C union { BOOLEAN ReplaceIfExists; DWORD Flags; } at offset 0; 7 bytes padding to 8.
        [FieldOffset(0)] public byte ReplaceIfExists;
        [FieldOffset(8)] public nint RootDirectory;
        [FieldOffset(16)] public uint FileNameLength;
        // FileName[] starts at offset 20 (right after FileNameLength, no further padding —
        // SetFileInformationByHandle reads (BufferSize - sizeof(header)) bytes of name).
    }

    // SetFileInformationByHandle for FileRenameInfo class — this is what NTFS rename via handle uses.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileInformationByHandle(nint hFile, int FileInformationClass,
        nint lpFileInformation, uint dwBufferSize);
    public const int FileRenameInfo = 3;
}

[Collection("RamDrive")]
public class LevelDbReproTests(RamDriveFixture fx, ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(fx.Root, $"ldb_{Guid.NewGuid():N}");

    public void Dispose()
    {
        RamDriveFixture.SetTraceFilter(null);
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void DumpTrace()
    {
        output.WriteLine("=== TraceLog ===");
        lock (RamDriveFixture.TraceLog)
            foreach (var line in RamDriveFixture.TraceLog) output.WriteLine(line);
    }

    /// <summary>
    /// Exactly mirrors leveldb's chromium env SetCurrentFile sequence captured by procmon:
    ///   1. WriteFile(dbtmp, "MANIFEST-000001\n", 16 bytes) — buffered/cached
    ///   2. FlushFileBuffers(dbtmp)
    ///   3. CloseHandle(dbtmp)
    ///   4. MoveFileEx(dbtmp -> CURRENT, REPLACE_EXISTING)
    ///   5. CreateFile(CURRENT, GENERIC_READ) + ReadFile must return 16 bytes
    /// </summary>
    [Fact]
    public void Win32_LevelDb_Sequence_Cached()
    {
        Directory.CreateDirectory(_dir);
        string tmp = Path.Combine(_dir, "dbtmp");
        string cur = Path.Combine(_dir, "CURRENT");

        RamDriveFixture.SetTraceFilter("ldb_");

        // Phase 1: write dbtmp via Win32 cached I/O (default — no FILE_FLAG_WRITE_THROUGH/NO_BUFFERING)
        nint h = LevelDbWin32.CreateFile(tmp,
            LevelDbWin32.GENERIC_WRITE, LevelDbWin32.FILE_SHARE_READ,
            0, LevelDbWin32.CREATE_ALWAYS, LevelDbWin32.FILE_ATTRIBUTE_NORMAL, 0);
        if (h == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile dbtmp");

        byte[] payload = "MANIFEST-000001\n"u8.ToArray();
        if (!LevelDbWin32.WriteFile(h, payload, (uint)payload.Length, out uint written, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WriteFile");
        written.Should().Be((uint)payload.Length);

        LevelDbWin32.FlushFileBuffers(h).Should().BeTrue();
        LevelDbWin32.CloseHandle(h).Should().BeTrue();

        // Phase 2: rename dbtmp -> CURRENT
        bool moved = LevelDbWin32.MoveFileEx(tmp, cur, LevelDbWin32.MOVEFILE_REPLACE_EXISTING);
        if (!moved) throw new Win32Exception(Marshal.GetLastWin32Error(), "MoveFileEx");

        // Phase 3: open + read CURRENT — MUST see 16 bytes
        nint hr = LevelDbWin32.CreateFile(cur,
            LevelDbWin32.GENERIC_READ, LevelDbWin32.FILE_SHARE_READ,
            0, LevelDbWin32.OPEN_EXISTING, LevelDbWin32.FILE_ATTRIBUTE_NORMAL, 0);
        if (hr == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile CURRENT for read");

        byte[] readBuf = new byte[8192];
        bool ok = LevelDbWin32.ReadFile(hr, readBuf, (uint)readBuf.Length, out uint readBytes, 0);
        LevelDbWin32.CloseHandle(hr);

        try
        {
            ok.Should().BeTrue();
            readBytes.Should().Be((uint)payload.Length, "ReadFile after rename must return all 16 bytes; got {0}", readBytes);
            readBuf.AsSpan(0, (int)readBytes).ToArray().Should().Equal(payload);
        }
        catch
        {
            DumpTrace();
            throw;
        }
    }

    /// <summary>
    /// Variant: chromium leveldb uses SetFileInformationByHandle(FileRenameInfo) on Windows 10+
    /// rather than MoveFileEx — they produce different IRPs (SetInformationFile in IRP_MJ_SET_INFORMATION).
    /// </summary>
    [Fact]
    public unsafe void Win32_LevelDb_SetFileInformationByHandle_Rename()
    {
        Directory.CreateDirectory(_dir);
        string tmp = Path.Combine(_dir, "dbtmp");
        string cur = Path.Combine(_dir, "CURRENT");

        RamDriveFixture.SetTraceFilter("ldb_");

        nint h = LevelDbWin32.CreateFile(tmp,
            LevelDbWin32.GENERIC_WRITE | (1u << 16) /* DELETE */, LevelDbWin32.FILE_SHARE_READ | LevelDbWin32.FILE_SHARE_WRITE | LevelDbWin32.FILE_SHARE_DELETE,
            0, LevelDbWin32.CREATE_ALWAYS, LevelDbWin32.FILE_ATTRIBUTE_NORMAL, 0);
        if (h == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile dbtmp");

        byte[] payload = "MANIFEST-000001\n"u8.ToArray();
        LevelDbWin32.WriteFile(h, payload, (uint)payload.Length, out _, 0).Should().BeTrue();
        LevelDbWin32.FlushFileBuffers(h).Should().BeTrue();

        // Build FILE_RENAME_INFO with target full path inline
        string target = cur;
        byte[] tgtBytes = Encoding.Unicode.GetBytes(target);
        int infoSize = sizeof(LevelDbWin32.FILE_RENAME_INFO) + tgtBytes.Length + 2; // + null terminator
        nint buf = Marshal.AllocHGlobal(infoSize);
        try
        {
            new Span<byte>((void*)buf, infoSize).Clear();
            var info = (LevelDbWin32.FILE_RENAME_INFO*)buf;
            info->ReplaceIfExists = 1;
            info->RootDirectory = 0;
            info->FileNameLength = (uint)tgtBytes.Length;
            tgtBytes.CopyTo(new Span<byte>((byte*)buf + sizeof(LevelDbWin32.FILE_RENAME_INFO), tgtBytes.Length));
            bool renamed = LevelDbWin32.SetFileInformationByHandle(h, LevelDbWin32.FileRenameInfo, buf, (uint)infoSize);
            if (!renamed) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetFileInformationByHandle(FileRenameInfo)");
        }
        finally { Marshal.FreeHGlobal(buf); }

        LevelDbWin32.CloseHandle(h);

        // Read back
        nint hr = LevelDbWin32.CreateFile(cur,
            LevelDbWin32.GENERIC_READ, LevelDbWin32.FILE_SHARE_READ,
            0, LevelDbWin32.OPEN_EXISTING, LevelDbWin32.FILE_ATTRIBUTE_NORMAL, 0);
        if (hr == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile CURRENT for read");

        byte[] readBuf = new byte[8192];
        bool ok = LevelDbWin32.ReadFile(hr, readBuf, (uint)readBuf.Length, out uint readBytes, 0);
        LevelDbWin32.CloseHandle(hr);

        try
        {
            ok.Should().BeTrue();
            readBytes.Should().Be((uint)payload.Length, "Got {0} bytes (expected 16)", readBytes);
            readBuf.AsSpan(0, (int)readBytes).ToArray().Should().Equal(payload);
        }
        catch
        {
            DumpTrace();
            throw;
        }
    }

    /// <summary>
    /// Mixed-case variant: rename writes to <c>\Temp\Dbtmp</c> then renames to <c>\TEMP\CURRENT</c>.
    /// Validates the case-insensitive upper-casing inside <c>FileSystemHost.Notify</c> — without it,
    /// the kernel's cache key for the all-uppercase path won't match the notification's mixed-case
    /// path and the read returns zero bytes.
    /// </summary>
    [Fact]
    public void MixedCase_Win32_LevelDb_Sequence()
    {
        // The fixture is case-insensitive (default), so MIXEDCASE/mixedcase resolve to the same node.
        // We deliberately use different cases for write vs read paths.
        Directory.CreateDirectory(_dir);
        string tmpMixedCase = Path.Combine(_dir, "Dbtmp");
        string curUpperCase = Path.Combine(_dir, "CURRENT");
        string curLowerCase = Path.Combine(_dir, "current"); // read with completely different case

        RamDriveFixture.SetTraceFilter("ldb_");

        nint h = LevelDbWin32.CreateFile(tmpMixedCase,
            LevelDbWin32.GENERIC_WRITE, LevelDbWin32.FILE_SHARE_READ,
            0, LevelDbWin32.CREATE_ALWAYS, LevelDbWin32.FILE_ATTRIBUTE_NORMAL, 0);
        if (h == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile dbtmp");

        byte[] payload = "MANIFEST-000001\n"u8.ToArray();
        LevelDbWin32.WriteFile(h, payload, (uint)payload.Length, out _, 0).Should().BeTrue();
        LevelDbWin32.FlushFileBuffers(h).Should().BeTrue();
        LevelDbWin32.CloseHandle(h);

        LevelDbWin32.MoveFileEx(tmpMixedCase, curUpperCase, LevelDbWin32.MOVEFILE_REPLACE_EXISTING)
            .Should().BeTrue();

        nint hr = LevelDbWin32.CreateFile(curLowerCase,
            LevelDbWin32.GENERIC_READ, LevelDbWin32.FILE_SHARE_READ,
            0, LevelDbWin32.OPEN_EXISTING, LevelDbWin32.FILE_ATTRIBUTE_NORMAL, 0);
        if (hr == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile current for read");

        byte[] readBuf = new byte[8192];
        bool ok = LevelDbWin32.ReadFile(hr, readBuf, (uint)readBuf.Length, out uint readBytes, 0);
        LevelDbWin32.CloseHandle(hr);

        try
        {
            ok.Should().BeTrue();
            readBytes.Should().Be((uint)payload.Length,
                "mixed-case path must invalidate the upper-cased FSD cache key; got {0} bytes", readBytes);
            readBuf.AsSpan(0, (int)readBytes).ToArray().Should().Equal(payload);
        }
        catch
        {
            DumpTrace();
            throw;
        }
    }
}

/// <summary>
/// Extra tests with a private fixture configured at the production-default
/// <c>FileInfoTimeoutMs=1000</c> (instead of the suite-wide <c>uint.MaxValue</c>).
/// Verifies the leveldb sequence still works under the shipped configuration —
/// not just the worst-case regression-bait one.
/// </summary>
[SupportedOSPlatform("windows")]
public class LevelDbDefaultTimeoutTests : IDisposable
{
    private readonly RamDriveCore.Memory.PagePool _pool;
    private readonly RamDriveCore.FileSystem.RamFileSystem _fs;
    private readonly FileSystemHost _host;
    private readonly string _root;
    private readonly string _dir;

    public LevelDbDefaultTimeoutTests()
    {
        var options = new RamDriveOptions
        {
            CapacityMb = 64,
            PageSizeKb = 64,
            EnableKernelCache = true,
            FileInfoTimeoutMs = 1000, // production default
            VolumeLabel = "DefaultTimeoutTest",
        };
        _pool = new RamDriveCore.Memory.PagePool(
            new Microsoft.Extensions.Options.OptionsWrapper<RamDriveOptions>(options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RamDriveCore.Memory.PagePool>.Instance);
        _fs = new RamDriveCore.FileSystem.RamFileSystem(_pool);
        _host = new FileSystemHost(new TestAdapter(_fs, options))
        {
            Prefix = $@"\winfsp-tests\itest-default-{Environment.ProcessId}",
        };
        int r = _host.Mount(null);
        if (r < 0) throw new InvalidOperationException($"mount failed: 0x{r:X8}");
        _root = (_host.MountPoint ?? throw new InvalidOperationException("no mount")) + @"\";
        _dir = Path.Combine(_root, $"ldb_default_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        _host.Dispose();
        _fs.Dispose();
        _pool.Dispose();
    }

    [Fact]
    public void Notify_DefaultTimeoutAlsoWorks()
    {
        Directory.CreateDirectory(_dir);
        string tmp = Path.Combine(_dir, "dbtmp");
        string cur = Path.Combine(_dir, "CURRENT");

        nint h = LevelDbWin32.CreateFile(tmp,
            LevelDbWin32.GENERIC_WRITE, LevelDbWin32.FILE_SHARE_READ,
            0, LevelDbWin32.CREATE_ALWAYS, LevelDbWin32.FILE_ATTRIBUTE_NORMAL, 0);
        if (h == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile dbtmp");
        byte[] payload = "MANIFEST-000001\n"u8.ToArray();
        LevelDbWin32.WriteFile(h, payload, (uint)payload.Length, out _, 0).Should().BeTrue();
        LevelDbWin32.FlushFileBuffers(h).Should().BeTrue();
        LevelDbWin32.CloseHandle(h);

        LevelDbWin32.MoveFileEx(tmp, cur, LevelDbWin32.MOVEFILE_REPLACE_EXISTING).Should().BeTrue();

        nint hr = LevelDbWin32.CreateFile(cur,
            LevelDbWin32.GENERIC_READ, LevelDbWin32.FILE_SHARE_READ,
            0, LevelDbWin32.OPEN_EXISTING, LevelDbWin32.FILE_ATTRIBUTE_NORMAL, 0);
        if (hr == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile CURRENT for read");
        byte[] readBuf = new byte[8192];
        bool ok = LevelDbWin32.ReadFile(hr, readBuf, (uint)readBuf.Length, out uint readBytes, 0);
        LevelDbWin32.CloseHandle(hr);

        ok.Should().BeTrue();
        readBytes.Should().Be((uint)payload.Length);
        readBuf.AsSpan(0, (int)readBytes).ToArray().Should().Equal(payload);
    }
}
