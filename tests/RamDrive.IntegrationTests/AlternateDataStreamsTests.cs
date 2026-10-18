// Integration tests for NTFS Alternate Data Streams (named streams / ADS).
//
// RamDrive models each named stream as its own FileNode keyed by the full path
// including ':' (e.g. "\dir\file.txt:stream1"), mirroring WinFsp memfs. A stream is
// attached data: it never appears in directory enumeration, it follows its base object
// on rename, and it is deleted together with it. Streams may be attached to files,
// directories and the volume root (NTFS semantics).
//
// See docs/ads-support-plan.md for the design and WinFspRamAdapter / RamFileSystem
// for the implementation.
//
// NOTE: these tests mount a real WinFsp volume and must be executed on the isolated
// test VM — never on the development workstation. See docs/testing-on-vm.md.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using Xunit;

namespace RamDrive.IntegrationTests;

[Collection("RamDrive")]
[SupportedOSPlatform("windows")]
public sealed class AlternateDataStreamsTests : IDisposable
{
    private const int ErrorHandleEof = 38;

    private readonly string _volumeRoot;
    private readonly string _dir;

    public AlternateDataStreamsTests(RamDriveFixture fx)
    {
        _volumeRoot = fx.Root;                       // "\winfsp-tests\<name>\"
        _dir = Path.Combine(_volumeRoot, $"ads_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    // ─── Win32 FindFirstStreamW / FindNextStreamW ────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_STREAM_DATA
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string cStreamName;                  // MAX_PATH + 36
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(
        string fileName, int infoLevel, out WIN32_FIND_STREAM_DATA data, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextStreamW(IntPtr handle, out WIN32_FIND_STREAM_DATA data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr handle);

    /// <summary>
    /// Win32 stream enumeration. Returned names carry the explicit data-type suffix —
    /// the main stream is <c>"::$DATA"</c> and a named stream <c>"s1"</c> is
    /// <c>":s1:$DATA"</c>.
    /// </summary>
    private static List<(string Name, long Size)> EnumStreams(string path)
    {
        var found = new List<(string, long)>();
        IntPtr h = FindFirstStreamW(path, 0, out var data, 0);
        if (h == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorHandleEof) return found;   // no streams at all
            throw new Win32Exception(err, $"FindFirstStreamW('{path}')");
        }
        try
        {
            found.Add((data.cStreamName, data.StreamSize));
            while (FindNextStreamW(h, out data)) found.Add((data.cStreamName, data.StreamSize));
        }
        finally { FindClose(h); }
        return found;
    }

    // ─── Basic create / read / write ─────────────────────────────────────────

    [Fact]
    public void Write_and_read_a_named_stream_leaves_the_main_stream_intact()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "MAIN");

        File.WriteAllText(file + ":stream1", "HELLO-STREAM");

        File.ReadAllText(file + ":stream1").Should().Be("HELLO-STREAM");
        File.ReadAllText(file).Should().Be("MAIN");
    }

