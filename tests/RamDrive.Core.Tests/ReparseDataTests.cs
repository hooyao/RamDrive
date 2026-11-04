using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using WinFsp.Native;

namespace RamDrive.Core.Tests;

public sealed class ReparseDataTests : IDisposable
{
    private readonly PagePool _pool = new(Options.Create(new RamDriveOptions { CapacityMb = 8, PageSizeKb = 64 }), NullLogger<PagePool>.Instance);
    private readonly RamFileSystem _fs;

    public ReparseDataTests() => _fs = new RamFileSystem(_pool);
    public void Dispose() { _fs.Dispose(); _pool.Dispose(); }

    private static byte[] Link(uint tag, string target = "target")
    {
        byte[] path = Encoding.Unicode.GetBytes(target);
        int offset = tag == ReparseData.SymbolicLink ? 20 : 16;
        byte[] data = new byte[offset + path.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), (ushort)(data.Length - 8));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)path.Length);
        if (tag == ReparseData.SymbolicLink) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 1);
        path.CopyTo(data, offset);
        return data;
    }

    private static byte[] DeleteBuffer(uint tag)
    {
        byte[] data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, tag);
        return data;
    }

    [Fact]
    public void RoundTrip_ClonesBuffers_AndProtectsAttributeAndTag()
    {
        var node = _fs.CreateDirectory(@"\link")!;
        byte[] data = Link(ReparseData.SymbolicLink);
        _fs.SetReparsePoint(node, data).Should().Be(NtStatus.Success);
        data[0] = 0;
        node.Attributes.Should().HaveFlag(FileAttributes.ReparsePoint);
        node.Attributes = FileAttributes.Hidden; // SetBasicInfo cannot clear the reparse flag.
        node.Attributes.Should().HaveFlag(FileAttributes.ReparsePoint);
        _fs.GetReparsePoint(@"\link", null, out var first).Should().Be(NtStatus.Success);
        first![0] = 0;
        _fs.GetReparsePoint(@"\link", null, out var second).Should().Be(NtStatus.Success);
        ReparseData.Tag(second!).Should().Be(ReparseData.SymbolicLink);
        _fs.DeleteReparsePoint(node, DeleteBuffer(ReparseData.MountPoint)).Should().Be(ReparseData.TagMismatch);
        _fs.DeleteReparsePoint(node, DeleteBuffer(ReparseData.SymbolicLink)).Should().Be(NtStatus.Success);
        node.Attributes.Should().NotHaveFlag(FileAttributes.ReparsePoint);
        _fs.GetReparsePoint(null, node, out _).Should().Be(NtStatus.NotAReparse);
    }

    [Fact]
    public void MalformedDataAndTagMismatch_AreRejectedWithoutMutation()
    {
        var dir = _fs.CreateDirectory(@"\dir")!;
        byte[] valid = Link(ReparseData.SymbolicLink);
        _fs.SetReparsePoint(dir, valid).Should().Be(NtStatus.Success);
        _fs.SetReparsePoint(dir, Link(ReparseData.MountPoint)).Should().Be(ReparseData.TagMismatch);
        byte[] bad = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(bad.AsSpan(8), 1);
        _fs.SetReparsePoint(dir, bad).Should().Be(ReparseData.InvalidData);
        bad = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(16), 2);
        _fs.SetReparsePoint(dir, bad).Should().Be(ReparseData.InvalidData);
        _fs.SetReparsePoint(dir, new byte[7]).Should().Be(ReparseData.InvalidData);
        _fs.GetReparsePoint(null, dir, out var stored).Should().Be(NtStatus.Success);
        stored.Should().Equal(valid);
        var file = _fs.CreateFile(@"\file")!;
        _fs.SetReparsePoint(file, Link(ReparseData.MountPoint)).Should().Be(NtStatus.InvalidParameter);
        file.Content!.Write(0, new byte[] { 1 }).Should().Be(1);
        _fs.SetReparsePoint(file, valid).Should().Be(ReparseData.InvalidData);
        _fs.GetReparsePoint(null, file, out _).Should().Be(NtStatus.NotAReparse);
    }

    [Fact]
    public void ConcurrentCreateChildAndSetLink_CannotBothSucceed()
    {
        for (int i = 0; i < 200; i++)
        {
            string path = @"\dir" + i;
            var dir = _fs.CreateDirectory(path)!;
            int status = 0;
            FileNode? child = null;
            Parallel.Invoke(
                () => status = _fs.SetReparsePoint(dir, Link(ReparseData.SymbolicLink)),
                () => child = _fs.CreateFile(path + @"\child"));
            if (child == null) status.Should().Be(NtStatus.Success);
            else status.Should().Be(NtStatus.DirectoryNotEmpty);
        }
    }

    [Theory]
    [InlineData("X:")]
    [InlineData("X:\\")]
    [InlineData(@"\\.\X:")]
    [InlineData(@"\\?\X:")]
    public void AbsoluteSameDriveTarget_IsMadeVolumeRelativeForResolution(string mountPoint)
    {
        byte[] data = Link(ReparseData.SymbolicLink, @"\??\X:\target");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0);
        byte[] resolved = ReparseData.ForResolution(data, null, mountPoint);
        ReparseData.Tag(resolved).Should().Be(ReparseData.SymbolicLink);
        BinaryPrimitives.ReadUInt32LittleEndian(resolved.AsSpan(16)).Should().Be(1u);
        Encoding.Unicode.GetString(resolved, 20, resolved.Length - 20).Should().Be(@"\target");

        byte[] root = Link(ReparseData.SymbolicLink, @"\??\X:\");
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(16), 0);
        byte[] resolvedRoot = ReparseData.ForResolution(root, null, mountPoint);
        Encoding.Unicode.GetString(resolvedRoot, 20, resolvedRoot.Length - 20).Should().Be(@"\.");

        byte[] external = Link(ReparseData.SymbolicLink, @"\??\Y:\target");
        BinaryPrimitives.WriteUInt32LittleEndian(external.AsSpan(16), 0);
        ReparseData.ForResolution(external, null, mountPoint).Should().BeSameAs(external);
    }
}
