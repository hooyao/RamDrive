using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Win32.SafeHandles;

namespace RamDrive.IntegrationTests;

[Collection("RamDrive")]
[SupportedOSPlatform("windows")]
public sealed class FileObjectLifetimeTests(RamDriveFixture fixture)
{
    private readonly RamDriveFixture _testFixture = fixture.ResetForTest();
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletedMapping_FlushesAndRetainsItsData_AfterFileHandleCloses(bool recreateName)
    {
        string path = Path.Combine(_testFixture.Root, $"mapped_lifetime_{Guid.NewGuid():N}.tmp");
        const int length = 2 * 1024 * 1024;
        var expected = new byte[length];
        for (int i = 0; i < expected.Length; i++) expected[i] = (byte)(i * 17 + 3);

        using var file = MappingNative.CreateFile(path, 0xC0000000, 7, 0, 2, 0x04000100, 0);
        if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile");
        Check(MappingNative.SetFilePointerEx(file, length - 1, out _, 0), "SetFilePointerEx");
        Check(MappingNative.WriteFile(file, new byte[1], 1, out _, 0), "WriteFile");
        using var section = MappingNative.CreateFileMapping(file, 0, 4, 0, 0, null);
        if (section.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileMapping");
        nint view = MappingNative.MapViewOfFile(section, 2, 0, 0, length);
        if (view == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "MapViewOfFile");
        try
        {
            Marshal.Copy(expected, 0, view, expected.Length);
            // Keep only the view: Cleanup/Delete occurs before its late paging writes.
            section.Dispose();
            file.Dispose();
            File.Exists(path).Should().BeFalse();
            if (recreateName) File.WriteAllBytes(path, [91, 92, 93]);

            Check(MappingNative.FlushViewOfFile(view, length), "FlushViewOfFile after Cleanup");
            var actual = new byte[length];
            Marshal.Copy(view, actual, 0, actual.Length);
            actual.Should().Equal(expected);
            if (recreateName) File.ReadAllBytes(path).Should().Equal(new byte[] { 91, 92, 93 });
        }
        finally
        {
            MappingNative.UnmapViewOfFile(view);
            if (recreateName && File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReplacedDestination_WithCachedClosedHandle_ResolvesToSourceData()
    {
        string source = Path.Combine(_testFixture.Root, $"replace_source_{Guid.NewGuid():N}");
        string target = Path.Combine(_testFixture.Root, $"replace_target_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(source, [11, 12, 13]);
            File.WriteAllBytes(target, [21, 22, 23]);
            // Warm a closed handle's cache. The configured WinFsp rename policy
            // rejects a still-user-open destination before calling the adapter;
            // retained open destination contexts are covered at callback level.
            File.ReadAllBytes(target).Should().Equal(new byte[] { 21, 22, 23 });
            File.Move(source, target, overwrite: true);
            File.Exists(source).Should().BeFalse();
            File.ReadAllBytes(target).Should().Equal(new byte[] { 11, 12, 13 });
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (File.Exists(target)) File.Delete(target);
        }
    }

    private static void Check(bool success, string operation)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }
}

internal static partial class MappingNative
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFilePointerEx(SafeFileHandle file, long offset, out long position, uint origin);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WriteFile(SafeFileHandle file, byte[] buffer, uint length, out uint written, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileMappingW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFileMapping(SafeFileHandle file, nint security, uint protection,
        uint highSize, uint lowSize, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint MapViewOfFile(SafeFileHandle section, uint access, uint offsetHigh, uint offsetLow, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushViewOfFile(nint view, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnmapViewOfFile(nint view);
}