    [Fact]
    public void A_named_stream_reports_its_own_size()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "1234567890");
        File.WriteAllText(file + ":s", "abc");

        new FileInfo(file).Length.Should().Be(10);
        new FileInfo(file + ":s").Length.Should().Be(3);
    }

    [Fact]
    public void Creating_a_stream_on_a_missing_base_fails()
    {
        string orphan = Path.Combine(_dir, "does-not-exist.txt") + ":s";
        Action act = () => File.WriteAllText(orphan, "x");
        act.Should().Throw<IOException>();
    }

    // ─── Enumeration ─────────────────────────────────────────────────────────

    [Fact]
    public void Named_streams_are_hidden_from_directory_enumeration()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "MAIN");
        File.WriteAllText(file + ":s1", "x");
        File.WriteAllText(file + ":s2", "y");

        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "a.txt" });
        Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName)
            .Should().BeEquivalentTo(new[] { "a.txt" });
    }

    [Fact]
    public void FindFirstStream_enumerates_main_and_named_streams()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "MAIN");
        File.WriteAllText(file + ":s1", "AAAA");
        File.WriteAllText(file + ":s2", "BB");

        var streams = EnumStreams(file);

        streams.Should().Contain(("::$DATA", 4L));
        streams.Should().Contain((":s1:$DATA", 4L));
        streams.Should().Contain((":s2:$DATA", 2L));
        streams.Should().HaveCount(3);
    }

    [Fact]
    public void FindFirstStream_on_a_plain_file_reports_only_the_main_stream()
    {
        // This is exactly what ReplaceFileW needs when NamedStreams is enabled: a
        // successful (non-empty) stream enumeration. A failure here resurfaces as
        // "ReplaceFile failed" (see CacheCoherencyTests.ReplaceFileW_LevelDB_Pattern_Then_Read).
        string file = Path.Combine(_dir, "plain.txt");
        File.WriteAllText(file, "DATA");

        EnumStreams(file).Should().ContainSingle().Which.Should().Be(("::$DATA", 4L));
    }

    // ─── Lifecycle: delete / move ────────────────────────────────────────────

    [Fact]
    public void Deleting_one_stream_keeps_the_base_and_the_others()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "MAIN");
        File.WriteAllText(file + ":s1", "one");
        File.WriteAllText(file + ":s2", "two");

        File.Delete(file + ":s1");

        File.Exists(file + ":s1").Should().BeFalse();
        File.Exists(file + ":s2").Should().BeTrue();
        File.ReadAllText(file + ":s2").Should().Be("two");
        File.ReadAllText(file).Should().Be("MAIN");
    }

    [Fact]
    public void Deleting_the_base_removes_all_its_streams()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "MAIN");
        File.WriteAllText(file + ":s1", "one");
        File.WriteAllText(file + ":s2", "two");

        File.Delete(file);

        File.Exists(file).Should().BeFalse();
        File.Exists(file + ":s1").Should().BeFalse();
        File.Exists(file + ":s2").Should().BeFalse();
    }

    [Fact]
    public void Moving_the_base_moves_its_streams()
    {
        string file = Path.Combine(_dir, "a.txt");
        string moved = Path.Combine(_dir, "b.txt");
        File.WriteAllText(file, "MAIN");
        File.WriteAllText(file + ":meta", "META");

        File.Move(file, moved);

        File.ReadAllText(moved).Should().Be("MAIN");
        File.ReadAllText(moved + ":meta").Should().Be("META");
        File.Exists(file + ":meta").Should().BeFalse();
    }

    [Fact]
    public void Replacing_the_base_discards_the_destination_streams()
    {
        string dst = Path.Combine(_dir, "dst.txt");
        string src = Path.Combine(_dir, "src.txt");
        File.WriteAllText(dst, "OLD");
        File.WriteAllText(dst + ":meta", "OLD-META");
        File.WriteAllText(src, "NEW");

        File.Move(src, dst, overwrite: true);

        File.ReadAllText(dst).Should().Be("NEW");
        File.Exists(dst + ":meta").Should().BeFalse("the destination's streams are replaced with the source object");
    }

    // ─── Streams on directories and the volume root ──────────────────────────

    [Fact]
    public void A_directory_can_carry_named_streams()
    {
        string dir = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(dir);

        File.WriteAllText(dir + ":meta", "DIR-META");

        File.ReadAllText(dir + ":meta").Should().Be("DIR-META");
        Directory.Exists(dir).Should().BeTrue();
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "sub" });
    }

    [Fact]
    public void Deleting_a_directory_removes_its_streams()
    {
        string dir = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(dir);
        File.WriteAllText(dir + ":meta", "DIR-META");

        Directory.Delete(dir);

        Directory.Exists(dir).Should().BeFalse();
        File.Exists(dir + ":meta").Should().BeFalse();
    }

    [Fact]
    public void The_volume_root_can_carry_named_streams()
    {
        string rootStream = _volumeRoot + ":ramdrive-root-ads";
        try
        {
            File.WriteAllText(rootStream, "ROOT");

            File.ReadAllText(rootStream).Should().Be("ROOT");
            EnumStreams(_volumeRoot).Should().Contain(s => s.Name == ":ramdrive-root-ads:$DATA");
        }
        finally
        {
            try { File.Delete(rootStream); } catch { /* best effort */ }
        }
    }

    // ─── Names ───────────────────────────────────────────────────────────────

    [Fact]
    public void Stream_names_are_case_insensitive()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "MAIN");
        File.WriteAllText(file + ":MixedCase", "v");

        File.ReadAllText(file + ":mixedcase").Should().Be("v");
    }

    [Fact]
    public void An_extra_colon_in_the_stream_name_is_kept()
    {
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "MAIN");

        // Parsing splits on the FIRST colon in the leaf, so "a:b" is the stream name.
        File.WriteAllText(file + ":a:b", "nested");

        File.ReadAllText(file + ":a:b").Should().Be("nested");
        EnumStreams(file).Should().Contain(s => s.Name == ":a:b:$DATA");
    }
}
