using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.Memory;

namespace RamDrive.Core.Tests;

public class PagePoolTests
{
    private static PagePool CreatePool(int capacityMb = 1, int pageSizeKb = 64, bool preAllocate = false)
    {
        var options = Options.Create(new RamDriveOptions
        {
            CapacityMb = capacityMb,
            PageSizeKb = pageSizeKb,
            PreAllocate = preAllocate
        });
        return new PagePool(options, NullLogger<PagePool>.Instance);
    }

    [Fact]
    public void Reserve_ShouldSucceed_WhenCapacityAvailable()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        // 1MB / 64KB = 16 pages
        pool.Reserve(10).Should().BeTrue();
        pool.ReservedCount.Should().Be(10);
        pool.FreeBytes.Should().Be((16 - 10) * 64 * 1024);
    }

    [Fact]
    public void Reserve_ShouldFail_WhenExceedsCapacity()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        pool.Reserve(17).Should().BeFalse();
        pool.ReservedCount.Should().Be(0);
    }

    [Fact]
    public void Reserve_ShouldSucceed_AfterPagesReturnedToFreeStack()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        // Rent all 16 pages
        var pages = new nint[16];
        int rented = pool.RentBatch(pages, 16);
        rented.Should().Be(16);

        // Return them all — they go to free stack, allocatedCount stays 16
        pool.ReturnBatch(pages, 16);
        pool.AllocatedCount.Should().Be(16);
        pool.RentedCount.Should().Be(0);

        // Reserve should succeed because rented + reserved <= maxPages
        // (Old bug: used allocatedCount which would make this fail)
        pool.Reserve(16).Should().BeTrue();
        pool.ReservedCount.Should().Be(16);
    }

    [Fact]
    public void Reserve_ShouldRespect_ExistingRentedAndReserved()
    {
        using var pool = CreatePool(capacityMb: 1, pageSizeKb: 64);
        // Rent 8 pages
        var pages = new nint[8];
        pool.RentBatch(pages, 8);

        // Reserve 4
        pool.Reserve(4).Should().BeTrue();

        // Try to reserve 5 more (8 + 4 + 5 = 17 > 16)
        pool.Reserve(5).Should().BeFalse();

        // Reserve 4 more (8 + 4 + 4 = 16 = capacity)
        pool.Reserve(4).Should().BeTrue();

        pool.ReturnBatch(pages, 8);
    }
}
