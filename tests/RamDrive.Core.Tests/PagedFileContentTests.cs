using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.Memory;
using Xunit;

namespace RamDrive.Core.Tests;

/// <summary>
/// Tests for <see cref="PagedFileContent"/> — the per-file page table that supports
/// random read/write at arbitrary byte offsets over fixed-size 64KB pages.
/// </summary>
public sealed class PagedFileContentTests : IDisposable
{
    private const int PageSizeKb = 64;
    private const int PageSize = PageSizeKb * 1024; // 65536 bytes

    private readonly PagePool _pool;

    public PagedFileContentTests()
    {
        var options = Options.Create(new RamDriveOptions
        {
            CapacityMb = 4, // 4 MB = 64 pages of 64KB
            PageSizeKb = PageSizeKb,
        });
        _pool = new PagePool(options, NullLogger<PagePool>.Instance);
    }

    public void Dispose() => _pool.Dispose();

    // ==================== Basic Write/Read ====================

    [Fact]
    public void Write_ThenRead_SmallData_WithinSinglePage()
    {
        using var content = new PagedFileContent(_pool);

        byte[] data = [1, 2, 3, 4, 5];
        int written = content.Write(0, data);
        written.Should().Be(5);
        content.Length.Should().Be(5);

        var readBuf = new byte[5];
        int read = content.Read(0, readBuf);
        read.Should().Be(5);
        readBuf.Should().Equal(data);
    }

    [Fact]
    public void Write_ThenRead_ExactlyOnePage()
    {
        using var content = new PagedFileContent(_pool);

        var data = new byte[PageSize];
        FillPattern(data, 0xAB);

        int written = content.Write(0, data);
        written.Should().Be(PageSize);
        content.Length.Should().Be(PageSize);

        var readBuf = new byte[PageSize];
        int read = content.Read(0, readBuf);
        read.Should().Be(PageSize);
        readBuf.Should().Equal(data);
    }

    // ==================== Random Read/Write at Arbitrary Offsets ====================

    [Fact]
    public void RandomWrite_AtMiddleOfPage()
    {
        using var content = new PagedFileContent(_pool);

        // Write at an arbitrary offset inside the first page
        int offset = 1000;
        byte[] data = [0xDE, 0xAD, 0xBE, 0xEF];
        content.Write(offset, data);

        content.Length.Should().Be(offset + data.Length);

        var readBuf = new byte[4];
        content.Read(offset, readBuf);
        readBuf.Should().Equal(data);
    }

    [Fact]
    public void RandomWrite_AtArbitraryOffset_InSecondPage()
    {
        using var content = new PagedFileContent(_pool);

        // Write into the second page (offset >= 64KB)
        int offset = PageSize + 500;
        byte[] data = [0x11, 0x22, 0x33];
        content.Write(offset, data);

        content.Length.Should().Be(offset + data.Length);

        var readBuf = new byte[3];
        content.Read(offset, readBuf);
        readBuf.Should().Equal(data);
    }

    [Fact]
    public void RandomWrite_SpanningTwoPages()
    {
        using var content = new PagedFileContent(_pool);

        // Write data that starts near the end of page 0 and spans into page 1
        int offset = PageSize - 3; // 3 bytes before page boundary
        byte[] data = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF]; // 6 bytes crosses boundary
        content.Write(offset, data);

        content.Length.Should().Be(offset + data.Length);

