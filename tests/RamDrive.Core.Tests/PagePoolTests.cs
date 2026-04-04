using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.Memory;
using Xunit;

namespace RamDrive.Core.Tests;

/// <summary>
/// Tests for <see cref="PagePool"/> — the fixed-size native page allocator.
/// Validates 64KB page allocation, batch operations, capacity enforcement, and disposal.
/// </summary>
public sealed class PagePoolTests : IDisposable
{
    private const int DefaultPageSizeKb = 64;
    private const long DefaultCapacityMb = 1; // 1 MB = 16 pages of 64KB

    private readonly PagePool _pool;

    public PagePoolTests()
    {
        _pool = CreatePool(DefaultCapacityMb, DefaultPageSizeKb);
    }

    public void Dispose() => _pool.Dispose();

    // ==================== Page Size ====================

    [Fact]
    public void PageSize_Is64KBByDefault()
    {
        _pool.PageSize.Should().Be(64 * 1024, "default page size is 64KB");
    }

    [Fact]
    public void MaxPages_CalculatedFromCapacityAndPageSize()
    {
        // 1 MB / 64KB = 16 pages
        _pool.MaxPages.Should().Be(16);
    }

    // ==================== Single Rent/Return ====================

    [Fact]
    public void Rent_ReturnsNonZeroPointer()
    {
        nint page = _pool.Rent();
        page.Should().NotBe(nint.Zero, "a rented page should have a valid native pointer");
        _pool.Return(page);
    }

