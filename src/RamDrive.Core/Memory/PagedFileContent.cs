using System.Runtime.InteropServices;

namespace RamDrive.Core.Memory;

/// <summary>
/// File content stored as a page table: an array of native page pointers.
/// Supports sparse allocation — only written pages consume memory.
/// Thread-safe via ReaderWriterLockSlim.
/// </summary>
public sealed class PagedFileContent : IDisposable
{
    private readonly PagePool _pool;
    private readonly int _pageSize;
    private nint[] _pages;      // index → native page pointer; nint.Zero = not allocated
    private long _length;       // logical file size in bytes
    private int _reservedPages; // pages reserved in pool but not yet allocated
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private bool _disposed;

    public long Length
    {
        get
        {
            _lock.EnterReadLock();
            try { return _length; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public PagedFileContent(PagePool pool)
    {
        _pool = pool;
        _pageSize = pool.PageSize;
        _pages = [];
        _length = 0;
    }

    /// <summary>
    /// Read data from the file into the destination span. Returns bytes actually read.
    /// Unallocated pages read as zeroes.
    /// </summary>
    public unsafe int Read(long offset, Span<byte> destination)
    {
        _lock.EnterReadLock();
        try
        {
            if (offset >= _length) return 0;

            int toRead = (int)Math.Min(destination.Length, _length - offset);
            int totalRead = 0;

            while (totalRead < toRead)
            {
                int pageIndex = (int)((offset + totalRead) / _pageSize);
                int pageOffset = (int)((offset + totalRead) % _pageSize);
                int chunkSize = Math.Min(toRead - totalRead, _pageSize - pageOffset);

                if (pageIndex < _pages.Length && _pages[pageIndex] != nint.Zero)
                {
                    new Span<byte>((byte*)_pages[pageIndex] + pageOffset, chunkSize)
                        .CopyTo(destination.Slice(totalRead, chunkSize));
                }
                else
                {
                    // Sparse: unallocated page reads as zeroes
                    destination.Slice(totalRead, chunkSize).Clear();
                }

                totalRead += chunkSize;
            }

            return totalRead;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Write data to the file from the source span. Returns bytes written, or -1 if out of disk space.
    /// Pages are pre-allocated outside the write lock to minimize lock hold time.
    /// </summary>
    public unsafe int Write(long offset, ReadOnlySpan<byte> source)
    {
        if (source.Length == 0) return 0;

        long endOffset = offset + source.Length;
        int requiredPages = (int)((endOffset + _pageSize - 1) / _pageSize);

        // --- Phase 1: determine which pages need allocation (read lock only) ---
        int neededCount = 0;
        _lock.EnterReadLock();
        try
        {
            int firstPage = (int)(offset / _pageSize);
            int lastPage = (int)((endOffset - 1) / _pageSize);
            for (int i = firstPage; i <= lastPage; i++)
            {
                if (i >= _pages.Length || _pages[i] == nint.Zero)
                    neededCount++;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // --- Phase 2: batch-allocate pages outside any lock ---
        // Unreserve own reservations first so AllocateNewPageIfUnderCapacity
        // can use that capacity. Verified correct by TLA+ model (tla/PagePoolFixed.tla).
        nint[]? preAllocated = null;
        int preAllocatedCount = 0;
        int unreservedForAlloc = 0;
        if (neededCount > 0)
        {
            int currentReserved = Volatile.Read(ref _reservedPages);
            if (currentReserved > 0)
            {
                unreservedForAlloc = Math.Min(currentReserved, neededCount);
                _pool.Unreserve(unreservedForAlloc);
                Interlocked.Add(ref _reservedPages, -unreservedForAlloc);
            }

            preAllocated = new nint[neededCount];
            preAllocatedCount = _pool.RentBatch(preAllocated, neededCount);
            if (preAllocatedCount < neededCount)
            {
                // Not enough capacity — return what we got, restore reservations, and fail
                if (preAllocatedCount > 0)
                    _pool.ReturnBatch(preAllocated, preAllocatedCount);
                if (unreservedForAlloc > 0)
                {
                    _pool.Reserve(unreservedForAlloc);
                    Interlocked.Add(ref _reservedPages, unreservedForAlloc);
                }
                return -1;
            }
        }

        // --- Phase 3: write lock — only page table + memcpy, no OS allocations ---
        // Reservations already consumed in Phase 2 via Unreserve.
        _lock.EnterWriteLock();
        try
        {
            if (requiredPages > _pages.Length)
                Array.Resize(ref _pages, requiredPages);

            int preAllocIdx = 0;
            int totalWritten = 0;

            while (totalWritten < source.Length)
            {
                int pageIndex = (int)((offset + totalWritten) / _pageSize);
                int pageOffset = (int)((offset + totalWritten) % _pageSize);
                int chunkSize = Math.Min(source.Length - totalWritten, _pageSize - pageOffset);

                if (_pages[pageIndex] == nint.Zero)
                {
                    // Use pre-allocated page
                    if (preAllocated != null && preAllocIdx < preAllocatedCount)
                    {
                        _pages[pageIndex] = preAllocated[preAllocIdx++];
                    }
                    else
                    {
                        // Race condition: another thread allocated between our read-lock scan
                        // and write-lock acquisition. Fall back to single Rent.
                        // Unreserve one slot if available so Rent can succeed.
                        int curReserved = Volatile.Read(ref _reservedPages);
                        if (curReserved > 0)
                        {
                            _pool.Unreserve(1);
                            Interlocked.Decrement(ref _reservedPages);
                        }
                        nint page = _pool.Rent();
                        if (page == nint.Zero)
                        {
                            // Return unused pre-allocated pages
                            if (preAllocated != null && preAllocIdx < preAllocatedCount)
                                _pool.ReturnBatch(preAllocated[preAllocIdx..], preAllocatedCount - preAllocIdx);
                            return totalWritten > 0 ? totalWritten : -1;
                        }
                        _pages[pageIndex] = page;
                    }
                }

                source.Slice(totalWritten, chunkSize)
                    .CopyTo(new Span<byte>((byte*)_pages[pageIndex] + pageOffset, chunkSize));

                totalWritten += chunkSize;
            }

            // Return any excess pre-allocated pages (rare: another thread filled gaps between phases)
            if (preAllocated != null && preAllocIdx < preAllocatedCount)
                _pool.ReturnBatch(preAllocated[preAllocIdx..], preAllocatedCount - preAllocIdx);

            if (endOffset > _length)
                _length = endOffset;

            return totalWritten;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Set logical file length. Truncation frees pages beyond the new length.
    /// Extension reserves capacity in the pool (without allocating pages) to guarantee
    /// that subsequent writes will succeed — required for kernel cache mode where the
    /// cache manager calls SetFileSize before issuing writes.
    /// Returns false if extending would exceed capacity.
    /// </summary>
    public bool SetLength(long newLength)
    {
        if (newLength < 0) return false;

        _lock.EnterWriteLock();
        try
        {
            if (newLength < _length)
            {
                // --- Truncation ---
                int newPageCount = (int)((newLength + _pageSize - 1) / _pageSize);

                // Zero partial data in the last retained page
                if (newLength > 0 && newLength % _pageSize != 0 && newPageCount > 0)
                {
                    int lastPageIndex = newPageCount - 1;
                    if (lastPageIndex < _pages.Length && _pages[lastPageIndex] != nint.Zero)
                    {
                        int keepBytes = (int)(newLength % _pageSize);
                        unsafe
                        {
                            NativeMemory.Clear(
                                (byte*)_pages[lastPageIndex] + keepBytes,
                                (nuint)(_pageSize - keepBytes));
                        }
                    }
                }

                // Collect pages to free, then batch-return
                int toFreeCount = 0;
                for (int i = newPageCount; i < _pages.Length; i++)
                {
                    if (_pages[i] != nint.Zero)
                        toFreeCount++;
                }

                if (toFreeCount > 0)
                {
                    nint[] toFree = new nint[toFreeCount];
                    int idx = 0;
                    for (int i = newPageCount; i < _pages.Length; i++)
                    {
                        if (_pages[i] != nint.Zero)
                        {
                            toFree[idx++] = _pages[i];
                            _pages[i] = nint.Zero;
                        }
                    }
                    _pool.ReturnBatch(toFree, toFreeCount);
                }

                if (newPageCount < _pages.Length)
                    Array.Resize(ref _pages, newPageCount);

                // Release excess reservations
                int oldRequired = (int)((_length + _pageSize - 1) / _pageSize);
                int allocatedInRange = 0;
                for (int i = 0; i < Math.Min(oldRequired, _pages.Length); i++)
                {
                    if (_pages[i] != nint.Zero)
                        allocatedInRange++;
                }
                int newReserved = Math.Max(0, newPageCount - allocatedInRange);
                int reserveDelta = _reservedPages - newReserved;
                if (reserveDelta > 0)
                {
                    _pool.Unreserve(reserveDelta);
                    _reservedPages = newReserved;
                }
            }
            else if (newLength > _length)
            {
                // --- Extension: reserve capacity without allocating ---
                int oldPageCount = (int)((_length + _pageSize - 1) / _pageSize);
                int newPageCount = (int)((newLength + _pageSize - 1) / _pageSize);
                int additionalPages = newPageCount - oldPageCount;

                if (additionalPages > 0)
                {
                    if (!_pool.Reserve(additionalPages))
                        return false;
                    _reservedPages += additionalPages;
                }

                // Expand page table (entries stay nint.Zero — sparse)
                if (newPageCount > _pages.Length)
                    Array.Resize(ref _pages, newPageCount);
            }

            _length = newLength;
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _lock.EnterWriteLock();
        try
        {
            // Release any outstanding reservations
            if (_reservedPages > 0)
            {
                _pool.Unreserve(_reservedPages);
                _reservedPages = 0;
            }

            for (int i = 0; i < _pages.Length; i++)
            {
                if (_pages[i] != nint.Zero)
                {
                    _pool.Return(_pages[i]);
                    _pages[i] = nint.Zero;
                }
            }
            _pages = [];
            _length = 0;
        }
        finally
        {
            _lock.ExitWriteLock();
            _lock.Dispose();
        }
    }
}
