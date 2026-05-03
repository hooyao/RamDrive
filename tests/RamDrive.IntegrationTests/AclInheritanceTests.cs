using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;

namespace RamDrive.IntegrationTests;

internal static partial class AclWin32
{
    public const uint DELETE = 0x00010000;
    public const uint FILE_READ_ATTRIBUTES = 0x80;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);
}

/// <summary>
/// Verifies the <c>default-security-descriptor</c> capability at the mounted-FS layer:
/// objects created under the volume root are reopenable by the creating principal,
/// and their effective DACL contains an inherited <c>FullControl</c> grant for Everyone.
/// </summary>
[Collection("RamDrive")]
[SupportedOSPlatform("windows")]
public class AclInheritanceTests(RamDriveFixture fx) : IDisposable
{
    private readonly string _dir = Path.Combine(fx.Root, $"acl_{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>
    /// Spec scenario "Newly-created file is reopenable by the calling principal".
    /// </summary>
    [Fact]
    public void NewFile_IsReopenableByCreator()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "file.txt");
        File.WriteAllBytes(path, "hello"u8.ToArray());

        // Reopen plain Read — must succeed
        using (var fs = File.OpenRead(path))
        {
            fs.Length.Should().Be(5);
        }

        // Reopen with Delete access (chrome's network temp-file pattern).
        // Use raw Win32 because .NET's FileStream doesn't expose DELETE in its FileAccess enum.
        nint h = AclWin32.CreateFile(path,
            AclWin32.DELETE | AclWin32.FILE_READ_ATTRIBUTES | AclWin32.SYNCHRONIZE,
            AclWin32.FILE_SHARE_READ | AclWin32.FILE_SHARE_WRITE | AclWin32.FILE_SHARE_DELETE,
            0, AclWin32.OPEN_EXISTING, AclWin32.FILE_ATTRIBUTE_NORMAL, 0);
        if (h == -1)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Reopen with DELETE access failed; ACL inheritance is broken if this is ACCESS_DENIED");
        AclWin32.CloseHandle(h);
    }

    /// <summary>
    /// Spec scenario "Newly-created directory is reopenable for enumeration".
    /// </summary>
    [Fact]
    public void NewDirectory_IsReopenableForEnumeration()
    {
        Directory.CreateDirectory(_dir);
        string sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "marker.txt"), "x");

        // Open directory + list children — exercises Read Data/List Directory access.
        var entries = Directory.GetFiles(sub);
        entries.Should().HaveCount(1);
        Path.GetFileName(entries[0]).Should().Be("marker.txt");
    }

    /// <summary>
    /// Spec scenario "Effective ACL on a fresh file grants FullControl to Everyone".
    /// </summary>
    [Fact]
    public void NewFile_EffectiveAcl_GrantsInheritedFullControlToEveryone()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "acl_check.txt");
        File.WriteAllText(path, "x");

        var sec = new FileInfo(path).GetAccessControl();
        var rules = sec.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            targetType: typeof(SecurityIdentifier));

        bool found = false;
        foreach (FileSystemAccessRule r in rules)
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            // S-1-1-0 = Everyone (WD)
            if (r.IdentityReference.Value != "S-1-1-0") continue;
            (r.FileSystemRights & FileSystemRights.FullControl).Should().Be(FileSystemRights.FullControl,
                "Everyone must have FullControl inherited from the root");
            r.IsInherited.Should().BeTrue(
                "the ACE must arrive on the file via inheritance from the root, not be set explicitly");
            found = true;
        }
        found.Should().BeTrue(
            "expected at least one inherited FullControl Allow ACE for Everyone (S-1-1-0); " +
            "if missing, the root SDDL likely lost its OI|CI flags");
    }
}
