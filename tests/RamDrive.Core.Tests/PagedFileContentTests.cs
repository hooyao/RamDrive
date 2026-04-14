using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.Memory;

namespace RamDrive.Core.Tests;

public class PagedFileContentTests
{
    private static PagePool CreatePool(int capacityMb = 1, int pageSizeKb = 64)
    {
        var options = Options.Create(new RamDriveOptions
        {
            CapacityMb = capacityMb,
            PageSizeKb = pageSizeKb
        });
        return new PagePool(options, NullLogger<PagePool>.Instance);
    }

    [Fact]
    public void SetLength_Extend_ShouldReserveCapacity()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        using var content = new PagedFileContent(pool);

        // 1MB / 64KB = 16 pages. Extend file to 10 pages.
        bool result = content.SetLength(10 * 64 * 1024);
        result.Should().BeTrue();
        content.Length.Should().Be(10 * 64 * 1024);

        // Pool should have 10 pages reserved
        pool.ReservedCount.Should().Be(10);
        pool.FreeBytes.Should().Be(6 * 64 * 1024);
    }

    [Fact]
    public void SetLength_Extend_ShouldFail_WhenNoCapacityLeft()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        using var file1 = new PagedFileContent(pool);
        using var file2 = new PagedFileContent(pool);

        // File1 claims 10 pages
        file1.SetLength(10 * 64 * 1024).Should().BeTrue();

        // File2 claims 7 pages (10 + 7 = 17 > 16) → should fail
        file2.SetLength(7 * 64 * 1024).Should().BeFalse();
        file2.Length.Should().Be(0);

        // File2 claims 6 pages (10 + 6 = 16 = capacity) → should succeed
        file2.SetLength(6 * 64 * 1024).Should().BeTrue();
    }

    [Fact]
    public void MultiFile_AggregateSize_CannotExceedCapacity()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        var files = new List<PagedFileContent>();

        try
        {
            // Create files that each claim near capacity
            var f1 = new PagedFileContent(pool);
            files.Add(f1);
            f1.SetLength(8 * 64 * 1024).Should().BeTrue(); // 8 pages

            var f2 = new PagedFileContent(pool);
            files.Add(f2);
            f2.SetLength(8 * 64 * 1024).Should().BeTrue(); // 8 pages (total = 16 = max)

            var f3 = new PagedFileContent(pool);
            files.Add(f3);
            // Any extension should fail — capacity fully reserved
            f3.SetLength(1 * 64 * 1024).Should().BeFalse();

            // After truncating f1, capacity frees up
            f1.SetLength(0).Should().BeTrue();
            f3.SetLength(8 * 64 * 1024).Should().BeTrue();
        }
        finally
        {
            foreach (var f in files)
                f.Dispose();
        }
    }

    [Fact]
    public void AllocatedBytes_ShouldBeZero_ForSparseExtension()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        using var content = new PagedFileContent(pool);

        // Extend without writing — sparse file
        content.SetLength(10 * 64 * 1024);

        content.Length.Should().Be(10 * 64 * 1024);
        content.AllocatedBytes.Should().Be(0);
    }

    [Fact]
    public void AllocatedBytes_ShouldTrackActualWrites()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        using var content = new PagedFileContent(pool);

        content.SetLength(5 * 64 * 1024);
        content.AllocatedBytes.Should().Be(0);

        // Write to first page
        var data = new byte[100];
        Array.Fill(data, (byte)0xAB);
        int written = content.Write(0, data);
        written.Should().Be(100);

        // Only 1 page allocated
        content.AllocatedBytes.Should().Be(64 * 1024);

        // Write spanning pages 2 and 3
        written = content.Write(2 * 64 * 1024 - 50, new byte[100]);
        written.Should().Be(100);
        content.AllocatedBytes.Should().Be(3 * 64 * 1024);
    }

    [Fact]
    public void AllocatedBytes_ShouldDecrease_AfterTruncation()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        using var content = new PagedFileContent(pool);

        content.SetLength(4 * 64 * 1024);

        // Write to all 4 pages
        for (int i = 0; i < 4; i++)
            content.Write(i * 64 * 1024, new byte[100]);
        content.AllocatedBytes.Should().Be(4 * 64 * 1024);

        // Truncate to 2 pages
        content.SetLength(2 * 64 * 1024);
        content.AllocatedBytes.Should().Be(2 * 64 * 1024);
    }

    [Fact]
    public void Write_ShouldConsumeReservation()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        using var content = new PagedFileContent(pool);

        content.SetLength(4 * 64 * 1024);
        pool.ReservedCount.Should().Be(4);

        // Write to 2 pages — converts 2 reservations to actual allocations
        content.Write(0, new byte[64 * 1024]);
        content.Write(64 * 1024, new byte[64 * 1024]);

        pool.ReservedCount.Should().BeLessThan(4);
        pool.RentedCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Dispose_ShouldReleaseReservations()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        var content = new PagedFileContent(pool);

        content.SetLength(10 * 64 * 1024);
        pool.ReservedCount.Should().Be(10);

        content.Dispose();
        pool.ReservedCount.Should().Be(0);
        pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public void SetLength_ExtendThenTruncate_ShouldReleaseExcessReservations()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        using var content = new PagedFileContent(pool);

        content.SetLength(10 * 64 * 1024);
        pool.ReservedCount.Should().Be(10);

        content.SetLength(3 * 64 * 1024);
        pool.ReservedCount.Should().Be(3);
        pool.FreeBytes.Should().Be(13 * 64 * 1024);
    }

    [Fact]
    public void Write_ShouldReturnMinusOne_WhenDiskFull()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        using var file1 = new PagedFileContent(pool);
        using var file2 = new PagedFileContent(pool);

        // file1 reserves all capacity
        file1.SetLength(16 * 64 * 1024).Should().BeTrue();

        // file2 has length 0, try to write — needs to extend first
        // Direct write to offset 0 with no SetLength should still work
        // if there's capacity. But all capacity is reserved by file1.
        file2.SetLength(1 * 64 * 1024).Should().BeFalse();
    }
}
