// Unit tests for WinFspRamAdapter.SetFileSecurity owner-change rejection.
//
// These tests are written from the REAL-NTFS oracle (observed on a true NTFS volume),
// NOT from the implementation: on NTFS a non-privileged caller cannot reassign an object's
// owner — the attempt fails atomically with ERROR_INVALID_OWNER (1307 / STATUS_INVALID_OWNER
// 0xC000005A), and crucially the DACL bundled in the same SetSecurity request is NOT applied,
// which is what keeps the object deletable by its creator.
//
// WinFsp cannot enforce this (no caller token at the SetSecurity callback), so the adapter
// approximates it by rejecting any owner *change*. See spec security-owner-enforcement.

using System.Runtime.Versioning;
using System.Security.AccessControl;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using RamDrive.Diagnostics.MemfsReference;
using WinFsp.Native;

namespace RamDrive.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class WinFspRamAdapterSetSecurityTests : IDisposable
{
    // SECURITY_INFORMATION bits.
    private const uint OWNER = 0x1;
    private const uint DACL = 0x4;

    // STATUS_INVALID_OWNER — the NTFS oracle status for a refused owner reassignment.
    private const int StatusInvalidOwner = unchecked((int)0xC000005A);

    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;
    private readonly WinFspRamAdapter _adapter;

    public WinFspRamAdapterSetSecurityTests()
    {
        var opts = new RamDriveOptions { CapacityMb = 8, PageSizeKb = 64, VolumeLabel = "Test" };
        _pool = new PagePool(new OptionsWrapper<RamDriveOptions>(opts), NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);
        // Ctor calls SetRootSecurityDescriptor, so nodes inherit owner = BUILTIN\Administrators (BA).
        _adapter = new WinFspRamAdapter(_fs, new OptionsWrapper<RamDriveOptions>(opts), NullLogger<WinFspRamAdapter>.Instance);
    }

    public void Dispose()
    {
        _fs.Dispose();
        _pool.Dispose();
    }

    private static byte[] Sd(string sddl)
    {
        var r = new RawSecurityDescriptor(sddl);
        var b = new byte[r.BinaryLength];
        r.GetBinaryForm(b, 0);
        return b;
    }

    private FileOperationInfo NodeInfo(string path)
    {
        var node = _fs.CreateDirectory(path);
        node.Should().NotBeNull();
        return new FileOperationInfo { Context = node };
    }

    [Fact]
    public void SetFileSecurity_OwnerChangeToDifferentSid_ReturnsInvalidOwner_AndLeavesSdUnchanged()
    {
        var info = NodeInfo(@"\victim");
        var node = (FileNode)info.Context!;
        byte[] before = (byte[])node.SecurityDescriptor!.Clone();

        // Current owner is BA (inherited from root). This modification changes owner to WD (Everyone)
        // AND tightens the DACL — the exact shape of dotTrace's locking SetSecurity.
        byte[] mod = Sd("O:WDD:P(A;;FA;;;SY)");
        // Sanity: the requested owner really differs from the current owner.
        new RawSecurityDescriptor(mod, 0).Owner!.Value
            .Should().NotBe(new RawSecurityDescriptor(before, 0).Owner!.Value);

        int status = _adapter.SetFileSecurity(@"\victim", OWNER | DACL, mod, info);

        status.Should().Be(StatusInvalidOwner, "NTFS refuses owner reassignment by a non-privileged caller");
        node.SecurityDescriptor.Should().Equal(before,
            "the failure is atomic — neither the owner nor the bundled DACL change may be applied");
    }

