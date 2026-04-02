using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;

namespace RamDrive.Core.Memory;

/// <summary>
/// Fixed-size page pool backed by NativeMemory. Lock-free rent/return via ConcurrentStack.
/// </summary>
public sealed class PagePool : IDisposable
{
    private readonly int _pageSize;
    private readonly long _maxPages;
    private readonly ConcurrentStack<nint> _freePages = new();
    private readonly ConcurrentStack<nint> _allPages = new(); // tracks every allocation for cleanup
    private long _allocatedCount; // total pages ever allocated from OS
    private long _rentedCount;    // pages currently in use (not on free stack)
    private volatile bool _disposed;

    public int PageSize => _pageSize;
    public long MaxPages => _maxPages;
    public long AllocatedCount => Volatile.Read(ref _allocatedCount);
    public long RentedCount => Volatile.Read(ref _rentedCount);
    public long FreeCount => AllocatedCount - RentedCount;
    public long CapacityBytes => _maxPages * _pageSize;
    public long UsedBytes => RentedCount * _pageSize;
    public long FreeBytes => CapacityBytes - UsedBytes;

    public PagePool(IOptions<RamDriveOptions> options, ILogger<PagePool> logger)
    {
        var opts = options.Value;
        _pageSize = opts.PageSizeKb * 1024;
        _maxPages = (opts.CapacityMb * 1024L * 1024L) / _pageSize;

        logger.LogInformation("PagePool: {PageSizeKb}KB pages, capacity {CapacityMb}MB ({MaxPages} pages), preAllocate={PreAllocate}",
            opts.PageSizeKb, opts.CapacityMb, _maxPages, opts.PreAllocate);

        if (opts.PreAllocate)
        {
            logger.LogInformation("PagePool: pre-allocating {Count} pages...", _maxPages);
            for (long i = 0; i < _maxPages; i++)
            {
                nint page = AllocateNativePage();
                _freePages.Push(page);
                Interlocked.Increment(ref _allocatedCount);
            }
            logger.LogInformation("PagePool: pre-allocation complete");
        }
    }

    /// <summary>
    /// Rent a zeroed page from the pool. Returns nint.Zero if capacity exhausted.
    /// </summary>
    public nint Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_freePages.TryPop(out nint page))
        {
            Interlocked.Increment(ref _rentedCount);
            return page;
        }

        return AllocateNewPageIfUnderCapacity();
    }

    /// <summary>
    /// Rent multiple pages in one batch. Returns actual count rented (may be less than requested
    /// if capacity exhausted). Uses TryPopRange for a single CAS operation on the free stack.
    /// </summary>
    public int RentBatch(nint[] buffer, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int total = 0;

        // Batch pop from free stack — single CAS for multiple pages
        if (count > 0)
        {
            int popped = _freePages.TryPopRange(buffer, 0, count);
            Interlocked.Add(ref _rentedCount, popped);
            total = popped;
        }

        // If free stack didn't have enough, allocate the rest
        while (total < count)
        {
            nint page = AllocateNewPageIfUnderCapacity();
            if (page == nint.Zero) break; // capacity exhausted
            buffer[total++] = page;
        }

        return total;
    }

    /// <summary>
    /// Return a page to the pool. The page is zeroed before returning to the free list.
    /// </summary>
    public unsafe void Return(nint page)
    {
        if (page == nint.Zero) return;

        NativeMemory.Clear((void*)page, (nuint)_pageSize);
        Interlocked.Decrement(ref _rentedCount);
        _freePages.Push(page);
    }

    /// <summary>
    /// Return multiple pages in one batch. Pages are zeroed, then pushed via PushRange (single CAS).
    /// </summary>
    public unsafe void ReturnBatch(nint[] pages, int count)
    {
        if (count <= 0) return;

        for (int i = 0; i < count; i++)
        {
            if (pages[i] != nint.Zero)
                NativeMemory.Clear((void*)pages[i], (nuint)_pageSize);
        }

        _freePages.PushRange(pages, 0, count);
        Interlocked.Add(ref _rentedCount, -count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_allPages.TryPop(out nint page))
        {
            unsafe { NativeMemory.Free((void*)page); }
        }
    }

    private nint AllocateNewPageIfUnderCapacity()
    {
        long current = Volatile.Read(ref _allocatedCount);
        while (current < _maxPages)
        {
            long next = Interlocked.CompareExchange(ref _allocatedCount, current + 1, current);
            if (next == current)
            {
                // Won the race — allocate from OS
                nint page = AllocateNativePage();
                Interlocked.Increment(ref _rentedCount);
                return page;
            }
            current = next;
        }
        return nint.Zero; // capacity exhausted
    }

    private unsafe nint AllocateNativePage()
    {
        void* ptr = NativeMemory.AllocZeroed((nuint)_pageSize);
        nint page = (nint)ptr;
        _allPages.Push(page);
        return page;
    }
}
