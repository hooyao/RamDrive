using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace RamDrive.IntegrationTests;

[Collection("RamDrive")]
[SupportedOSPlatform("windows")]
public sealed class ReparseLinkTests(RamDriveFixture fixture)
{
    private readonly string _root = fixture.ResetForTest().Root;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize,
        byte[]? output, int outputSize, out int returned, nint overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes { public Luid Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PrivilegeSet { public uint Count; public uint Control; public LuidAndAttributes Privilege; }

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadToken(nint thread, uint access, bool openAsSelf, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrivilegeCheck(SafeAccessTokenHandle token, ref PrivilegeSet privileges,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags,
        uint disabledSidCount, nint disabledSids, uint deletedPrivilegeCount, nint deletedPrivileges,
        uint restrictedSidCount, nint restrictedSids, out SafeAccessTokenHandle token);

    private static SafeFileHandle LinkHandle(string path, uint access = 0xC0000000)
    {
        var handle = CreateFile(path, access, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"open link {path}");
        return handle;
    }

    private static (bool Ok, int Error, byte[] Data) Control(string path, uint code, byte[]? input = null)
    {
        using var handle = LinkHandle(path);
        byte[]? output = code == 0x000900A8 ? new byte[16384] : null;
        bool ok = DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output?.Length ?? 0, out int returned, 0);
        return (ok, ok ? 0 : Marshal.GetLastWin32Error(), output == null ? [] : output[..returned]);
    }

    private static byte[] JunctionData(string substitute)
    {
        // Windows paths are UTF-16 code units and may contain unpaired surrogates.
        var name = MemoryMarshal.AsBytes(substitute.AsSpan()).ToArray();
        var data = new byte[16 + name.Length + 2 + name.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(data.Length - 8));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), (ushort)(name.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)name.Length);
        name.CopyTo(data, 16);
        name.CopyTo(data, 16 + name.Length + 2);
        return data;
    }

    [Fact]
    public void RelativeDirectoryAndFileSymlinks_ResolveAndShareTarget()
    {
        string target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "data.txt"), "first");
        string dirLink = Path.Combine(_root, "dirlink");
        string fileLink = Path.Combine(_root, "filelink");
        Directory.CreateSymbolicLink(dirLink, "target");
        File.CreateSymbolicLink(fileLink, @"target\data.txt");

        File.ReadAllText(Path.Combine(dirLink, "data.txt")).Should().Be("first");
        File.WriteAllText(fileLink, "second");
        File.ReadAllText(Path.Combine(target, "data.txt")).Should().Be("second");
        File.ReadAllText(Path.Combine(dirLink, "data.txt")).Should().Be("second");
        File.GetAttributes(dirLink).Should().HaveFlag(FileAttributes.ReparsePoint);
        new DirectoryInfo(dirLink).LinkTarget.Should().Be("target");
        new FileInfo(fileLink).LinkTarget.Should().Be(@"target\data.txt");
        Control(dirLink, 0x000900A8).Ok.Should().BeTrue();
    }

    [Fact]
    public void LinkTargets_EnforceTargetReadAccess()
    {
        string targetDir = Path.Combine(_root, "protected");
        Directory.CreateDirectory(targetDir);
        string target = Path.Combine(targetDir, "data");
        File.WriteAllText(target, "private");
        string fileLink = Path.Combine(_root, "file-link");
        string dirLink = Path.Combine(_root, "dir-link");
        string junction = Path.Combine(_root, "junction");
        File.CreateSymbolicLink(fileLink, @"protected\data");
        Directory.CreateSymbolicLink(dirLink, "protected");
        Directory.CreateDirectory(junction);
        Control(junction, 0x000900A4, JunctionData(@"\??\UNC\" + targetDir.TrimStart('\\'))).Ok.Should().BeTrue();

        var info = new FileInfo(target);
        FileSecurity security = info.GetAccessControl();
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        security.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.ReadData, AccessControlType.Deny));
        info.SetAccessControl(security);

        foreach (string path in new[] { target, fileLink, Path.Combine(dirLink, "data"), Path.Combine(junction, "data") })
            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(path));
        Control(fileLink, 0x000900A8).Ok.Should().BeTrue("the link itself remains accessible");
    }

    [Fact]
    public void LinkTargets_ShareFileIdentityAndCachedData()
    {
        string targetDir = Path.Combine(_root, "target");
        Directory.CreateDirectory(targetDir);
        string target = Path.Combine(targetDir, "data");
        File.WriteAllText(target, "before");
        string fileLink = Path.Combine(_root, "file-link");
        string dirLink = Path.Combine(_root, "dir-link");
        File.CreateSymbolicLink(fileLink, @"target\data");
        Directory.CreateSymbolicLink(dirLink, "target");

        using var direct = File.OpenHandle(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var throughFile = File.OpenHandle(fileLink, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var throughDirectory = File.OpenHandle(Path.Combine(dirLink, "data"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        GetFileInformationByHandle(direct, out var directInfo).Should().BeTrue();
        GetFileInformationByHandle(throughFile, out var fileInfo).Should().BeTrue();
        GetFileInformationByHandle(throughDirectory, out var dirInfo).Should().BeTrue();
        directInfo.VolumeSerialNumber.Should().NotBe(0);
        (directInfo.FileIndexHigh | directInfo.FileIndexLow).Should().NotBe(0);
        (fileInfo.VolumeSerialNumber, fileInfo.FileIndexHigh, fileInfo.FileIndexLow)
            .Should().Be((directInfo.VolumeSerialNumber, directInfo.FileIndexHigh, directInfo.FileIndexLow));
        (dirInfo.VolumeSerialNumber, dirInfo.FileIndexHigh, dirInfo.FileIndexLow)
            .Should().Be((directInfo.VolumeSerialNumber, directInfo.FileIndexHigh, directInfo.FileIndexLow));

        File.WriteAllText(target, "a longer value");
        File.ReadAllText(fileLink).Should().Be("a longer value");
        File.WriteAllText(Path.Combine(dirLink, "data"), "changed via link");
        File.ReadAllText(target).Should().Be("changed via link");
        RandomAccess.GetLength(throughFile).Should().Be("changed via link".Length);
    }

    [Fact]
    public void AbsoluteLinks_PreserveUnpairedSurrogatesInTargets()
    {
        string target = Path.Combine(_root, "target-\uD800");
        string decoy = Path.Combine(_root, "target-\uFFFD");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(decoy);
        File.WriteAllText(Path.Combine(target, "data"), "expected");
        File.WriteAllText(Path.Combine(decoy, "data"), "decoy");

        string symlink = Path.Combine(_root, "absolute");
        Directory.CreateSymbolicLink(symlink, target);
        File.ReadAllText(Path.Combine(symlink, "data")).Should().Be("expected");
        File.WriteAllText(Path.Combine(symlink, "data"), "updated");
        File.ReadAllText(Path.Combine(target, "data")).Should().Be("updated");
        File.ReadAllText(Path.Combine(decoy, "data")).Should().Be("decoy");

        using var other = new RamDriveFixture();
        string junction = Path.Combine(other.Root, "junction");
        Directory.CreateDirectory(junction);
        byte[] raw = JunctionData(@"\??\UNC\" + target.TrimStart('\\'));
        Control(junction, 0x000900A4, raw).Ok.Should().BeTrue();
        File.ReadAllText(Path.Combine(junction, "data")).Should().Be("updated");
        Control(junction, 0x000900A8).Data.Should().Equal(raw);
    }

    [Fact]
    public void DirectoryLinks_WithTrailingSeparators_ResolveChildren()
    {
        string target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "data"), "contents");
        using var other = new RamDriveFixture();
        string externalTarget = Path.Combine(other.Root, "target");
        Directory.CreateDirectory(externalTarget);
        File.WriteAllText(Path.Combine(externalTarget, "data"), "external");

        string relative = Path.Combine(_root, "relative");
        string absolute = Path.Combine(_root, "absolute");
        string external = Path.Combine(_root, "external");
        Directory.CreateSymbolicLink(relative, @"target\");
        Directory.CreateSymbolicLink(absolute, target + @"\\");
        Directory.CreateSymbolicLink(external, externalTarget + @"\");
        byte[] relativeData = Control(relative, 0x000900A8).Data;
        byte[] absoluteData = Control(absolute, 0x000900A8).Data;
        byte[] externalData = Control(external, 0x000900A8).Data;
        foreach (string link in new[] { relative, absolute })
            File.ReadAllText(Path.Combine(link, "data")).Should().Be("contents");
        File.ReadAllText(Path.Combine(external, "data")).Should().Be("external");
        new DirectoryInfo(relative).LinkTarget.Should().Be(@"target\");
        Control(relative, 0x000900A8).Data.Should().Equal(relativeData);
        Control(absolute, 0x000900A8).Data.Should().Equal(absoluteData);
        Control(external, 0x000900A8).Data.Should().Equal(externalData);

        foreach (var (name, destination, expected) in new[]
        {
            ("local-junction", target, "contents"),
            ("external-junction", externalTarget, "external")
        })
        {
            string junction = Path.Combine(_root, name);
            Directory.CreateDirectory(junction);
            byte[] raw = JunctionData(@"\??\UNC\" + destination.TrimStart('\\') + @"\");
            Control(junction, 0x000900A4, raw).Ok.Should().BeTrue();
            File.ReadAllText(Path.Combine(junction, "data")).Should().Be(expected);
            Control(junction, 0x000900A8).Data.Should().Equal(raw);
        }
    }

    [Fact]
    public void Junction_RoundTrip_TraversesUncTarget_AndDeleteKeepsDirectory()
    {
        string target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "data.txt"), "junction");
        string link = Path.Combine(_root, "junction");
        Directory.CreateDirectory(link);
        string nativeTarget = @"\??\UNC\" + target.TrimStart('\\');
        byte[] buffer = JunctionData(nativeTarget);
        var set = Control(link, 0x000900A4, buffer);
        set.Ok.Should().BeTrue($"FSCTL_SET_REPARSE_POINT failed with {set.Error}");
        var get = Control(link, 0x000900A8);
        get.Ok.Should().BeTrue($"FSCTL_GET_REPARSE_POINT failed with {get.Error}");
        get.Data.Should().Equal(buffer);
        File.ReadAllText(Path.Combine(link, "data.txt")).Should().Be("junction");

        byte[] delete = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(delete, 0xA0000003);
        var result = Control(link, 0x000900AC, delete);
        result.Ok.Should().BeTrue($"FSCTL_DELETE_REPARSE_POINT failed with {result.Error}");
        Directory.Exists(link).Should().BeTrue();
        File.GetAttributes(link).Should().NotHaveFlag(FileAttributes.ReparsePoint);
        File.ReadAllText(Path.Combine(target, "data.txt")).Should().Be("junction");
    }

    [Fact]
    public void Junction_ToLocalVolume_TraversesTarget()
    {
        string target = Path.Combine(Path.GetTempPath(), "ramdrive-link-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "data.txt"), "external");
        try
        {
            string link = Path.Combine(_root, "external-junction");
            Directory.CreateDirectory(link);
            var set = Control(link, 0x000900A4, JunctionData(@"\??\" + target));
            set.Ok.Should().BeTrue($"set failed with {set.Error}");
            File.ReadAllText(Path.Combine(link, "data.txt")).Should().Be("external");
        }
        finally
        {
            File.Delete(Path.Combine(target, "data.txt"));
            Directory.Delete(target);
        }
    }

    [Fact]
    public void AbsoluteAndNestedLinks_SupportReadWriteAndRenameThroughThePath()
    {
        string target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "source"), "contents");
        string first = Path.Combine(_root, "first");
        string second = Path.Combine(_root, "second");
        Directory.CreateSymbolicLink(first, target);
        Directory.CreateSymbolicLink(second, "first");
        File.ReadAllText(Path.Combine(second, "source")).Should().Be("contents");
        File.Move(Path.Combine(second, "source"), Path.Combine(second, "renamed"));
        File.ReadAllText(Path.Combine(target, "renamed")).Should().Be("contents");
        File.WriteAllText(Path.Combine(second, "renamed"), "updated");
        File.ReadAllText(Path.Combine(target, "renamed")).Should().Be("updated");
    }

    [Fact]
    public void BrokenAndCyclicLinks_AreDirectlyQueryableAndDoNotHang()
    {
        string broken = Path.Combine(_root, "broken");
        File.CreateSymbolicLink(broken, "missing");
        Assert.ThrowsAny<IOException>(() => File.ReadAllText(broken));
        Control(broken, 0x000900A8).Ok.Should().BeTrue();

        string first = Path.Combine(_root, "cycle1");
        string second = Path.Combine(_root, "cycle2");
        Directory.CreateSymbolicLink(first, "cycle2");
        Directory.CreateSymbolicLink(second, "cycle1");
        var ex = Assert.ThrowsAny<IOException>(() => File.ReadAllText(Path.Combine(first, "test")));
        (ex.HResult & 0xFFFF).Should().Be(1921);
        Control(first, 0x000900A8).Ok.Should().BeTrue();
    }

    [Fact]
    public void RenamingAndDeletingLink_DoesNotAffectTarget()
    {
        string target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "data"), "kept");
        string link = Path.Combine(_root, "link");
        string renamed = Path.Combine(_root, "renamed");
        Directory.CreateSymbolicLink(link, "target");
        Directory.Move(link, renamed);
        File.ReadAllText(Path.Combine(renamed, "data")).Should().Be("kept");
        Directory.Delete(renamed);
        File.ReadAllText(Path.Combine(target, "data")).Should().Be("kept");
    }

    [Fact]
    public void InvalidMutations_DoNotChangeOriginalObject()
    {
        string dir = Path.Combine(_root, "nonempty");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "child"), "kept");
        var nonempty = Control(dir, 0x000900A4, JunctionData(@"\??\C:\target"));
        nonempty.Ok.Should().BeFalse();
        nonempty.Error.Should().Be(145); // ERROR_DIR_NOT_EMPTY
        File.ReadAllText(Path.Combine(dir, "child")).Should().Be("kept");

        string link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, "nonempty");
        var before = Control(link, 0x000900A8);
        byte[] wrongTag = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(wrongTag, 0xA0000003);
        var mismatch = Control(link, 0x000900AC, wrongTag);
        mismatch.Ok.Should().BeFalse();
        mismatch.Error.Should().Be(4394);
        var replaceMismatch = Control(link, 0x000900A4, JunctionData(@"\??\C:\target"));
        replaceMismatch.Ok.Should().BeFalse();
        replaceMismatch.Error.Should().Be(4394);
        Control(link, 0x000900A8).Data.Should().Equal(before.Data);

        string empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);
        byte[] invalid = JunctionData(@"\??\C:\target");
        BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(8), 1);
        var malformed = Control(empty, 0x000900A4, invalid);
        malformed.Ok.Should().BeFalse();
        malformed.Error.Should().Be(4392);
        File.GetAttributes(empty).Should().NotHaveFlag(FileAttributes.ReparsePoint);
    }

    [Fact]
    public void Junction_ToOwnUncRoot_ResolvesChildren()
    {
        Directory.CreateDirectory(Path.Combine(_root, "target"));
        File.WriteAllText(Path.Combine(_root, "target", "data"), "root");
        string link = Path.Combine(_root, "rootlink");
        Directory.CreateDirectory(link);
        var set = Control(link, 0x000900A4, JunctionData(@"\??\UNC\" + _root.Trim('\\')));
        set.Ok.Should().BeTrue($"set failed with {set.Error}");
        File.ReadAllText(Path.Combine(link, "target", "data")).Should().Be("root");
    }

    [Fact]
    public void CyclicUncJunctions_TerminateAndRemainQueryable()
    {
        string first = Path.Combine(_root, "junction-a");
        string second = Path.Combine(_root, "junction-b");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        Control(first, 0x000900A4, JunctionData(@"\??\UNC\" + second.TrimStart('\\'))).Ok.Should().BeTrue();
        Control(second, 0x000900A4, JunctionData(@"\??\UNC\" + first.TrimStart('\\'))).Ok.Should().BeTrue();
        var ex = Assert.ThrowsAny<IOException>(() => File.ReadAllText(Path.Combine(first, "missing")));
        (ex.HResult & 0xFFFF).Should().Be(1921);
        Control(first, 0x000900A8).Ok.Should().BeTrue();
    }

    [Fact]
    public void CyclicAbsoluteSymlinks_TerminateAndRemainQueryable()
    {
        string first = Path.Combine(_root, "symlink-a");
        string second = Path.Combine(_root, "symlink-b");
        Directory.CreateSymbolicLink(first, second);
        Directory.CreateSymbolicLink(second, first);
        var ex = Assert.ThrowsAny<IOException>(() => File.ReadAllText(Path.Combine(first, "missing")));
        (ex.HResult & 0xFFFF).Should().Be(1921);
        Control(first, 0x000900A8).Ok.Should().BeTrue();
    }

    [Fact]
    public void Symlink_ToDifferentUncVolume_IsResolvedByWindows()
    {
        using var other = new RamDriveFixture();
        Directory.CreateDirectory(Path.Combine(other.Root, "target"));
        File.WriteAllText(Path.Combine(other.Root, "target", "data"), "other volume");
        string link = Path.Combine(_root, "other-volume");
        Directory.CreateSymbolicLink(link, Path.Combine(other.Root, "target"));
        File.ReadAllText(Path.Combine(link, "data")).Should().Be("other volume");
        File.WriteAllText(Path.Combine(link, "data"), "changed");
        File.ReadAllText(Path.Combine(other.Root, "target", "data")).Should().Be("changed");
    }

    [Fact]
    public void RenameAndDeleteFileLink_LeaveTargetIntact()
    {
        string target = Path.Combine(_root, "target-file");
        File.WriteAllText(target, "kept");
        string link = Path.Combine(_root, "link-file");
        string renamed = Path.Combine(_root, "renamed-link");
        File.CreateSymbolicLink(link, "target-file");
        File.Move(link, renamed);
        File.ReadAllText(renamed).Should().Be("kept");
        File.Delete(renamed);
        File.ReadAllText(target).Should().Be("kept");
    }

    [Fact]
    public void DeleteFileReparseData_LeavesAnEmptyOrdinaryFile()
    {
        string target = Path.Combine(_root, "target");
        File.WriteAllText(target, "kept");
        string link = Path.Combine(_root, "file-link");
        File.CreateSymbolicLink(link, "target");
        var get = Control(link, 0x000900A8);
        get.Ok.Should().BeTrue();
        BinaryPrimitives.ReadUInt32LittleEndian(get.Data).Should().Be(0xA000000Cu);
        var delete = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(delete, 0xA000000C);
        var result = Control(link, 0x000900AC, delete);
        result.Ok.Should().BeTrue($"delete failed with {result.Error}");
        File.GetAttributes(link).Should().NotHaveFlag(FileAttributes.ReparsePoint);
        File.ReadAllText(link).Should().BeEmpty();
        File.ReadAllText(target).Should().Be("kept");
    }

    [Fact]
    public void SettingLinkOnNonemptyFile_MatchesNtfs()
    {
        string source = Path.Combine(_root, "source-link");
        File.CreateSymbolicLink(source, "missing-target");
        byte[] data = Control(source, 0x000900A8).Data;
        string ramFile = Path.Combine(_root, "existing-file");
        File.WriteAllText(ramFile, "content");
        string ntfsFile = Path.Combine(Path.GetTempPath(), "ramdrive-reparse-oracle-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(ntfsFile, "content");
        string ntfsEmpty = ntfsFile + ".empty";
        File.WriteAllText(ntfsEmpty, "");
        try
        {
            var baseline = Control(ntfsEmpty, 0x000900A4, data);
            baseline.Ok.Should().BeTrue($"NTFS rejected the test buffer: {baseline.Error}");
            var actual = Control(ramFile, 0x000900A4, data);
            var expected = Control(ntfsFile, 0x000900A4, data);
            actual.Ok.Should().Be(expected.Ok, $"NTFS error={expected.Error}, RamDrive error={actual.Error}");
            if (!expected.Ok) actual.Error.Should().Be(expected.Error);
        }
        finally { File.Delete(ntfsFile); File.Delete(ntfsEmpty); }
    }

    [Fact]
    public void SymbolicLinkMutations_WithoutPrivileges_MatchNtfs()
    {
        string source = Path.Combine(_root, "source-link");
        File.CreateSymbolicLink(source, "missing-target");
        byte[] data = Control(source, 0x000900A8).Data;
        string ramFile = Path.Combine(_root, "empty-file");
        File.WriteAllText(ramFile, "");
        string ntfsFile = Path.Combine(Path.GetTempPath(), "ramdrive-reparse-privilege-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(ntfsFile, "");
        string ntfsLink = ntfsFile + "-link";
        string ramLink = Path.Combine(_root, "delete-link");
        string ntfsCreated = ntfsFile + "-created";
        string ramCreated = Path.Combine(_root, "created-link");
        try
        {
            File.CreateSymbolicLink(ntfsLink, "missing-target");
            File.CreateSymbolicLink(ramLink, "missing-target");
            // Restrict only a duplicated token. The caller's process token and the
            // machine's Developer Mode configuration are never changed.
            OpenProcessToken(GetCurrentProcess(), 0x000E, out var processToken).Should().BeTrue();
            using (processToken)
            {
                CreateRestrictedToken(processToken, 0x1, 0, 0, 0, 0, 0, 0, out var restrictedToken).Should().BeTrue();
                using (restrictedToken)
                {
                    var expected = (Ok: false, Error: -1, Data: Array.Empty<byte>());
                    var actual = (Ok: false, Error: -1, Data: Array.Empty<byte>());
                    var expectedDelete = (Ok: false, Error: -1, Data: Array.Empty<byte>());
                    var actualDelete = (Ok: false, Error: -1, Data: Array.Empty<byte>());
                    int expectedCreate = -1, actualCreate = -1;
                    WindowsIdentity.RunImpersonated(restrictedToken, () =>
                    {
                        LookupPrivilegeValue(null, "SeCreateSymbolicLinkPrivilege", out var luid).Should().BeTrue();
                        var privileges = new PrivilegeSet { Count = 1, Control = 1, Privilege = new LuidAndAttributes { Luid = luid } };
                        OpenThreadToken(GetCurrentThread(), 8, true, out var threadToken).Should().BeTrue();
                        using (threadToken)
                        {
                            PrivilegeCheck(threadToken, ref privileges, out bool hasPrivilege).Should().BeTrue();
                            hasPrivilege.Should().BeFalse();
                        }
                        expected = Control(ntfsFile, 0x000900A4, data);
                        actual = Control(ramFile, 0x000900A4, data);
                        byte[] delete = new byte[8];
                        BinaryPrimitives.WriteUInt32LittleEndian(delete, 0xA000000C);
                        expectedDelete = Control(ntfsLink, 0x000900AC, delete);
                        actualDelete = Control(ramLink, 0x000900AC, delete);
                        try { File.CreateSymbolicLink(ntfsCreated, "missing-target"); expectedCreate = 0; }
                        catch (Exception ex) { expectedCreate = ex.HResult & 0xFFFF; }
                        try { File.CreateSymbolicLink(ramCreated, "missing-target"); actualCreate = 0; }
                        catch (Exception ex) { actualCreate = ex.HResult & 0xFFFF; }
                    });
                    object? devMode = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock", "AllowDevelopmentWithoutDevLicense", null);
                    Console.WriteLine($"Developer Mode: {devMode ?? "unset"}; NTFS: {expected.Ok}, {expected.Error}; RamDrive: {actual.Ok}, {actual.Error}");
                    Console.WriteLine($"NTFS delete: {expectedDelete.Ok}, {expectedDelete.Error}; RamDrive delete: {actualDelete.Ok}, {actualDelete.Error}");
                    Console.WriteLine($"NTFS CreateSymbolicLink: {expectedCreate}; RamDrive CreateSymbolicLink: {actualCreate}");
                    actual.Ok.Should().Be(expected.Ok, $"NTFS error={expected.Error}, RamDrive error={actual.Error}");
                    if (!expected.Ok) actual.Error.Should().Be(expected.Error);
                    actualDelete.Ok.Should().Be(expectedDelete.Ok, $"NTFS error={expectedDelete.Error}, RamDrive error={actualDelete.Error}");
                    if (!expectedDelete.Ok) actualDelete.Error.Should().Be(expectedDelete.Error);
                    actualCreate.Should().Be(expectedCreate);
                }
            }
        }
        finally { File.Delete(ntfsFile); File.Delete(ntfsLink); File.Delete(ntfsCreated); }
    }
}
