using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using WinFsp.Native;
using WinFsp.Native.Interop;

namespace RamDrive.Benchmarks;

/// <summary>
/// Simulates the full FileSystemHost.OnRead/OnWrite call chain without the WinFsp kernel.
/// Includes: GCHandle resolution, HandleState lookup, NativeBufferMemory wrapping,
/// IFileSystem.ReadFile/WriteFile dispatch, and native buffer memcpy.
/// This isolates the .NET user-mode overhead from the kernel round-trip cost.
/// </summary>
[MemoryDiagnoser]
public unsafe class OnReadWriteBenchmark
{
    private const long FileSize = 256L * 1024 * 1024; // 256 MB

    [Params(4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288,
            1048576, 2097152, 4194304, 8388608, 12582912, 16777216)]
    public int BlockSize;

    // --- Simulated FileSystemHost state ---
    private IFileSystem _adapter = null!;
    private bool _synchronousIo;
    private GCHandle _selfHandle;        // simulates FileSystemHost storing itself
    private GCHandle _handleStateHandle; // simulates per-file HandleState
    private HandleState _handleState = null!;
    private FspFullContext _ctx;          // simulates native FspFullContext

    // --- Native buffer simulating kernel I/O buffer ---
    private nint _nativeBuffer;

    // --- Infrastructure ---
    private PagePool _pool = null!;
    private RamFileSystem _fs = null!;

    // --- Copied from FileSystemHost: NativeBufferMemory ---
    [ThreadStatic] private static NativeBufferMemory? t_nativeBuffer;

    private static NativeBufferMemory GetNativeBuffer(nint ptr, int length)
    {
        var buf = t_nativeBuffer ??= new NativeBufferMemory();
        buf.Reset(ptr, length);
        return buf;
    }

