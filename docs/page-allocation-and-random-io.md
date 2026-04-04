# Page Allocation & Random I/O Design

This document explains how RamDrive allocates fixed-size 64KB pages and supports random read/write at arbitrary byte offsets.

## Overview

RamDrive stores all file data in **native memory** (outside the .NET GC heap) using a **page pool** architecture. Each file's content is backed by a **page table** — an array of pointers to fixed-size pages — enabling O(1) random access to any byte offset.

```
User I/O request (read/write at offset + length)
    │
    │  offset → pageIndex = offset / pageSize
    │           pageOffset = offset % pageSize
    ▼
PagedFileContent (nint[] _pages — per-file page table)
    │
    │  _pages[pageIndex] → native memory pointer
    ▼
PagePool (ConcurrentStack<nint> free list + NativeMemory)
    │
    ▼
OS Native Memory (NativeMemory.AllocZeroed)
```

## Page Pool (`PagePool.cs`)

The `PagePool` manages a fixed-capacity pool of identically-sized memory pages.

### Allocation Strategy

```
┌─────────────────────────────────────────────────────────┐
│                     PagePool                            │
│                                                         │
│  Configuration:                                         │
│    pageSize = PageSizeKb × 1024  (default: 65536)      │
│    maxPages = CapacityMb × 1M / pageSize                │
│                                                         │
│  ┌───────────────────────┐   ┌───────────────────────┐  │
│  │  ConcurrentStack      │   │  ConcurrentStack      │  │
│  │  _freePages (LIFO)    │   │  _allPages (cleanup)  │  │
│  │  ┌──┬──┬──┬──┐        │   │  tracks every alloc   │  │
│  │  │p5│p3│p1│..│        │   │  for Dispose()        │  │
│  │  └──┴──┴──┴──┘        │   └───────────────────────┘  │
│  └───────────────────────┘                              │
│                                                         │
│  Counters:                                              │
│    _allocatedCount  (total pages allocated from OS)     │
│    _rentedCount     (pages currently in use)            │
└─────────────────────────────────────────────────────────┘
```

### Two Allocation Modes

1. **Lazy (default, `PreAllocate=false`)**: Pages are allocated from the OS on first demand via `NativeMemory.AllocZeroed`. The CAS loop in `AllocateNewPageIfUnderCapacity` ensures thread-safe capacity enforcement without locks.

2. **Pre-allocate (`PreAllocate=true`)**: All pages are allocated at startup and pushed onto `_freePages`. Subsequent `Rent` calls only pop from the stack — no OS calls on the hot path.

### Rent/Return Flow

```
Rent():
  1. Try _freePages.TryPop()          ← lock-free CAS, O(1)
  2. If empty → AllocateNewPageIfUnderCapacity()
     └─ CAS loop on _allocatedCount   ← ensures total ≤ maxPages
     └─ NativeMemory.AllocZeroed()    ← OS allocation (only first time)
  3. Return nint.Zero if capacity exhausted

Return(page):
  1. NativeMemory.Clear(page)          ← zero the page (security + correctness)
  2. _freePages.Push(page)             ← back to free list

RentBatch(buffer, count):
  1. _freePages.TryPopRange()          ← single CAS for multiple pages
  2. Allocate remainder if needed

ReturnBatch(pages, count):
  1. Clear all pages
  2. _freePages.PushRange()            ← single CAS for multiple pages
```

### CAS-Based Capacity Enforcement

Instead of using a lock, `AllocateNewPageIfUnderCapacity` uses a compare-and-swap loop:

```csharp
long current = Volatile.Read(ref _allocatedCount);
while (current < _maxPages)
{
    long next = Interlocked.CompareExchange(ref _allocatedCount, current + 1, current);
    if (next == current)  // Won the race
    {
        return AllocateNativePage();  // Safe to allocate
    }
    current = next;  // Lost the race, retry with updated value
}
return nint.Zero;  // Capacity exhausted
```

This is lock-free and scales well under concurrent access.

## Paged File Content (`PagedFileContent.cs`)

Each file has a `PagedFileContent` object that maps byte offsets to pages.

### Page Table Structure

```
File: "data.bin" (150,000 bytes written)

_length = 150000
_pages[] (nint array — page table):

Index:   [0]        [1]        [2]
Pointer: 0x7F...A0  0x7F...B0  0x7F...C0
         │          │          │
         ▼          ▼          ▼
         ┌──────┐   ┌──────┐   ┌──────┐
         │64 KB │   │64 KB │   │64 KB │
         │page 0│   │page 1│   │page 2│
         └──────┘   └──────┘   └──────┘

Byte 0─65535 → page 0
Byte 65536─131071 → page 1
Byte 131072─150000 → page 2 (partial, rest is zero)
```

### Random Read at Arbitrary Offset

To read `N` bytes starting at byte offset `O`:

```
pageIndex  = O / pageSize       ← which page
pageOffset = O % pageSize       ← offset within that page
chunkSize  = min(N, pageSize - pageOffset)  ← bytes available in this page

Copy chunkSize bytes from:
  _pages[pageIndex] + pageOffset  →  destination buffer

If chunkSize < N:
  advance to next page, repeat
```

