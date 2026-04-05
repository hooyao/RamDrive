using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RamDrive.Core.Configuration;
using RamDrive.Core.Memory;

namespace RamDrive.Benchmarks;

[MemoryDiagnoser]
public class PagedFileContentBenchmark
{
    private const long FileSize = 256L * 1024 * 1024; // 256 MB — same as ATTO

    // Same block sizes as ATTO (4 KB ~ 16 MB)
    [Params(4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288,
            1048576, 2097152, 4194304, 8388608, 12582912, 16777216)]
    public int BlockSize;

    private PagePool _pool = null!;
    private PagedFileContent _content = null!;
    private byte[] _writeBuffer = null!;
    private byte[] _readBuffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new OptionsWrapper<RamDriveOptions>(
            new RamDriveOptions { CapacityMb = 512, PageSizeKb = 64 });
        var logger = NullLogger<PagePool>.Instance;
        _pool = new PagePool(options, logger);

        _content = new PagedFileContent(_pool);

        // Pre-fill the file with data so Read benchmark has something to read
        _writeBuffer = new byte[BlockSize];
        Random.Shared.NextBytes(_writeBuffer);

        for (long offset = 0; offset < FileSize; offset += BlockSize)
            _content.Write(offset, _writeBuffer);

        _readBuffer = new byte[BlockSize];
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _content.Dispose();
        _pool.Dispose();
    }

    [Benchmark]
    public long SequentialWrite()
    {
        long totalWritten = 0;
        for (long offset = 0; offset < FileSize; offset += BlockSize)
        {
            _content.Write(offset, _writeBuffer);
            totalWritten += BlockSize;
        }
        return totalWritten;
    }

    [Benchmark]
    public long SequentialRead()
    {
        long totalRead = 0;
        for (long offset = 0; offset < FileSize; offset += BlockSize)
        {
            _content.Read(offset, _readBuffer);
            totalRead += BlockSize;
        }
        return totalRead;
    }
}
