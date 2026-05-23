// Unit tests for RamFileSystem.CreateFile/CreateDirectory SD inheritance.
//
// Background: prior to the fix, calling CreateFile(path) or CreateDirectory(path)
// without an SD argument left FileNode.SecurityDescriptor = null. This bypassed
// WinFsp's kernel inheritance (which only fires when the user-mode CreateFile
// callback is invoked) and produced "born-null" SDs on every InitialDirectories
// node. WinFspRamAdapter.GetFileSecurityByName then returned that null to the
// WinFsp kernel as success, which strict access-check callers (MSIX AppContainer)
// interpreted as "invalid SD" → access denied → Windows365.exe crash on launch.
//
// Pins the structural invariant established by spec default-security-descriptor:
// "no FileNode may have SecurityDescriptor == null".

using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;

namespace RamDrive.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class RamFileSystemSdInheritanceTests : IDisposable
{
    private readonly PagePool _pool;
    private readonly RamFileSystem _fs;
    private readonly byte[] _rootSd;

    public RamFileSystemSdInheritanceTests()
    {
        var opts = new RamDriveOptions { CapacityMb = 8, PageSizeKb = 64 };
        _pool = new PagePool(new OptionsWrapper<RamDriveOptions>(opts), NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);

        // Mirror what WinFspRamAdapter ctor does — install the canonical root SD bytes.
        var sd = new System.Security.AccessControl.RawSecurityDescriptor(
            "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;WD)");
        _rootSd = new byte[sd.BinaryLength];
        sd.GetBinaryForm(_rootSd, 0);
        _fs.SetRootSecurityDescriptor(_rootSd);
    }

    public void Dispose()
    {
        _fs.Dispose();
        _pool.Dispose();
    }

    [Fact]
    public void CreateDirectory_WithoutSd_InheritsRootSd()
    {
        var node = _fs.CreateDirectory(@"\Temp");

        node.Should().NotBeNull();
        node!.SecurityDescriptor.Should().NotBeNull("no FileNode may have a null SecurityDescriptor");
        node.SecurityDescriptor.Should().BeSameAs(_rootSd,
            "the child should share the parent's SD byte[] by reference (copy-on-write — see CreateFile comment)");
    }

    [Fact]
    public void CreateFile_WithoutSd_InheritsRootSd()
    {
        var node = _fs.CreateFile(@"\file.txt");

        node.Should().NotBeNull();
        node!.SecurityDescriptor.Should().NotBeNull();
        node.SecurityDescriptor.Should().BeSameAs(_rootSd);
    }

    [Fact]
    public void NestedCreate_PropagatesRootSdThroughEveryLevel()
    {
        // Reproduces the spec scenario "Nested InitialDirectories produce non-null SDs at every level"
        // — { "Cache": { "App1": {}, "App2": {} } } as built by CreateDirectoriesRecursive.
        var cache = _fs.CreateDirectory(@"\Cache");
        var app1 = _fs.CreateDirectory(@"\Cache\App1");
        var app2 = _fs.CreateDirectory(@"\Cache\App2");

        cache.Should().NotBeNull();
        app1.Should().NotBeNull();
        app2.Should().NotBeNull();

        cache!.SecurityDescriptor.Should().BeSameAs(_rootSd);
        app1!.SecurityDescriptor.Should().BeSameAs(_rootSd, "inherit chain is parent→child by reference");
        app2!.SecurityDescriptor.Should().BeSameAs(_rootSd);
    }

    [Fact]
    public void DeepNesting_PropagatesSd()
    {
        // { "Work": { "Build": { "Output": {} } } } — 3 levels deep
        _fs.CreateDirectory(@"\Work");
        _fs.CreateDirectory(@"\Work\Build");
        var output = _fs.CreateDirectory(@"\Work\Build\Output");

        output.Should().NotBeNull();
        output!.SecurityDescriptor.Should().BeSameAs(_rootSd);
    }

    [Fact]
    public void ExplicitSd_BeatsInheritance()
    {
        var custom = new byte[] { 1, 2, 3, 4 };  // not a real SD, but distinct bytes ensure we can detect the choice

        var node = _fs.CreateDirectory(@"\X", custom);

        node.Should().NotBeNull();
        node!.SecurityDescriptor.Should().BeSameAs(custom,
            "explicit SD argument must override parent inheritance");
        node.SecurityDescriptor.Should().NotBeSameAs(_rootSd);
    }

    [Fact]
    public void ChildOfCustomSdParent_InheritsCustom_NotRoot()
    {
        // Verify the inherit-from-parent contract walks dynamically — a child of a
        // node with a non-root SD inherits that custom SD, not the root.
        var custom = new byte[] { 9, 8, 7, 6 };
        _fs.CreateDirectory(@"\Custom", custom);

        var child = _fs.CreateDirectory(@"\Custom\Child");

        child.Should().NotBeNull();
        child!.SecurityDescriptor.Should().BeSameAs(custom,
            "inheritance is from the immediate parent, not the root");
    }

    [Fact]
    public void NoNullSdInvariant_HoldsAfterMixedCreations()
    {
        // After a realistic sequence of creates (with and without SD args, files and dirs,
        // nested levels), walk the entire tree and confirm no node has null SD.

        _fs.CreateDirectory(@"\Temp");
        _fs.CreateDirectory(@"\Cache");
        _fs.CreateDirectory(@"\Cache\App1");
        _fs.CreateDirectory(@"\Cache\App2", new byte[] { 1, 2, 3, 4 });
        _fs.CreateDirectory(@"\Cache\App2\Sub");  // child of custom-SD parent
        _fs.CreateFile(@"\Temp\file.bin");
        _fs.CreateFile(@"\Cache\App1\data.bin");

        // Walk via ListDirectory recursively — proxy for the tree.
        WalkAndAssert(@"\");

        void WalkAndAssert(string dir)
        {
            var entries = _fs.ListDirectory(dir);
            entries.Should().NotBeNull();
            foreach (var n in entries!)
            {
                n.SecurityDescriptor.Should().NotBeNull(
                    $"node at {dir}{n.Name} must not have null SD (post-fix structural invariant)");
                if (n.IsDirectory)
                {
                    var sub = dir.EndsWith('\\') ? dir + n.Name : dir + @"\" + n.Name;
                    WalkAndAssert(sub);
                }
            }
        }
    }

    [Fact]
    public void RootSd_IsNonNull_AfterSetRootSecurityDescriptor()
    {
        // The root SD must be non-null before any child is created. Since SetRootSecurityDescriptor
        // is called from RamFileSystem ctor in our test, and CreateDirectory at \X then inherits
        // from root, an empty ListDirectory on root with a single child should expose root's SD
        // via that child.
        var x = _fs.CreateDirectory(@"\X");
        x!.SecurityDescriptor.Should().BeSameAs(_rootSd);
    }
}
