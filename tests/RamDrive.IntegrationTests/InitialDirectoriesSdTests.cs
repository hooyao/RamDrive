// Integration test for the InitialDirectories SD-inheritance fix
// (spec default-security-descriptor, scenarios "InitialDirectories-created
// directory carries a valid SD", "GetFileSecurityByName never returns success
// with null SD", and "Subdirectory created under an InitialDirectories folder
// by a user-mode process is reopenable by AppContainer-style access checks").
//
// Independent fixture (not the shared RamDriveCollection) because the test
// needs to control mount-time directory creation precisely — bootstrapping
// \Temp via RamFileSystem.CreateDirectory(path) before the mount comes up,
// which is exactly what WinFspHostedService.CreateDirectoriesRecursive does
// in production.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using WinFsp.Native;

namespace RamDrive.IntegrationTests;

[SupportedOSPlatform("windows")]
internal static partial class SdWin32
{
    public const int OWNER_SECURITY_INFORMATION = 1;
    public const int GROUP_SECURITY_INFORMATION = 2;
    public const int DACL_SECURITY_INFORMATION = 4;

    [LibraryImport("advapi32.dll", EntryPoint = "GetFileSecurityW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileSecurity(string lpFileName, int RequestedInformation,
        byte[]? pSecurityDescriptor, int nLength, out int lpnLengthNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsValidSecurityDescriptor(byte[] pSecurityDescriptor);
}

[SupportedOSPlatform("windows")]
public sealed class InitialDirectoriesSdFixture : IDisposable
{
    public string Root { get; }

    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;
    private readonly FileSystemHost _host;

    public InitialDirectoriesSdFixture()
    {
        var options = new RamDriveOptions
        {
            CapacityMb = 64,
            PageSizeKb = 64,
            EnableKernelCache = true,
            FileInfoTimeoutMs = uint.MaxValue,
            VolumeLabel = "InitDirTest",
        };

        _pool = new PagePool(new OptionsWrapper<RamDriveOptions>(options), NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);
        var adapter = new WinFspRamAdapter(
            _fs, new OptionsWrapper<RamDriveOptions>(options), NullLogger<WinFspRamAdapter>.Instance);

        // Mirror what WinFspHostedService.CreateDirectoriesRecursive does after mount —
        // call _fs.CreateDirectory(path) directly, no SD argument. This is the exact
        // bypass path that produced the original bug.
        _fs.CreateDirectory(@"\Temp");
        _fs.CreateDirectory(@"\Cache");
        _fs.CreateDirectory(@"\Cache\App1");

        _host = new FileSystemHost(adapter);
        _host.Prefix = $@"\winfsp-tests\initdir-sd-{Environment.ProcessId}-{Guid.NewGuid():N}";

        int result = _host.Mount(null);
        if (result < 0)
            throw new InvalidOperationException($"WinFsp mount failed: 0x{result:X8}. Is WinFsp installed?");

        Root = _host.MountPoint!;
        if (!Root.EndsWith('\\')) Root += @"\";
    }

    public void Dispose()
    {
        _host.Dispose();
        _fs.Dispose();
        _pool.Dispose();
    }
}

[Collection("InitialDirectoriesSd")]
[CollectionDefinition("InitialDirectoriesSd")]
public class InitialDirectoriesSdCollection : ICollectionFixture<InitialDirectoriesSdFixture>;

/// <summary>
/// Regression tests for the InitialDirectories null-SD bug — see
/// openspec/changes/fix-initialdirectories-null-sd/proposal.md.
/// </summary>
[Collection("InitialDirectoriesSd")]
[SupportedOSPlatform("windows")]
public sealed class InitialDirectoriesSdTests(InitialDirectoriesSdFixture fx)
{
    /// <summary>
    /// Spec scenario "InitialDirectories-created directory carries a valid SD".
    /// </summary>
    [Fact]
    public void InitialDirectory_Temp_HasValidSelfRelativeSd()
    {
        string path = Path.Combine(fx.Root, "Temp");
        Directory.Exists(path).Should().BeTrue("the fixture must have bootstrapped \\Temp");

        // Probe size, then read SD via Win32 (mirrors what icacls/Get-Acl ultimately call).
        SdWin32.GetFileSecurity(path,
            SdWin32.OWNER_SECURITY_INFORMATION | SdWin32.GROUP_SECURITY_INFORMATION | SdWin32.DACL_SECURITY_INFORMATION,
            null, 0, out int needed);
        needed.Should().BeGreaterThan(0,
            "needed=0 means the adapter returned a NULL SD (the precise pre-fix symptom);" +
            " icacls would report 'error 1338 ERROR_INVALID_SECURITY_DESCR'");

        var buf = new byte[needed];
        bool ok = SdWin32.GetFileSecurity(path,
            SdWin32.OWNER_SECURITY_INFORMATION | SdWin32.GROUP_SECURITY_INFORMATION | SdWin32.DACL_SECURITY_INFORMATION,
            buf, needed, out _);
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetFileSecurity failed");

        SdWin32.IsValidSecurityDescriptor(buf).Should().BeTrue(
            "the OS itself must accept the bytes as a valid SD");

        // Decode and assert control flags.
        var sd = new RawSecurityDescriptor(buf, 0);
        sd.ControlFlags.Should().HaveFlag(ControlFlags.SelfRelative);
        sd.ControlFlags.Should().HaveFlag(ControlFlags.DiscretionaryAclPresent);
        sd.DiscretionaryAcl.Should().NotBeNull();
        sd.DiscretionaryAcl.Count.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Spec scenario "Nested InitialDirectories produce non-null SDs at every level".
    /// </summary>
    [Fact]
    public void InitialDirectory_NestedCacheApp1_HasValidSd()
    {
        string path = Path.Combine(fx.Root, "Cache", "App1");
        Directory.Exists(path).Should().BeTrue();

        SdWin32.GetFileSecurity(path, SdWin32.DACL_SECURITY_INFORMATION, null, 0, out int needed);
        needed.Should().BeGreaterThan(0);

        var buf = new byte[needed];
        bool ok = SdWin32.GetFileSecurity(path, SdWin32.DACL_SECURITY_INFORMATION, buf, needed, out _);
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

        SdWin32.IsValidSecurityDescriptor(buf).Should().BeTrue();
    }

    /// <summary>
    /// Spec scenario "Subdirectory created under an InitialDirectories folder by a user-mode
    /// process is reopenable by AppContainer-style access checks".
    ///
    /// We don't run under a restricted token here (the integration host runs with the test
    /// runner's full token), but we exercise the same WinFsp / kernel access-check path:
    /// create a subdir via user-mode CreateDirectoryW (goes through WinFspRamAdapter.CreateFile
    /// callback, which now sees a non-null parent SD), then read its effective DACL and
    /// require the inherited Everyone (WD) FullControl ACE.
    /// </summary>
    [Fact]
    public void Subdirectory_UnderInitialDirectory_InheritsEveryoneFullControl()
    {
        string sub = Path.Combine(fx.Root, "Temp", $"Sub_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sub);

        var sec = new DirectoryInfo(sub).GetAccessControl();
        var rules = sec.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(System.Security.Principal.SecurityIdentifier));

        bool found = false;
        foreach (FileSystemAccessRule r in rules)
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            if (r.IdentityReference.Value != "S-1-1-0") continue;  // S-1-1-0 = Everyone (WD)
            (r.FileSystemRights & FileSystemRights.FullControl).Should().Be(FileSystemRights.FullControl);
            r.IsInherited.Should().BeTrue(
                "the ACE must arrive via inheritance from \\Temp (which itself inherits from root)");
            found = true;
        }
        found.Should().BeTrue(
            "expected an inherited FullControl Allow ACE for Everyone — if missing, \\Temp had " +
            "a null/empty SD at CreateFile callback time and kernel inheritance produced an empty DACL");

        try { Directory.Delete(sub); } catch { }
    }

    /// <summary>
    /// Spec scenario "GetFileSecurityByName never returns success with null SD" — verified
    /// indirectly via user-mode probe: if GetFileSecurity (the kernel path that calls our
    /// GetFileSecurityByName) returns 0 needed bytes, the adapter has returned a null SD
    /// with success, which is the pre-fix bug.
    /// </summary>
    [Fact]
    public void GetFileSecurity_OnEveryInitialDirectory_ReturnsNonZeroSize()
    {
        string[] paths = {
            Path.Combine(fx.Root, "Temp"),
            Path.Combine(fx.Root, "Cache"),
            Path.Combine(fx.Root, "Cache", "App1"),
        };
        foreach (var p in paths)
        {
            SdWin32.GetFileSecurity(p, SdWin32.DACL_SECURITY_INFORMATION, null, 0, out int needed);
            needed.Should().BeGreaterThan(0,
                $"{p}: needed=0 indicates the adapter returned a NULL SD — the bug we fixed");
        }
    }

    /// <summary>
    /// Coverage for the <see cref="WinFspRamAdapter.GetFileSecurity"/> callback (handle-based,
    /// distinct from the path-based GetFileSecurityByName already covered above). Uses
    /// <c>DirectoryInfo.GetAccessControl()</c> which goes through the handle path internally.
    /// </summary>
    [Fact]
    public void GetFileSecurity_HandleBased_OnInitialDirectory_ReturnsValidSd()
    {
        string path = Path.Combine(fx.Root, "Temp");
        var sec = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        var sddl = sec.GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        sddl.Should().NotBeNullOrEmpty(
            "handle-based GetFileSecurity must also return a valid SD — covers the second " +
            "null-SD callback (WinFspRamAdapter.GetFileSecurity, not GetFileSecurityByName)");
        sddl.Should().Contain("D:", "must include a DACL");
    }

    /// <summary>
    /// Reflection: compare our bypass-path SD to the NTFS standard.
    ///
    /// NTFS, when inheriting via SeAssignSecurityEx, sets the INHERITED_ACE (ID) flag on
    /// each copied ACE so callers can distinguish inherited from explicit. Our bypass
    /// path inherits the parent's SD bytes by reference, so \Temp's ACEs are byte-equal
    /// to root's ACEs and DO NOT carry the ID flag.
    ///
    /// This is a deliberate documented divergence — see openspec/changes/.../design.md
    /// Decision 1. The "no null SD" structural invariant is preserved; the only consequence
    /// is that on InitialDirectories nodes, GetAccessControl reports r.IsInherited == false.
    /// Children of \Temp created via the normal WinFsp callback path DO get the ID flag
    /// (set by WinFsp's FspCreateSecurityDescriptor), so this divergence stops at exactly
    /// the bypass-created nodes.
    ///
    /// This test pins the divergence so future maintainers know it is by design.
    /// If a stricter NTFS-equivalence requirement emerges, the fix is in
    /// RamFileSystem.CreateFile/CreateDirectory: instead of `node.SD = parent.SD`, compute
    /// a fresh SD with INHERITED_ACE applied to each ACE.
    /// </summary>
    [Fact]
    public void InitialDirectory_AcesAreFlaggedNotInherited_KnownDivergence()
    {
        string tempPath = Path.Combine(fx.Root, "Temp");
        var tempAcl = new DirectoryInfo(tempPath).GetAccessControl();
        var tempRules = tempAcl.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));

        bool anyInheritedFlagOnTemp = false;
        foreach (FileSystemAccessRule r in tempRules) if (r.IsInherited) anyInheritedFlagOnTemp = true;

        // The bypass-path node has no inherited-flag on its own ACEs (they're a byte-copy of root's).
        anyInheritedFlagOnTemp.Should().BeFalse(
            "documented divergence: bypass-created InitialDirectories nodes inherit the parent SD " +
            "by reference, not via NTFS's SeAssignSecurityEx, so the INHERITED_ACE (ID) flag is " +
            "not set on \\Temp's own ACEs. Children of \\Temp DO get the flag (covered by " +
            "Subdirectory_UnderInitialDirectory_InheritsEveryoneFullControl)");

        // But a child of \Temp created via the WinFsp callback path DOES get the inherited flag.
        string sub = Path.Combine(tempPath, $"NtfsEquivCheck_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sub);
        try
        {
            var subRules = new DirectoryInfo(sub).GetAccessControl()
                .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
            bool anyInheritedOnChild = false;
            foreach (FileSystemAccessRule r in subRules) if (r.IsInherited) anyInheritedOnChild = true;
            anyInheritedOnChild.Should().BeTrue(
                "children of bypass-path nodes go through WinFsp's FspCreateSecurityDescriptor, " +
                "which sets INHERITED_ACE — divergence is contained to the bypass node itself");
        }
        finally { try { Directory.Delete(sub); } catch { } }
    }
}
