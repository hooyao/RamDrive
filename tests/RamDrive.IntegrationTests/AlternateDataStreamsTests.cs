// Integration tests for NTFS Alternate Data Streams (named streams / ADS).
//
// RamDrive models each named stream as its own FileNode keyed by the full path
// including ':' (e.g. "\dir\file.txt:stream1"), mirroring WinFsp memfs. A stream is
// attached data: it never appears in directory enumeration, it follows its base object
// on rename, and it is deleted together with it. Streams may be attached to files,
// directories and the volume root (NTFS semantics).
//
// See WinFspRamAdapter / RamFileSystem for the implementation.
//
// NOTE: these tests mount a real WinFsp volume and therefore require WinFsp to be
// installed on the machine running them.

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
    public void Creating_a_stream_on_a_missing_base_creates_the_base_file()
    {
        // NTFS semantics: addressing a stream of an object that does not exist yet creates
        // that object implicitly (a zero-length main stream plus the named stream).
        string baseFile = Path.Combine(_dir, "created-by-stream.txt");

        File.WriteAllText(baseFile + ":s", "x");

        File.Exists(baseFile).Should().BeTrue();
        File.Exists(baseFile + ":s").Should().BeTrue();
        File.ReadAllText(baseFile).Should().BeEmpty("only the named stream carries data");
        File.ReadAllText(baseFile + ":s").Should().Be("x");
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

    [Fact]
    public void Case_only_rename_preserves_content_and_streams()
    {
        // Regression: Children is case-insensitive, so a case-only rename resolves the
        // destination to the source node itself. It must NOT be treated as a
        // replacement (which disposed the source and its streams, corrupting the tree).
        string file = Path.Combine(_dir, "a.txt");
        string renamed = Path.Combine(_dir, "A.txt");
        File.WriteAllText(file, "MAIN");
        File.WriteAllText(file + ":s1", "STREAM1");
        File.WriteAllText(file + ":s2", "STREAM2");

        File.Move(file, renamed, overwrite: true);

        File.ReadAllText(renamed).Should().Be("MAIN");
        File.ReadAllText(renamed + ":s1").Should().Be("STREAM1");
        File.ReadAllText(renamed + ":s2").Should().Be("STREAM2");
        // Lookups stay case-insensitive (NTFS): the old spelling still resolves to the
        // renamed object, and so do its streams. Only the canonical spelling is
        // enumerated (checked below); the old one is not a second entry.
        File.ReadAllText(file).Should().Be("MAIN");
        File.Exists(file + ":s1").Should().BeTrue("the old stream spelling still resolves");
        File.Exists(file + ":s2").Should().BeTrue("the old stream spelling still resolves");
        File.ReadAllText(file + ":s1").Should().Be("STREAM1");
        File.ReadAllText(file + ":s2").Should().Be("STREAM2");
        Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "A.txt" });
        // The stream-enumeration path (WinFsp FindFirstStreamW) must also see both
        // streams under the new spelling.
        EnumStreams(renamed).Select(s => s.Name)
            .Should().BeEquivalentTo(new[] { "::$DATA", ":s1:$DATA", ":s2:$DATA" });
    }

    [Fact]
    public void Case_only_rename_without_overwrite_succeeds()
    {
        // A case-only rename is not a name collision: replacing "a.txt" with "A.txt"
        // keeps the very same object. NTFS allows this without an overwrite request,
        // and so must RamDrive.
        string file = Path.Combine(_dir, "a.txt");
        string renamed = Path.Combine(_dir, "A.txt");
        File.WriteAllText(file, "MAIN");
        File.WriteAllText(file + ":s1", "STREAM1");

        File.Move(file, renamed);

        File.ReadAllText(renamed).Should().Be("MAIN");
        File.ReadAllText(renamed + ":s1").Should().Be("STREAM1");
        Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "A.txt" });
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
        // The stream must not surface as an entry of its base either.
        Directory.GetFileSystemEntries(dir).Should().BeEmpty();
        Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "sub" });
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

    [Fact]
    public void A_bare_stream_inside_a_subdirectory_is_rejected()
    {
        // NTFS allows a bare stream ("\:ads", empty base name) only directly on the
        // volume root. Inside any subdirectory the very same syntax is refused with
        // ERROR_INVALID_NAME — verified against a real NTFS volume. RamDrive must match
        // that, otherwise an editor happily "saves" a stream NTFS would never create.
        string sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);

        Action act = () => File.WriteAllText(sub + "\\:bare", "x");

        act.Should().Throw<IOException>();
        File.Exists(sub + "\\:bare").Should().BeFalse();
        File.Exists(sub + "\\:bare:$DATA").Should().BeFalse();
    }

    [Fact]
    public void A_stream_on_a_named_subdirectory_is_allowed()
    {
        // The forbidden shape is "dir\:ads" (empty base); "dir:ads" (the directory's
        // own name in front of the colon) is a legitimate stream on that directory and
        // works anywhere, including inside a subdirectory.
        string dir = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(dir);

        File.WriteAllText(dir + ":meta", "DIR-META");

        File.ReadAllText(dir + ":meta").Should().Be("DIR-META");
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
    public void A_second_colon_in_the_leaf_is_rejected_by_the_platform()
    {
        // Win32 path parsing rejects a second ':' in the leaf (ERROR_INVALID_NAME), so a
        // stream whose name itself contains ':' cannot be addressed from user mode — and
        // neither can the quoted form. StreamPath splitting on the FIRST ':' therefore
        // matches exactly what the platform is able to hand us.
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "MAIN");

        Action act = () => File.WriteAllText(file + ":a:b", "nested");

        act.Should().Throw<IOException>();
        File.Exists(file + ":a:b").Should().BeFalse();
        File.Exists(file + ":\"a:b\"").Should().BeFalse();
        File.ReadAllText(file).Should().Be("MAIN");
    }

    // ─── The :$DATA type suffix ──────────────────────────────────────────────

    [Fact]
    public void The_explicit_main_stream_form_reads_the_main_stream()
    {
        // "f::$DATA" is the main stream spelled with its type; it must be the same
        // stream as "f". Only the FIRST colon separates the name, the second the type.
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "MAIN");

        File.ReadAllText(file + "::$DATA").Should().Be("MAIN");
    }

    [Fact]
    public void A_stream_literally_named_DATA_does_not_overwrite_the_main_stream()
    {
        // Regression: "$DATA" in the NAME position is an ordinary stream name, not the
        // main stream. Collapsing it to the base made this write destroy the file's
        // main content (and the stream was never created).
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "MAIN");

        File.WriteAllText(file + ":$DATA", "DOLLAR");

        File.ReadAllText(file).Should().Be("MAIN", "the main stream must be untouched");
        File.ReadAllText(file + ":$DATA").Should().Be("DOLLAR", "the literal $DATA stream was created");
        // Two distinct streams now exist: the main stream and "$DATA".
        EnumStreams(file).Select(s => s.Name)
            .Should().BeEquivalentTo(new[] { "::$DATA", ":$DATA:$DATA" });
    }

    [Fact]
    public void A_named_stream_type_suffix_is_optional()
    {
        // "f:s1:$DATA" is the same stream as "f:s1".
        string file = Path.Combine(_dir, "a.txt");
        File.WriteAllText(file, "MAIN");

        File.WriteAllText(file + ":s1:$DATA", "S1");

        File.ReadAllText(file + ":s1").Should().Be("S1");
        File.ReadAllText(file + ":s1:$DATA").Should().Be("S1");
        File.ReadAllText(file).Should().Be("MAIN");
    }
}