    private sealed class NativeBufferMemory : MemoryManager<byte>
    {
        private nint _ptr;
        private int _length;
        public void Reset(nint ptr, int length) { _ptr = ptr; _length = length; }
        public override Span<byte> GetSpan() => new((void*)_ptr, _length);
        public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)_ptr + elementIndex);
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { _ptr = 0; _length = 0; }
    }

    // --- Copied from FileSystemHost: HandleState ---
    private sealed class HandleState
    {
        public readonly FileOperationInfo Info = new();
        public string? FileName;
    }

    // --- Minimal IFileSystem implementation (same logic as WinFspRamAdapter) ---
    private sealed class MinimalAdapter : IFileSystem
    {
        private readonly RamFileSystem _fs;
        private readonly RamDriveOptions _options;

        public MinimalAdapter(RamFileSystem fs, RamDriveOptions options)
        {
            _fs = fs;
            _options = options;
        }

        public bool SynchronousIo => true;

        public int GetVolumeInfo(out ulong totalSize, out ulong freeSize, out string volumeLabel)
        {
            totalSize = (ulong)_fs.TotalBytes;
            freeSize = (ulong)_fs.FreeBytes;
            volumeLabel = _options.VolumeLabel;
            return NtStatus.Success;
        }

        public int GetFileSecurityByName(string fileName, out uint fileAttributes, ref byte[]? securityDescriptor)
        {
            var node = _fs.FindNode(fileName);
            if (node == null) { fileAttributes = 0; return NtStatus.ObjectNameNotFound; }
            fileAttributes = (uint)node.Attributes;
            securityDescriptor = null;
            return NtStatus.Success;
        }

        public ValueTask<CreateResult> CreateFile(string fileName, uint co, uint ga, uint fa, byte[]? sd, ulong alloc,
            FileOperationInfo info, CancellationToken ct)
        {
            var file = _fs.CreateFile(fileName);
            if (file == null) return new(CreateResult.Error(NtStatus.ObjectPathNotFound));
            if (alloc > 0) file.Content!.SetLength((long)alloc);
            info.Context = file;
            info.IsDirectory = false;
            return new(new CreateResult(NtStatus.Success, MakeFileInfo(file)));
        }

        public ValueTask<CreateResult> OpenFile(string fileName, uint co, uint ga,
            FileOperationInfo info, CancellationToken ct)
        {
            var node = _fs.FindNode(fileName);
            if (node == null) return new(CreateResult.Error(NtStatus.ObjectNameNotFound));
            info.Context = node;
            info.IsDirectory = node.IsDirectory;
            return new(new CreateResult(NtStatus.Success, MakeFileInfo(node)));
        }

        public ValueTask<ReadResult> ReadFile(string fileName, Memory<byte> buffer, ulong offset,
            FileOperationInfo info, CancellationToken ct)
        {
            var node = (FileNode)info.Context!;
            long fileLength = node.Content!.Length;
            if ((long)offset >= fileLength) return new(ReadResult.EndOfFile());
            int toRead = (int)Math.Min(buffer.Length, fileLength - (long)offset);
            int bytesRead = node.Content.Read((long)offset, buffer.Span[..toRead]);
            node.LastAccessTime = DateTime.UtcNow;
            return new(ReadResult.Success((uint)bytesRead));
        }

        public ValueTask<WriteResult> WriteFile(string fileName, ReadOnlyMemory<byte> buffer, ulong offset,
            bool writeToEndOfFile, bool constrainedIo,
            FileOperationInfo info, CancellationToken ct)
        {
            var node = (FileNode)info.Context!;
            long writeOffset = writeToEndOfFile ? node.Content!.Length : (long)offset;
            int written = node.Content!.Write(writeOffset, buffer.Span);
            if (written < 0) return new(WriteResult.Error(NtStatus.DiskFull));
            node.LastWriteTime = DateTime.UtcNow;
            return new(WriteResult.Success((uint)written, MakeFileInfo(node)));
        }

        public void Cleanup(string? fileName, FileOperationInfo info, CleanupFlags flags) { }
        public void Close(FileOperationInfo info) { info.Context = null; }

        public ValueTask<FsResult> GetFileInformation(string fileName, FileOperationInfo info, CancellationToken ct)
            => new(FsResult.Success(MakeFileInfo((FileNode)info.Context!)));

        public ValueTask<int> CanDelete(string fileName, FileOperationInfo info, CancellationToken ct)
            => new(NtStatus.Success);

        public ValueTask<ReadDirectoryResult> ReadDirectory(string fileName, string? pattern, string? marker,
            nint buffer, uint length, FileOperationInfo info, CancellationToken ct)
            => new(ReadDirectoryResult.Success(0));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static FspFileInfo MakeFileInfo(FileNode node) => new()
        {
            FileAttributes = (uint)node.Attributes,
            FileSize = (ulong)node.Size,
            AllocationSize = (ulong)node.Size,
            CreationTime = (ulong)node.CreationTime.ToFileTimeUtc(),
            LastAccessTime = (ulong)node.LastAccessTime.ToFileTimeUtc(),
            LastWriteTime = (ulong)node.LastWriteTime.ToFileTimeUtc(),
            ChangeTime = (ulong)node.LastWriteTime.ToFileTimeUtc(),
        };
    }

    [GlobalSetup]
    public void Setup()
    {
        var options = new RamDriveOptions { CapacityMb = 512, PageSizeKb = 64 };
        _pool = new PagePool(new OptionsWrapper<RamDriveOptions>(options), NullLogger<PagePool>.Instance);
        _fs = new RamFileSystem(_pool);

        var adapter = new MinimalAdapter(_fs, options);
        _adapter = adapter;
        _synchronousIo = adapter.SynchronousIo;

        // Simulate FileSystemHost storing itself in a GCHandle (like Self(fs) does)
        _selfHandle = GCHandle.Alloc(this);

        // Create file and pre-fill
        var fileNode = _fs.CreateFile(@"\bench.dat")!;
        var fillBuf = new byte[BlockSize];
        Random.Shared.NextBytes(fillBuf);
        for (long offset = 0; offset < FileSize; offset += BlockSize)
            fileNode.Content!.Write(offset, fillBuf);

        // Set up HandleState (like OnOpen does)
        _handleState = new HandleState
        {
            FileName = @"\bench.dat",
            Info = { Context = fileNode, IsDirectory = false }
        };

        // Simulate FspFullContext with GCHandle in UserContext2
        _handleStateHandle = GCHandle.Alloc(_handleState);
        _ctx = new FspFullContext { UserContext2 = (ulong)(nint)GCHandle.ToIntPtr(_handleStateHandle) };

        // Allocate native buffer simulating WinFsp kernel buffer
        _nativeBuffer = (nint)NativeMemory.AlignedAlloc((nuint)BlockSize, 4096);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        NativeMemory.AlignedFree((void*)_nativeBuffer);
        _handleStateHandle.Free();
        _selfHandle.Free();
        _fs.Dispose();
        _pool.Dispose();
    }

    /// <summary>
    /// Full OnRead simulation: Self() → H(ctx) → GetNativeBuffer → ReadFile → write pBt.
    /// </summary>
    [Benchmark]
    public long SequentialRead()
    {
        long totalRead = 0;
        nint buffer = _nativeBuffer;
        uint length = (uint)BlockSize;

        for (long offset = 0; offset < FileSize; offset += BlockSize)
        {
            // === Simulate OnRead exactly ===
            // var self = Self(fs);
            var self = (OnReadWriteBenchmark)_selfHandle.Target!;

            // var hs = H(ctx);
            var hs = (HandleState)GCHandle.FromIntPtr((nint)_ctx.UserContext2).Target!;

            // if (self._synchronousIo) { ... }
            var directBuf = GetNativeBuffer(buffer, (int)length);
            var task = self._adapter.ReadFile(hs.FileName!, directBuf.Memory, (ulong)offset,
                hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();

            uint bytesTransferred = r.BytesTransferred;
            // *pBt = r.BytesTransferred; (simulated)
            // return r.Status; (simulated)

            totalRead += bytesTransferred;
        }
        return totalRead;
    }

    /// <summary>
    /// Full OnWrite simulation: Self() → H(ctx) → GetNativeBuffer → WriteFile → write pBt + pFi.
    /// </summary>
    [Benchmark]
    public long SequentialWrite()
    {
        long totalWritten = 0;
        nint buffer = _nativeBuffer;
        uint length = (uint)BlockSize;

        for (long offset = 0; offset < FileSize; offset += BlockSize)
        {
            // === Simulate OnWrite exactly ===
            var self = (OnReadWriteBenchmark)_selfHandle.Target!;
            var hs = (HandleState)GCHandle.FromIntPtr((nint)_ctx.UserContext2).Target!;

            var directBuf = GetNativeBuffer(buffer, (int)length);
            var task = self._adapter.WriteFile(hs.FileName!, directBuf.Memory, (ulong)offset,
                false, false, hs.Info, hs.Info.CancellationToken);
            var r = task.IsCompletedSuccessfully ? task.Result : task.AsTask().GetAwaiter().GetResult();

            uint bytesTransferred = r.BytesTransferred;
            FspFileInfo fi = r.FileInfo;
            // *pBt = r.BytesTransferred; (simulated)
            // *pFi = r.FileInfo; (simulated)

            totalWritten += bytesTransferred;
        }
        return totalWritten;
    }
}
