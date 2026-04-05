using BenchmarkDotNet.Running;
using RamDrive.Benchmarks;

if (args.Length > 0 && args[0] == "e2e")
    BenchmarkRunner.Run<WinFspEndToEndBenchmark>(args: args[1..]);
else if (args.Length > 0 && args[0] == "onread")
    BenchmarkRunner.Run<OnReadWriteBenchmark>(args: args[1..]);
else
    BenchmarkRunner.Run<PagedFileContentBenchmark>(args: args);
