# Benchmark results — post fix-leveldb-cache-coherency

Captured 2026-05-03 with `dotnet run --project tests/RamDrive.Benchmarks -c Release -- onread --job short`.
Configuration: 3 iterations, 1 launch, 3 warmup. Hardware: Windows 11 Pro for Workstations 26200.

`OnReadWriteBenchmark` exercises the full `FileSystemHost` Read/Write call chain
through `WinFspRamAdapter`, including the new `Notify` matrix on `WriteFile`
return paths (which the matrix correctly does not invoke for the read/write
hot path — `Notify` is only sent for path-mutating callbacks).

| Method          | BlockSize | Mean      | Error     | StdDev    | Allocated |
|-----------------|----------:|----------:|----------:|----------:|----------:|
| SequentialRead  |      4096 | 13.811 ms | 1.8962 ms | 0.1039 ms |         - |
| SequentialWrite |      4096 | 24.420 ms | 0.9398 ms | 0.0515 ms |         - |
| SequentialRead  |      8192 | 12.555 ms | 2.0784 ms | 0.1139 ms |         - |
| SequentialWrite |      8192 | 19.985 ms | 4.1743 ms | 0.2288 ms |         - |
| SequentialRead  |     16384 | 11.006 ms | 1.3674 ms | 0.0749 ms |         - |
| SequentialWrite |     16384 | 18.896 ms | 0.1491 ms | 0.0082 ms |         - |
| SequentialRead  |     32768 | 10.060 ms | 0.6049 ms | 0.0332 ms |         - |
| SequentialWrite |     32768 | 18.258 ms | 3.1481 ms | 0.1726 ms |         - |
| SequentialRead  |     65536 |  9.364 ms | 1.4486 ms | 0.0794 ms |         - |
| SequentialWrite |     65536 | 17.427 ms | 2.4319 ms | 0.1333 ms |         - |
| SequentialRead  |    131072 |  9.413 ms | 1.8348 ms | 0.1006 ms |         - |
| SequentialWrite |    131072 | 17.069 ms | 1.1019 ms | 0.0604 ms |         - |
| SequentialRead  |    262144 |  9.367 ms | 2.3537 ms | 0.1290 ms |         - |
| SequentialWrite |    262144 | 17.115 ms | 0.9876 ms | 0.0541 ms |         - |
| SequentialRead  |    524288 |  9.237 ms | 1.5848 ms | 0.0869 ms |         - |
| SequentialWrite |    524288 | 17.160 ms | 2.5695 ms | 0.1408 ms |         - |
| SequentialRead  |   1048576 |  9.231 ms | 2.2686 ms | 0.1244 ms |         - |
| SequentialWrite |   1048576 | 17.365 ms | 3.9799 ms | 0.2181 ms |         - |
| SequentialRead  |   2097152 |  9.260 ms | 0.6299 ms | 0.0345 ms |         - |
| SequentialWrite |   2097152 | 17.231 ms | 2.9760 ms | 0.1631 ms |         - |
| SequentialRead  |   4194304 |  9.295 ms | 0.7746 ms | 0.0425 ms |         - |
| SequentialWrite |   4194304 | 17.290 ms | 4.7538 ms | 0.2606 ms |         - |
| SequentialRead  |   8388608 |  9.316 ms | 1.2339 ms | 0.0676 ms |         - |
| SequentialWrite |   8388608 | 17.692 ms | 3.1522 ms | 0.1728 ms |         - |
| SequentialRead  |  12582912 |  9.492 ms | 1.3293 ms | 0.0729 ms |         - |
| SequentialWrite |  12582912 | 18.190 ms | 2.5413 ms | 0.1393 ms |         - |
| SequentialRead  |  16777216 |  9.340 ms | 1.8437 ms | 0.1011 ms |         - |
| SequentialWrite |  16777216 | 17.795 ms | 4.5721 ms | 0.2506 ms |         - |

## Notes

- `Allocated = -` across all rows: confirms zero managed-heap allocation on
  the hot read/write path is preserved by the change. The new `Notify` calls
  are not on this path (they fire only on `CreateFile`, `OverwriteFile`,
  `MoveFile`, `Cleanup(Delete)`, `SetFileSize`, `SetFileAttributes`).
- Steady-state read converges at ~9.2 ms for blocks ≥ 64 KB; write at
  ~17 ms. These are the post-fix numbers and serve as baseline for future
  changes.
- A pre-fix baseline is not captured here; comparison against an explicit
  pre-fix tag would require re-running on the same hardware with that
  commit checked out.
