using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace WinFsp;

/// <summary>
/// High-performance tiered buffer pool using pinned managed arrays on the Pinned Object Heap (POH).
///
/// <para><b>Two-level caching:</b></para>
/// <list type="bullet">
///   <item><b>Thread-local fast path:</b> Each thread caches one buffer per tier.
///         Rent/Return on the same thread is a simple field read/write — zero CAS, zero contention.</item>
///   <item><b>Shared bounded pool:</b> When thread-local misses, falls back to a
///         <see cref="ConcurrentQueue{T}"/> with atomic count cap. Lock-free, MPMC-safe.</item>
///   <item><b>Overflow:</b> When the pool is full on Return, the buffer is dropped for GC.
///         When the pool is empty on Rent, a new pinned array is allocated.</item>
/// </list>
///
/// <para><b>Tier configuration:</b> Pass <see cref="Tier"/> array to constructor.
/// Tiers are auto-sorted by BlockSize. Requests exceeding the largest tier get a one-off
/// allocation (not pooled).</para>
/// </summary>
internal sealed class PinnedBufferPool : MemoryPool<byte>
{
    /// <summary>Defines one tier of the pool.</summary>
    /// <param name="BlockSize">Buffer size in bytes.</param>
    /// <param name="MaxPooled">Maximum buffers in the shared pool. Excess are left for GC.</param>
    public readonly record struct Tier(int BlockSize, int MaxPooled);

    /// <summary>Default tiers: 64KB/256KB/1MB/4MB — up to 30 MB total pooled memory.</summary>
    public static readonly Tier[] DefaultTiers =
    [
        new(64 * 1024,      32),  // 64 KB  × 32 =  2 MB
        new(256 * 1024,     16),  // 256 KB × 16 =  4 MB
        new(1024 * 1024,     8),  // 1 MB   ×  8 =  8 MB
        new(4 * 1024 * 1024, 4),  // 4 MB   ×  4 = 16 MB
    ];

    /// <summary>Minimal tiers for sync-only FS where the pool is rarely used.</summary>
    public static readonly Tier[] MinimalTiers =
    [
        new(64 * 1024, 2),   // 128 KB
        new(1024 * 1024, 1), //   1 MB
    ];

    private readonly int _tierCount;
    private readonly int[] _tierSizes;
    private readonly int[] _tierCaps;
    private readonly ConcurrentQueue<byte[]>[] _sharedPools;
    private readonly int[] _sharedCounts; // approximate count per tier (Interlocked)
    private readonly ThreadLocal<byte[]?[]> _threadCache;
    private int _disposed;

    /// <param name="tiers">
    /// Tier configuration. Auto-sorted by BlockSize ascending.
    /// If null, uses <see cref="DefaultTiers"/>.
    /// </param>
    public PinnedBufferPool(Tier[]? tiers = null)
    {
        tiers ??= DefaultTiers;
        var sorted = tiers.OrderBy(t => t.BlockSize).ToArray();
        _tierCount = sorted.Length;
        _tierSizes = new int[_tierCount];
        _tierCaps = new int[_tierCount];
        _sharedPools = new ConcurrentQueue<byte[]>[_tierCount];
        _sharedCounts = new int[_tierCount];
        for (int i = 0; i < _tierCount; i++)
        {
            _tierSizes[i] = sorted[i].BlockSize;
            _tierCaps[i] = sorted[i].MaxPooled;
            _sharedPools[i] = new ConcurrentQueue<byte[]>();
        }
        _threadCache = new ThreadLocal<byte[]?[]>(() => new byte[]?[_tierCount], trackAllValues: false);
    }

    public override int MaxBufferSize => int.MaxValue;

    // ═══════════════════════════════════════════
    //  Rent
    // ═══════════════════════════════════════════

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        if (minBufferSize <= 0) minBufferSize = _tierSizes[0];

        for (int i = 0; i < _tierCount; i++)
        {
            if (minBufferSize <= _tierSizes[i])
                return RentFromTier(i, minBufferSize);
        }

        // Oversized — one-off pinned array, not pooled
        byte[] oversized = GC.AllocateArray<byte>(minBufferSize, pinned: true);
        return new OwnedBuffer(this, -1, oversized, minBufferSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private OwnedBuffer RentFromTier(int tierIndex, int requestedLength)
    {
        byte[]? buffer = null;

        // Level 1: thread-local — zero cost, no CAS
        var cache = GetThreadCache();
        if (cache[tierIndex] != null)
        {
            buffer = cache[tierIndex];
            cache[tierIndex] = null;
        }

        // Level 2: shared pool — ConcurrentQueue.TryDequeue (lock-free, MPMC-safe)
        if (buffer == null && _sharedPools[tierIndex].TryDequeue(out buffer))
        {
            Interlocked.Decrement(ref _sharedCounts[tierIndex]);
        }

        // Level 3: allocate new
        buffer ??= GC.AllocateArray<byte>(_tierSizes[tierIndex], pinned: true);

        return new OwnedBuffer(this, tierIndex, buffer, requestedLength);
    }

    // ═══════════════════════════════════════════
    //  Return
    // ═══════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Return(int tierIndex, byte[] buffer)
    {
        if (tierIndex < 0 || Volatile.Read(ref _disposed) != 0)
            return; // oversized or disposed — let GC collect

        // Level 1: thread-local
        var cache = GetThreadCache();
        if (cache[tierIndex] == null)
        {
            cache[tierIndex] = buffer;
            return;
        }

        // Level 2: shared pool with cap
        if (Interlocked.Increment(ref _sharedCounts[tierIndex]) <= _tierCaps[tierIndex])
        {
            _sharedPools[tierIndex].Enqueue(buffer);
        }
        else
        {
            // Over cap — don't pool, let GC collect
            Interlocked.Decrement(ref _sharedCounts[tierIndex]);
        }
    }

    // ═══════════════════════════════════════════
    //  Thread-local cache
    // ═══════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[]?[] GetThreadCache() => _threadCache.Value!;

    // ═══════════════════════════════════════════
    //  Dispose
    // ═══════════════════════════════════════════

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        for (int i = 0; i < _tierCount; i++)
            while (_sharedPools[i].TryDequeue(out _)) { }
        _threadCache.Dispose();
    }

    // ═══════════════════════════════════════════
    //  OwnedBuffer — lightweight IMemoryOwner<byte>
    // ═══════════════════════════════════════════

    private sealed class OwnedBuffer : IMemoryOwner<byte>
    {
        private readonly PinnedBufferPool _pool;
        private readonly int _tierIndex; // -1 = oversized
        private byte[]? _buffer;
        private readonly int _length;

        public OwnedBuffer(PinnedBufferPool pool, int tierIndex, byte[] buffer, int length)
        {
            _pool = pool;
            _tierIndex = tierIndex;
            _buffer = buffer;
            _length = length;
        }

        public Memory<byte> Memory => new(_buffer!, 0, _length);

        public void Dispose()
        {
            byte[]? buf = Interlocked.Exchange(ref _buffer, null);
            if (buf != null)
                _pool.Return(_tierIndex, buf);
        }
    }
}
