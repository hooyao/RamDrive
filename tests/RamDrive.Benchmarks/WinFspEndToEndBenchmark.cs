using BenchmarkDotNet.Attributes;

namespace RamDrive.Benchmarks;

/// <summary>
/// End-to-end benchmark through WinFsp: uses FileStream on the mounted R:\ drive.
/// Measures the same block sizes as ATTO to allow direct comparison.
/// Must have RamDrive mounted on R:\ before running.
/// </summary>
[MemoryDiagnoser]
public class WinFspEndToEndBenchmark
{
    private const long FileSize = 256L * 1024 * 1024; // 256 MB — same as ATTO
    private const string TestFile = @"R:\bench_e2e.dat";

    [Params(4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288,
            1048576, 2097152, 4194304, 8388608, 12582912, 16777216)]
    public int BlockSize;

    private byte[] _buffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[BlockSize];
        Random.Shared.NextBytes(_buffer);

        // Pre-create the file so Read benchmark has data
        using var fs = new FileStream(TestFile, FileMode.Create, FileAccess.Write,
            FileShare.None, BlockSize, FileOptions.WriteThrough | FileOptions.SequentialScan);
        for (long written = 0; written < FileSize; written += BlockSize)
            fs.Write(_buffer);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { File.Delete(TestFile); } catch { }
    }

    [Benchmark]
    public long SequentialWrite()
    {
        using var fs = new FileStream(TestFile, FileMode.Create, FileAccess.Write,
            FileShare.None, BlockSize, FileOptions.WriteThrough | FileOptions.SequentialScan);

        long totalWritten = 0;
        for (long offset = 0; offset < FileSize; offset += BlockSize)
        {
            fs.Write(_buffer);
            totalWritten += BlockSize;
        }
        return totalWritten;
    }

    [Benchmark]
    public long SequentialRead()
    {
        using var fs = new FileStream(TestFile, FileMode.Open, FileAccess.Read,
            FileShare.Read, BlockSize, FileOptions.SequentialScan);

        long totalRead = 0;
        while (true)
        {
            int n = fs.Read(_buffer);
            if (n == 0) break;
            totalRead += n;
        }
        return totalRead;
    }
}