        var readBuf = new byte[6];
        content.Read(offset, readBuf);
        readBuf.Should().Equal(data, "cross-page write/read should preserve all bytes");
    }

    [Fact]
    public void RandomWrite_SpanningThreePages()
    {
        using var content = new PagedFileContent(_pool);

        // Write starting in page 0, crossing page 1 entirely, ending in page 2
        int offset = PageSize - 10;
        int writeSize = PageSize + 20; // spans from page 0 into page 2
        var data = new byte[writeSize];
        FillPattern(data, 0x42);

        content.Write(offset, data);

        var readBuf = new byte[writeSize];
        content.Read(offset, readBuf);
        readBuf.Should().Equal(data, "write spanning 3 pages should be fully readable");
    }

    [Fact]
    public void MultipleRandomWrites_ToNonContiguousOffsets()
    {
        using var content = new PagedFileContent(_pool);

        // Write to page 0
        content.Write(100, new byte[] { 0x01 });
        // Write to page 3 (skipping pages 1 and 2)
        content.Write(3 * PageSize + 500, new byte[] { 0x03 });
        // Write to page 7
        content.Write(7 * PageSize + 200, new byte[] { 0x07 });

        // Verify each write
        var buf = new byte[1];
        content.Read(100, buf);
        buf[0].Should().Be(0x01);

        content.Read(3 * PageSize + 500, buf);
        buf[0].Should().Be(0x03);

        content.Read(7 * PageSize + 200, buf);
        buf[0].Should().Be(0x07);
    }

    [Fact]
    public void OverwriteExistingData_AtSameOffset()
    {
        using var content = new PagedFileContent(_pool);

        int offset = 2048;
        content.Write(offset, new byte[] { 0xAA, 0xBB, 0xCC });
        content.Write(offset, new byte[] { 0x11, 0x22, 0x33 });

        var buf = new byte[3];
        content.Read(offset, buf);
        buf.Should().Equal([0x11, 0x22, 0x33], "overwrite should replace previous data");
    }

    // ==================== Sparse Reads ====================

    [Fact]
    public void Read_UnallocatedPage_ReturnsZeroes()
    {
        using var content = new PagedFileContent(_pool);

        // Write to page 2, but don't touch page 0 or 1
        content.Write(2 * PageSize, new byte[] { 0xFF });

        // Read from page 0 (unallocated, but within file length? No — length is based on writes)
        // Actually, file length is 2*PageSize+1, so page 0 and 1 are within bounds
        var buf = new byte[10];
        content.Read(0, buf);
        buf.Should().AllBeEquivalentTo((byte)0, "unallocated sparse pages should read as zeroes");
    }

    [Fact]
    public void Read_BeyondFileLength_ReturnsZeroBytes()
    {
        using var content = new PagedFileContent(_pool);

        content.Write(0, new byte[] { 1, 2, 3 });

        var buf = new byte[10];
        int read = content.Read(100, buf); // offset beyond length
        read.Should().Be(0, "reading past file length returns 0 bytes");
    }

    [Fact]
    public void Read_PartiallyBeyondLength_ClampsToBounds()
    {
        using var content = new PagedFileContent(_pool);

        content.Write(0, new byte[] { 1, 2, 3, 4, 5 });

        var buf = new byte[10];
        int read = content.Read(3, buf); // offset 3, length 5, so only 2 bytes left
        read.Should().Be(2);
        buf[0].Should().Be(4);
        buf[1].Should().Be(5);
    }

    // ==================== SetLength / Truncation ====================

    [Fact]
    public void SetLength_Truncate_FreesPages()
    {
        using var content = new PagedFileContent(_pool);

        // Write 3 full pages
        var data = new byte[3 * PageSize];
        FillPattern(data, 0xAB);
        content.Write(0, data);

        long usedBefore = _pool.RentedCount;
        usedBefore.Should().Be(3);

        // Truncate to 1 page
        content.SetLength(PageSize).Should().BeTrue();
        content.Length.Should().Be(PageSize);

        _pool.RentedCount.Should().Be(1, "2 pages should have been freed by truncation");
    }

    [Fact]
    public void SetLength_Truncate_ZerosPartialPage()
    {
        using var content = new PagedFileContent(_pool);

        var data = new byte[PageSize];
        FillPattern(data, 0xFF);
        content.Write(0, data);

        // Truncate to half a page
        int halfPage = PageSize / 2;
        content.SetLength(halfPage);

        // Extend back without writing
        content.SetLength(PageSize);

        var readBuf = new byte[PageSize];
        content.Read(0, readBuf);

        // First half should have original data
        for (int i = 0; i < halfPage; i++)
            readBuf[i].Should().Be(0xFF, $"byte {i} in retained portion should be preserved");

        // Second half should be zero (cleared during truncation)
        for (int i = halfPage; i < PageSize; i++)
            readBuf[i].Should().Be(0, $"byte {i} beyond truncation should be zero");
    }

    [Fact]
    public void SetLength_Extend_DoesNotAllocate()
    {
        using var content = new PagedFileContent(_pool);

        content.SetLength(10 * PageSize);
        content.Length.Should().Be(10 * PageSize);
        _pool.RentedCount.Should().Be(0, "extending with SetLength does not allocate pages (sparse)");
    }

    [Fact]
    public void SetLength_ToZero_FreesAllPages()
    {
        using var content = new PagedFileContent(_pool);

        content.Write(0, new byte[2 * PageSize]);
        _pool.RentedCount.Should().Be(2);

        content.SetLength(0);
        content.Length.Should().Be(0);
        _pool.RentedCount.Should().Be(0);
    }

    // ==================== Capacity Exhaustion ====================

    [Fact]
    public void Write_ReturnsNegativeOne_WhenDiskFull()
    {
        using var content = new PagedFileContent(_pool);

        // Fill the entire pool (4 MB = 64 pages)
        var bigData = new byte[4 * 1024 * 1024];
        int written = content.Write(0, bigData);
        written.Should().Be(bigData.Length);

        // Try to write one more page
        int result = content.Write(bigData.Length, new byte[PageSize]);
        result.Should().Be(-1, "should return -1 when pool is exhausted");
    }

    // ==================== Empty Write ====================

    [Fact]
    public void Write_EmptySpan_ReturnsZero()
    {
        using var content = new PagedFileContent(_pool);

        int written = content.Write(0, ReadOnlySpan<byte>.Empty);
        written.Should().Be(0);
        content.Length.Should().Be(0);
    }

    // ==================== Concurrent Access ====================

    [Fact]
    public void ConcurrentReads_DoNotBlock()
    {
        using var content = new PagedFileContent(_pool);

        var data = new byte[PageSize];
        FillPattern(data, 0xBE);
        content.Write(0, data);

        const int threadCount = 8;
        var barrier = new Barrier(threadCount);
        var exceptions = new List<Exception>();

        var threads = Enumerable.Range(0, threadCount).Select(_ => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                var buf = new byte[PageSize];
                int read = content.Read(0, buf);
                read.Should().Be(PageSize);
                buf[0].Should().Be(0xBE);
            }
            catch (Exception ex)
            {
                lock (exceptions)
                    exceptions.Add(ex);
            }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        exceptions.Should().BeEmpty("concurrent reads should succeed without errors");
    }

    [Fact]
    public void ConcurrentRandomWrites_ToDistinctPages_Succeed()
    {
        using var content = new PagedFileContent(_pool);

        // Pre-extend so page table is sized
        content.SetLength(8 * PageSize);

        const int threadCount = 8;
        var barrier = new Barrier(threadCount);
        var exceptions = new List<Exception>();

        var threads = Enumerable.Range(0, threadCount).Select(i => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                long offset = i * PageSize;
                var data = new byte[100];
                FillPattern(data, (byte)(i + 1));
                content.Write(offset, data);
            }
            catch (Exception ex)
            {
                lock (exceptions)
                    exceptions.Add(ex);
            }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        exceptions.Should().BeEmpty();

        // Verify each thread's write
        for (int i = 0; i < threadCount; i++)
        {
            var buf = new byte[100];
            content.Read(i * PageSize, buf);
            buf[0].Should().Be((byte)(i + 1), $"thread {i}'s write should be preserved");
        }
    }

    // ==================== Page Boundary Edge Cases ====================

    [Fact]
    public void Write_ExactlyAtPageBoundary_AllocatesNewPage()
    {
        using var content = new PagedFileContent(_pool);

        // Write exactly to fill page 0
        content.Write(0, new byte[PageSize]);
        _pool.RentedCount.Should().Be(1);

        // Write one byte at the start of page 1
        content.Write(PageSize, new byte[] { 0xFF });
        _pool.RentedCount.Should().Be(2, "writing at page boundary allocates a new page");
    }

    [Fact]
    public void Read_CrossingPageBoundary_IsContiguous()
    {
        using var content = new PagedFileContent(_pool);

        // Fill last 2 bytes of page 0 and first 2 bytes of page 1
        content.Write(PageSize - 2, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

        var buf = new byte[4];
        content.Read(PageSize - 2, buf);
        buf.Should().Equal([0xAA, 0xBB, 0xCC, 0xDD],
            "reading across page boundary should return contiguous data");
    }

    // ==================== Helpers ====================

    private static void FillPattern(byte[] buffer, byte value)
    {
        Array.Fill(buffer, value);
    }
}