    [Fact]
    public void Rent_IncrementsRentedCount()
    {
        _pool.RentedCount.Should().Be(0);

        nint page = _pool.Rent();
        _pool.RentedCount.Should().Be(1);

        _pool.Return(page);
        _pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public void Rent_LazyAllocates_OnlyWhenNeeded()
    {
        _pool.AllocatedCount.Should().Be(0, "no pages allocated before first rent");

        nint page = _pool.Rent();
        _pool.AllocatedCount.Should().Be(1, "one page allocated on first rent");

        _pool.Return(page);
        _pool.AllocatedCount.Should().Be(1, "returning a page does not deallocate it");
    }

    [Fact]
    public void Return_PutsPageBackOnFreeList()
    {
        nint page = _pool.Rent();
        _pool.FreeCount.Should().Be(0, "rented page is not free");

        _pool.Return(page);
        _pool.FreeCount.Should().Be(1, "returned page is back on free list");
    }

    [Fact]
    public unsafe void Rent_ReturnsZeroedMemory()
    {
        nint page = _pool.Rent();

        // Write non-zero data
        byte* ptr = (byte*)page;
        for (int i = 0; i < _pool.PageSize; i++)
            ptr[i] = 0xFF;

        _pool.Return(page); // Return zeroes the page

        // Re-rent — should get the same (zeroed) page from free list
        nint page2 = _pool.Rent();
        byte* ptr2 = (byte*)page2;
        for (int i = 0; i < _pool.PageSize; i++)
            ptr2[i].Should().Be(0, "re-rented page should be zeroed");

        _pool.Return(page2);
    }

    // ==================== Capacity Enforcement ====================

    [Fact]
    public void Rent_ReturnsZero_WhenCapacityExhausted()
    {
        var pages = new nint[_pool.MaxPages];
        for (int i = 0; i < _pool.MaxPages; i++)
        {
            pages[i] = _pool.Rent();
            pages[i].Should().NotBe(nint.Zero);
        }

        // One more should fail
        nint extra = _pool.Rent();
        extra.Should().Be(nint.Zero, "should return Zero when capacity exhausted");

        // Return all
        foreach (nint p in pages)
            _pool.Return(p);
    }

    [Fact]
    public void Rent_SucceedsAgain_AfterReturningPages()
    {
        var pages = new nint[_pool.MaxPages];
        for (int i = 0; i < _pool.MaxPages; i++)
            pages[i] = _pool.Rent();

        // Capacity exhausted
        _pool.Rent().Should().Be(nint.Zero);

        // Return one page
        _pool.Return(pages[0]);

        // Now can rent again
        nint newPage = _pool.Rent();
        newPage.Should().NotBe(nint.Zero, "should succeed after returning a page");
        _pool.Return(newPage);

        // Cleanup
        for (int i = 1; i < pages.Length; i++)
            _pool.Return(pages[i]);
    }

    // ==================== Batch Rent/Return ====================

    [Fact]
    public void RentBatch_RentsMultiplePagesAtOnce()
    {
        var buffer = new nint[4];
        int rented = _pool.RentBatch(buffer, 4);

        rented.Should().Be(4);
        _pool.RentedCount.Should().Be(4);

        foreach (nint p in buffer)
            p.Should().NotBe(nint.Zero);

        _pool.ReturnBatch(buffer, 4);
        _pool.RentedCount.Should().Be(0);
    }

    [Fact]
    public void RentBatch_ReturnsPartialCount_WhenCapacityInsufficient()
    {
        // Pool has 16 pages max; try to rent 20
        var buffer = new nint[20];
        int rented = _pool.RentBatch(buffer, 20);

        rented.Should().Be(16, "can only rent up to capacity");
        _pool.RentedCount.Should().Be(16);

        _pool.ReturnBatch(buffer, rented);
    }

    [Fact]
    public void ReturnBatch_ReturnsAllPages()
    {
        var buffer = new nint[8];
        _pool.RentBatch(buffer, 8);

        _pool.RentedCount.Should().Be(8);
        _pool.ReturnBatch(buffer, 8);
        _pool.RentedCount.Should().Be(0);
        _pool.FreeCount.Should().Be(8);
    }

    // ==================== Pre-allocation ====================

    [Fact]
    public void PreAllocate_AllocatesAllPagesAtStartup()
    {
        using var prePool = CreatePool(1, 64, preAllocate: true);

        prePool.AllocatedCount.Should().Be(16, "all 16 pages pre-allocated");
        prePool.RentedCount.Should().Be(0, "none rented yet");
        prePool.FreeCount.Should().Be(16, "all on free list");
    }

    // ==================== Capacity Reporting ====================

    [Fact]
    public void CapacityBytes_ReportsCorrectTotal()
    {
        _pool.CapacityBytes.Should().Be(1 * 1024 * 1024, "1 MB capacity");
    }

    [Fact]
    public void UsedBytes_And_FreeBytes_TrackCorrectly()
    {
        _pool.UsedBytes.Should().Be(0);
        _pool.FreeBytes.Should().Be(1 * 1024 * 1024);

        nint page = _pool.Rent();
        _pool.UsedBytes.Should().Be(64 * 1024);
        _pool.FreeBytes.Should().Be(1 * 1024 * 1024 - 64 * 1024);

        _pool.Return(page);
        _pool.UsedBytes.Should().Be(0);
        _pool.FreeBytes.Should().Be(1 * 1024 * 1024);
    }

    // ==================== Thread Safety ====================

    [Fact]
    public void ConcurrentRentReturn_DoesNotCorruptState()
    {
        using var pool = CreatePool(4, 64); // 4 MB = 64 pages

        const int threadCount = 8;
        const int opsPerThread = 100;
        var barrier = new Barrier(threadCount);
        var exceptions = new List<Exception>();

        var threads = Enumerable.Range(0, threadCount).Select(_ => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                for (int i = 0; i < opsPerThread; i++)
                {
                    nint page = pool.Rent();
                    if (page != nint.Zero)
                    {
                        Thread.Yield();
                        pool.Return(page);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (exceptions)
                    exceptions.Add(ex);
            }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        exceptions.Should().BeEmpty("no exceptions during concurrent rent/return");
        pool.RentedCount.Should().Be(0, "all pages returned after concurrent operations");
    }

    // ==================== Helper ====================

    private static PagePool CreatePool(long capacityMb, int pageSizeKb, bool preAllocate = false)
    {
        var options = Options.Create(new RamDriveOptions
        {
            CapacityMb = capacityMb,
            PageSizeKb = pageSizeKb,
            PreAllocate = preAllocate,
        });
        return new PagePool(options, NullLogger<PagePool>.Instance);
    }
}
