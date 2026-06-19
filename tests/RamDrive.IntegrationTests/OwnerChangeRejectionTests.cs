using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using FluentAssertions;

namespace RamDrive.IntegrationTests;

internal static partial class OwnerWin32
{
    public const uint READ_CONTROL = 0x00020000;
    public const uint WRITE_DAC = 0x00040000;
    public const uint WRITE_OWNER = 0x00080000;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // required to open a directory handle
    public const uint FILE_SHARE_ALL = 1 | 2 | 4;
    public const uint OWNER_SECURITY_INFORMATION = 0x00000001;
    public const int ERROR_INVALID_OWNER = 1307;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetKernelObjectSecurity(nint handle, uint securityInformation, byte[] securityDescriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "GetFileSecurityW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileSecurity(string lpFileName, uint requestedInformation,
        byte[] pSecurityDescriptor, uint nLength, out uint lpnLengthNeeded);
}

/// <summary>
/// Verifies the <c>security-owner-enforcement</c> capability at the mounted-FS layer: a
/// non-privileged caller cannot reassign an object's owner. Written from the real-NTFS oracle —
/// on NTFS the attempt fails with <c>ERROR_INVALID_OWNER</c> (1307) and, because the failure is
/// atomic, the object keeps its creator-owner and stays deletable. This is the exact regression
/// behind dotTrace's leaked <c>jetbrainsproc_&lt;GUID&gt;</c> temp dirs.
/// </summary>
[Collection("RamDrive")]
[SupportedOSPlatform("windows")]
public class OwnerChangeRejectionTests(RamDriveFixture fx) : IDisposable
{
    private readonly string _dir = Path.Combine(fx.Root, $"own_{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string BuiltinAdministrators = "S-1-5-32-544"; // BA
    private const string LocalSystem = "S-1-5-18";               // SY

    private static string ReadOwnerSid(string path)
    {
        var buf = new byte[4096];
        if (!OwnerWin32.GetFileSecurity(path, OwnerWin32.OWNER_SECURITY_INFORMATION, buf, (uint)buf.Length, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetFileSecurity({path}) failed");
        return new RawSecurityDescriptor(buf, 0).Owner!.Value;
    }

    private static byte[] OwnerOnlySd(string sidValue)
    {
        var sd = new RawSecurityDescriptor($"O:{sidValue}");
        var b = new byte[sd.BinaryLength];
        sd.GetBinaryForm(b, 0);
        return b;
    }

    [Fact]
    public void OwnerChangeToDifferentSid_IsRefused_AndDirStaysDeletableAndAclReadable()
    {
        Directory.CreateDirectory(_dir);

        // Owner of a freshly-created object is the creating principal (whatever the test runs as).
        string ownerBefore = ReadOwnerSid(_dir);

        // Pick a target owner guaranteed to differ from the current owner, so this is a genuine
        // owner *change* regardless of whether the test process is elevated.
        string targetOwner = ownerBefore == BuiltinAdministrators ? LocalSystem : BuiltinAdministrators;

        // Open a handle with WRITE_OWNER. RamDrive's default DACL grants Everyone FullAccess (which
        // includes WRITE_OWNER) and it does no open-time access check, so the handle opens — the
        // rejection must come from SetSecurity itself, mirroring the real failure mode.
        nint h = OwnerWin32.CreateFile(_dir,
            OwnerWin32.WRITE_OWNER | OwnerWin32.WRITE_DAC | OwnerWin32.READ_CONTROL,
            OwnerWin32.FILE_SHARE_ALL, 0, OwnerWin32.OPEN_EXISTING, OwnerWin32.FILE_FLAG_BACKUP_SEMANTICS, 0);
        h.Should().NotBe(-1, "the directory handle should open (RamDrive grants Everyone WRITE_OWNER)");

        bool setResult;
        int err;
        try
        {
            setResult = OwnerWin32.SetKernelObjectSecurity(h, OwnerWin32.OWNER_SECURITY_INFORMATION, OwnerOnlySd(targetOwner));
            err = Marshal.GetLastWin32Error();
        }
        finally
        {
            OwnerWin32.CloseHandle(h);
        }

        // Oracle (real NTFS): the owner reassignment is refused.
        setResult.Should().BeFalse("a non-privileged caller cannot reassign the owner (NTFS: ERROR_INVALID_OWNER)");
        err.Should().Be(OwnerWin32.ERROR_INVALID_OWNER,
            "WinFsp maps STATUS_INVALID_OWNER (0xC000005A) to ERROR_INVALID_OWNER (1307)");

        // The object retains its original owner — the ACL is still readable by the creator …
        ReadOwnerSid(_dir).Should().Be(ownerBefore, "the refused owner change must leave the owner untouched");

        // … and the directory is still deletable by its creator (pre-fix it became un-deletable,
        // which is what leaked dotTrace's jetbrainsproc_<GUID> temp dirs and broke the next run).
        var deleteAct = () => Directory.Delete(_dir);
        deleteAct.Should().NotThrow("the directory must remain deletable after a refused owner change");
    }

    [Fact]
    public void OwnerUnchanged_DaclTighten_Succeeds()
    {
        Directory.CreateDirectory(_dir);

        // Tighten the DACL WITHOUT touching the owner — this must succeed (no over-rejection).
        var dirInfo = new DirectoryInfo(_dir);
        var sec = dirInfo.GetAccessControl();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var everyone = new System.Security.Principal.SecurityIdentifier("S-1-1-0");
        sec.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

        var act = () => dirInfo.SetAccessControl(sec);
        act.Should().NotThrow("a DACL-only change with the owner left intact must be accepted");
    }
}
