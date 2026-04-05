using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinFsp;

/// <summary>
/// Pool of fixed-size unmanaged memory blocks for async I/O operations.
/// Eliminates GC pressure and LOH fragmentation from buffer allocation on the hot path.
///
/// Each block is allocated via NativeMemory.AlignedAlloc for optimal memcpy performance.
/// Blocks are reused via a lock-free ConcurrentStack (same pattern as RamDrive's PagePool).
/// </summary>
internal sealed unsafe class UnmanagedBufferPool : IDisposable
{
    private readonly int _blockSize;
    private readonly System.Collections.Concurrent.ConcurrentStack<nint> _freeList = new();
    private readonly System.Collections.Concurrent.ConcurrentStack<nint> _allBlocks = new(); // for cleanup
    private int _disposed;

    /// <summary>Create a pool with the specified block size (aligned to 4096 bytes).</summary>
    public UnmanagedBufferPool(int blockSize = 64 * 1024)
    {
        // Round up to 4KB alignment boundary
        _blockSize = (blockSize + 4095) & ~4095;
    }

    /// <summary>Block size in bytes (4KB-aligned).</summary>
    public int BlockSize => _blockSize;

    /// <summary>
    /// Rent a block. Returns a native pointer to _blockSize bytes of aligned memory.
    /// The block is NOT zeroed (caller is responsible for writing before reading).
    /// </summary>
    public nint Rent()
    {
        if (_freeList.TryPop(out nint block))
            return block;

        // Allocate new block with 4KB alignment (page-aligned for optimal DMA/memcpy)
        nint newBlock = (nint)NativeMemory.AlignedAlloc((nuint)_blockSize, 4096);
        _allBlocks.Push(newBlock);
        return newBlock;
    }

    /// <summary>
    /// Rent a block and wrap it as Memory&lt;byte&gt; via MemoryManager.
    /// The returned IMemoryOwner must be disposed to return the block to the pool.
    /// </summary>
    public OwnedBuffer RentAsMemory(int requestedSize)
    {
        if (requestedSize > _blockSize)
            throw new ArgumentOutOfRangeException(nameof(requestedSize),
                $"Requested {requestedSize} bytes but pool block size is {_blockSize}");

        nint block = Rent();
        return new OwnedBuffer(this, block, requestedSize);
    }

    /// <summary>Return a block to the pool.</summary>
    public void Return(nint block)
    {
        if (block != 0)
            _freeList.Push(block);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        while (_allBlocks.TryPop(out nint block))
            NativeMemory.AlignedFree((void*)block);
    }

    /// <summary>
    /// An owned buffer from the pool. Dispose to return to pool.
    /// Implements MemoryManager&lt;byte&gt; so it can produce Memory&lt;byte&gt; backed by unmanaged memory.
    /// </summary>
    internal sealed class OwnedBuffer : MemoryManager<byte>
    {
        private readonly UnmanagedBufferPool _pool;
        private nint _block;
        private readonly int _length;

        internal OwnedBuffer(UnmanagedBufferPool pool, nint block, int length)
        {
            _pool = pool;
            _block = block;
            _length = length;
        }

        public nint Pointer => _block;

        public override Span<byte> GetSpan() => new((void*)_block, _length);

        public override MemoryHandle Pin(int elementIndex = 0)
            => new((byte*)_block + elementIndex);

        public override void Unpin() { }

        protected override void Dispose(bool disposing)
        {
            nint block = Interlocked.Exchange(ref _block, 0);
            if (block != 0)
                _pool.Return(block);
        }

        /// <summary>Return the buffer to the pool.</summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>Copy from this buffer into a Span (e.g., kernel buffer).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyTo(Span<byte> destination, int count)
        {
            new Span<byte>((void*)_block, count).CopyTo(destination);
        }

        /// <summary>Copy from a ReadOnlySpan into this buffer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyFrom(ReadOnlySpan<byte> source)
        {
            source.CopyTo(new Span<byte>((void*)_block, _length));
        }
    }
}