**Example**: Read 100 bytes at offset 65500 (page size = 65536):

```
Step 1: pageIndex=0, pageOffset=65500, chunkSize=min(100, 36)=36
  → copy 36 bytes from page 0, offset 65500

Step 2: pageIndex=1, pageOffset=0, chunkSize=min(64, 65536)=64
  → copy 64 bytes from page 1, offset 0

Total: 36 + 64 = 100 bytes ✓
```

### Random Write — Three-Phase Protocol

Write operations use a three-phase approach to minimize write-lock hold time:

```
Phase 1 — Read Lock: Scan page table
  ┌─────────────────────────────────────────┐
  │ Identify which pages need allocation    │
  │ (i.e., _pages[i] == nint.Zero)         │
  │ Count = neededCount                     │
  └─────────────────────────────────────────┘
             │
             ▼
Phase 2 — No Lock: Batch allocate from PagePool
  ┌─────────────────────────────────────────┐
  │ pool.RentBatch(buffer, neededCount)     │
  │ OS allocation happens here,             │
  │ outside any file lock                   │
  └─────────────────────────────────────────┘
             │
             ▼
Phase 3 — Write Lock: Assign pages + memcpy
  ┌─────────────────────────────────────────┐
  │ For each chunk:                         │
  │   if _pages[idx] == Zero:               │
  │     _pages[idx] = preAllocated[j++]     │
  │   memcpy: source → page + offset        │
  │ Update _length if extended              │
  └─────────────────────────────────────────┘
```

**Why three phases?** Phase 2 (the expensive part — OS memory allocation) runs without holding any lock. The write lock in phase 3 only does pointer assignments and `memcpy`, which are fast.

### Sparse File Support

Pages are allocated **on demand**. If you write to offset 1,000,000 without writing offsets 0–999,999, only the page(s) covering offset 1,000,000 are allocated:

```
_pages[]:  [Zero] [Zero] ... [Zero] [0x7F..] [Zero] ...
            │      │           │      │
            ▼      ▼           ▼      ▼
           (not   (not       (not   (allocated —
          allocated)         allocated) data here)
```

Reading from an unallocated page returns zeroes — the same behavior as a sparse file on NTFS.

### Truncation

`SetLength(newLength)` when shrinking:
1. Zero the partial data in the last retained page (security)
2. Collect all pages beyond `newLength`
3. `ReturnBatch` them to the pool (zeroed on return)
4. Shrink the `_pages[]` array

### Locking Model

```
Per-file ReaderWriterLockSlim:

  Read():     EnterReadLock    ← concurrent reads don't block each other
  Write():    Phase 1 = EnterReadLock (scan)
              Phase 2 = no lock (allocate)
              Phase 3 = EnterWriteLock (assign + copy)
  SetLength(): EnterWriteLock

Global _structureLock (in RamFileSystem):
  CreateFile/CreateDirectory/Delete/Move
  ← only structural operations, not I/O
```

## End-to-End: A Random Write Example

Writing "Hello" at offset 70,000 in a new file:

```
1. DokanRamAdapter.WriteFile(offset=70000, data="Hello")
     │
2.   node.Content.Write(70000, "Hello")
     │
3.   Phase 1 (read lock):
     │  pageIndex 0 (offset 0-65535)    → need? yes (Zero)
     │  pageIndex 1 (offset 65536-...)  → need? yes (Zero)
     │  neededCount = 2
     │
4.   Phase 2 (no lock):
     │  pool.RentBatch([p0, p1], 2)
     │  ← allocates 2 × 64KB from native memory
     │
5.   Phase 3 (write lock):
     │  _pages[0] = p0    (but we don't write to page 0)
     │  _pages[1] = p1
     │
     │  Chunk 1: pageIndex=1, pageOffset=4464, chunkSize=5
     │  memcpy "Hello" → p1 + 4464
     │
     │  _length = 70005
     │
6. Done — 2 pages allocated, 5 bytes written across page boundary
```

Note: Page 0 is allocated even though no data is written to it. This is because the write spans from page 0's range to page 1. The page table index calculation `(offset / pageSize)` to `((offset + length - 1) / pageSize)` determines which pages are touched. In this case `70000 / 65536 = 1` and `70004 / 65536 = 1`, so actually only page 1 is allocated. Page 0 remains sparse (Zero).

## Performance Characteristics

| Operation | Complexity | Lock Held |
|-----------|-----------|-----------|
| Random read (single page) | O(1) | Read lock (shared) |
| Random read (cross-page) | O(pages touched) | Read lock (shared) |
| Random write (pages pre-allocated) | O(pages touched) | Write lock (exclusive, memcpy only) |
| Random write (new pages needed) | O(pages touched) | Phase 1: read, Phase 2: none, Phase 3: write |
| Page rent from free list | O(1) | Lock-free (CAS) |
| Page batch rent | O(1) amortized | Lock-free (single CAS via TryPopRange) |
| SetLength (truncate) | O(freed pages) | Write lock |
| SetLength (extend, sparse) | O(1) | Write lock |
