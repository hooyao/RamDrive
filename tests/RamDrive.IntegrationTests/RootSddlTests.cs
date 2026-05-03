using System.Runtime.Versioning;
using System.Security.AccessControl;
using FluentAssertions;

namespace RamDrive.IntegrationTests;

/// <summary>
/// Pin the canonical root SDDL specified by the <c>default-security-descriptor</c> capability.
///
/// <para>The constant lives in two places (production <c>WinFspRamAdapter.cs</c> and the
/// integration <c>TestAdapter</c> in <c>RamDriveFixture.cs</c>). Both must match this
/// exact string and every ACE in it must carry <c>OBJECT_INHERIT_ACE | CONTAINER_INHERIT_ACE</c>.
/// A future SDDL edit that drops those flags fails this test before it can ship.</para>
///
/// <para>Mounted-FS behaviour is exercised separately in <c>AclInheritanceTests</c>;
/// this class is a static-content guard with no FS dependency, so it lives in this assembly
/// only because the constant is internal-private to the consuming projects.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public class RootSddlTests
{
    /// <summary>
    /// The canonical root SDDL mandated by spec <c>default-security-descriptor</c>.
    /// Mirror of the <c>RootSddl</c> constant in <c>WinFspRamAdapter.cs</c> and <c>RamDriveFixture.cs</c>.
    /// </summary>
    private const string CanonicalRootSddl =
        "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;WD)";

    [Fact]
    public void RootSddl_EveryAce_HasObjectAndContainerInheritFlags()
    {
        var sd = new RawSecurityDescriptor(CanonicalRootSddl);
        var dacl = sd.DiscretionaryAcl;
        dacl.Should().NotBeNull("the canonical root SDDL must include a DACL");
        dacl.Count.Should().BeGreaterThan(0, "the DACL must contain at least one ACE");

        const AceFlags requiredFlags = AceFlags.ObjectInherit | AceFlags.ContainerInherit;
        for (int i = 0; i < dacl.Count; i++)
        {
            var ace = dacl[i];
            (ace.AceFlags & requiredFlags).Should().Be(requiredFlags,
                "ACE #{0} (type={1}) must carry OICI so newly-created files and directories " +
                "inherit it; without these flags WinFsp's FspCreateSecurityDescriptor leaves " +
                "children with an empty DACL and reopen returns ACCESS_DENIED",
                i, ace.AceType);
        }
    }

    [Fact]
    public void RootSddl_GrantsFullControlToWellKnownPrincipals()
    {
        var sd = new RawSecurityDescriptor(CanonicalRootSddl);
        var dacl = sd.DiscretionaryAcl;

        // We expect three Allow ACEs: SYSTEM (SY), Administrators (BA), Everyone (WD).
        var sids = new System.Collections.Generic.List<string>();
        for (int i = 0; i < dacl.Count; i++)
        {
            if (dacl[i] is CommonAce ace && ace.AceType == AceType.AccessAllowed)
                sids.Add(ace.SecurityIdentifier.Value);
        }
        sids.Should().Contain("S-1-5-18", "LocalSystem (SY) must be granted access");
        sids.Should().Contain("S-1-5-32-544", "BUILTIN\\Administrators (BA) must be granted access");
        sids.Should().Contain("S-1-1-0", "Everyone (WD) must be granted access");
    }
}