    [Fact]
    public void SetFileSecurity_DaclOnlyChange_OwnerUntouched_ReturnsSuccess_AndAppliesDacl()
    {
        var info = NodeInfo(@"\daclonly");
        var node = (FileNode)info.Context!;
        string ownerBefore = new RawSecurityDescriptor(node.SecurityDescriptor!, 0).Owner!.Value;

        // DACL only (owner bit NOT set): a protected DACL granting Everyone read.
        byte[] mod = Sd("D:P(A;;FR;;;WD)");
        int status = _adapter.SetFileSecurity(@"\daclonly", DACL, mod, info);

        status.Should().Be(NtStatus.Success);
        var after = new RawSecurityDescriptor(node.SecurityDescriptor!, 0);
        after.Owner!.Value.Should().Be(ownerBefore, "a DACL-only change must not alter the owner");
        DaclContainsSid(after, "S-1-1-0").Should().BeTrue("the Everyone (WD) ACE must now be present");
    }

    [Fact]
    public void SetFileSecurity_OwnerInfoSetButSameOwner_ReturnsSuccess_AndAppliesDacl()
    {
        var info = NodeInfo(@"\sameowner");
        var node = (FileNode)info.Context!;
        // Re-apply the SAME owner (BA) together with a DACL change. This must NOT be rejected:
        // many real callers include OWNER info carrying the unchanged owner.
        byte[] mod = Sd("O:BAD:P(A;;FR;;;WD)");

        int status = _adapter.SetFileSecurity(@"\sameowner", OWNER | DACL, mod, info);

        status.Should().Be(NtStatus.Success, "re-applying the current owner is an idempotent no-op, not a change");
        var after = new RawSecurityDescriptor(node.SecurityDescriptor!, 0);
        after.Owner!.Value.Should().Be("S-1-5-32-544", "owner stays BUILTIN\\Administrators (BA)");
        DaclContainsSid(after, "S-1-1-0").Should().BeTrue("the accompanying DACL change must be applied");
    }

    [Fact]
    public void SetFileSecurity_NonexistentNode_ReturnsObjectNameNotFound()
    {
        var info = new FileOperationInfo { Context = null };
        int status = _adapter.SetFileSecurity(@"\nope", OWNER | DACL, Sd("O:WD"), info);
        status.Should().Be(NtStatus.ObjectNameNotFound);
    }

    // Lockstep guard: the production adapter and the MemfsReference oracle MUST return the SAME
    // status for an owner change, or the differential checker breaks. ChaosTests does NOT exercise
    // SetSecurity, so the differential fuzzer would never catch a divergence here — this pins it.
    [Fact]
    public async Task MemfsReference_OwnerChange_ReturnsSameStatus_AsProduction()
    {
        // Production: create a file, current owner inherited = BA.
        var ramInfo = new FileOperationInfo();
        var ramCreate = await _adapter.CreateFile(@"\f", 0, 0, 0, null, 0, ramInfo, default);
        ramCreate.Status.Should().Be(NtStatus.Success);

        // Oracle: create a file with no SD → falls back to default root SD, owner = BA.
        var memfs = new MemfsReferenceFs(8);
        var memInfo = new FileOperationInfo();
        var memCreate = await memfs.CreateFile(@"\f", 0, 0, 0, null, 0, memInfo, default);
        memCreate.Status.Should().Be(NtStatus.Success);

        byte[] mod = Sd("O:WDD:P(A;;FA;;;SY)");   // owner WD ≠ BA → owner change
        int ramStatus = _adapter.SetFileSecurity(@"\f", OWNER | DACL, mod, ramInfo);
        int memStatus = memfs.SetFileSecurity(@"\f", OWNER | DACL, mod, memInfo);

        ramStatus.Should().Be(StatusInvalidOwner);
        memStatus.Should().Be(ramStatus, "the oracle must stay in lockstep with production on SetFileSecurity");
    }

    private static bool DaclContainsSid(RawSecurityDescriptor sd, string sidValue)
    {
        var dacl = sd.DiscretionaryAcl;
        if (dacl == null) return false;
        for (int i = 0; i < dacl.Count; i++)
            if (dacl[i] is CommonAce ace && ace.SecurityIdentifier.Value == sidValue)
                return true;
        return false;
    }
}
