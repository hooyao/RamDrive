using System.Buffers.Binary;
using System.Runtime.InteropServices;
using WinFsp.Native;

namespace RamDrive.Core.FileSystem;

/// <summary>Validation for the two Microsoft reparse buffer formats supported by RamDrive.</summary>
internal static class ReparseData
{
    internal const uint MountPoint = 0xA0000003;
    internal const uint SymbolicLink = 0xA000000C;
    internal const int InvalidData = unchecked((int)0xC0000278);
    internal const int InvalidTag = unchecked((int)0xC0000276);
    internal const int TagMismatch = unchecked((int)0xC0000277);
    private const int MaxSize = 16 * 1024;

    internal static uint Tag(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadUInt32LittleEndian(data);

    internal static int ValidateSet(ReadOnlySpan<byte> data, bool isDirectory)
    {
        if (data.Length < 8 || data.Length > MaxSize || U16(data, 4) != data.Length - 8)
            return InvalidData;
        uint tag = Tag(data);
        if (tag != MountPoint && tag != SymbolicLink) return InvalidTag;
        if (tag == MountPoint && !isDirectory) return NtStatus.InvalidParameter;
        int pathStart = tag == MountPoint ? 16 : 20;
        if (data.Length < pathStart) return InvalidData;
        if (tag == SymbolicLink && (BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) & ~1u) != 0)
            return InvalidData;
        int available = data.Length - pathStart;
        int subOffset = U16(data, 8), subLength = U16(data, 10);
        int printOffset = U16(data, 12), printLength = U16(data, 14);
        if (subLength == 0 || ((subOffset | subLength | printOffset | printLength) & 1) != 0 ||
            subOffset + subLength > available || printOffset + printLength > available)
            return InvalidData;
        if (tag == MountPoint && subLength > MaxSize - 20 &&
            MemoryMarshal.Cast<byte, char>(data.Slice(pathStart + subOffset, subLength))
                .StartsWith(@"\??\UNC\".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return InvalidData; // The synthetic symbolic-link response also needs a flags field.
        return NtStatus.Success;
    }

    internal static int ValidateDelete(ReadOnlySpan<byte> data)
    {
        if (data.Length != 8 || U16(data, 4) != 0) return InvalidData;
        uint tag = Tag(data);
        return tag == MountPoint || tag == SymbolicLink ? NtStatus.Success : InvalidTag;
    }

    // The Windows mount-point handler only accepts local-device targets. For UNC
    // junctions, route resolution through the symlink handler. Absolute symlinks
    // and junctions targeting this volume are made volume-relative so WinFsp can
    // resolve them in one pass and detect cycles. Stored data remains unchanged.
    internal static byte[] ForResolution(byte[] data, string? volumePrefix, string? mountPoint)
    {
        uint tag = Tag(data);
        if (tag != MountPoint && tag != SymbolicLink) return data;
        if (tag == SymbolicLink && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)) != 0) return data;
        int pathStart = tag == MountPoint ? 16 : 20;
        int offset = U16(data, 8), length = U16(data, 10);
        // Windows paths are UTF-16 code units, including unpaired surrogates;
        // an Encoding round trip would replace those and change the target.
        ReadOnlySpan<char> target = MemoryMarshal.Cast<byte, char>(data.AsSpan(pathStart + offset, length));
        bool relative = false;
        string? nativePrefix = null;
        if (!string.IsNullOrEmpty(volumePrefix))
            nativePrefix = @"\??\UNC" + volumePrefix.TrimEnd('\\');
        else if (!string.IsNullOrEmpty(mountPoint))
        {
            string drive = mountPoint.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                           mountPoint.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? mountPoint[4..] : mountPoint;
            if (drive.Length >= 2 && char.IsAsciiLetter(drive[0]) && drive[1] == ':' &&
                (drive.Length == 2 || (drive.Length == 3 && drive[2] == '\\')))
                nativePrefix = @"\??\" + drive[..2];
        }
        if (nativePrefix != null && target.StartsWith(nativePrefix.AsSpan(), StringComparison.OrdinalIgnoreCase) &&
            (target.Length == nativePrefix.Length || target[nativePrefix.Length] == '\\'))
        {
            target = target[nativePrefix.Length..];
            // A bare backslash plus a remaining component would produce a
            // doubled separator in WinFsp's native splice. Its dot handling
            // normalizes this root-relative spelling before returning.
            if (target.Length == 0 || target.SequenceEqual(@"\".AsSpan())) target = @"\.".AsSpan();
            relative = true;
        }
        if (!relative && (tag == SymbolicLink || !target.StartsWith(@"\??\UNC\".AsSpan(), StringComparison.OrdinalIgnoreCase))) return data;
        ReadOnlySpan<byte> substitute = MemoryMarshal.AsBytes(target);
        length = substitute.Length;
        byte[] link = new byte[20 + length];
        BinaryPrimitives.WriteUInt32LittleEndian(link, SymbolicLink);
        BinaryPrimitives.WriteUInt16LittleEndian(link.AsSpan(4), checked((ushort)(link.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(link.AsSpan(10), (ushort)length);
        if (relative) BinaryPrimitives.WriteUInt32LittleEndian(link.AsSpan(16), 1);
        substitute.CopyTo(link.AsSpan(20));
        return link;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
}
