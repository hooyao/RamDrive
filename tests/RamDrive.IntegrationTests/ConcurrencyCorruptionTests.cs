using System.Diagnostics;
using FluentAssertions;

namespace RamDrive.IntegrationTests;

/// <summary>
/// Confound-free real-corruption detector. Each file is only EVER written with its own
/// unique byte value V_i (via full overwrite, offset write, or extend-fill). Therefore a
/// read of file i may legitimately contain only V_i (written) or 0x00 (sparse hole / not yet
/// written / truncated-and-zeroed). If a read of file i returns ANY other byte — another
/// file's value V_j, or arbitrary garbage — the filesystem mixed in data that was never
/// written to that file: unambiguous real corruption (page aliasing / use-after-free / lost
/// zero-fill). All opens use FileShare.ReadWrite so same-file operations run truly
/// concurrently (maximising pressure on the three-phase write vs SetLength race), and the
/// run uses the SAME fixture config as ChaosTests (UNC mount, FileInfoTimeout=uint.MaxValue).
/// </summary>
[Collection("RamDrive")]
public class ConcurrencyCorruptionTests(RamDriveFixture fx)
{
    [Fact]
    public void EachFileOnlyHoldsItsOwnValue_NoCrossContamination()
    {
        int durationSec = int.TryParse(Environment.GetEnvironmentVariable("CORRUPT_DURATION_SEC"), out var d) ? d : 20;
        int workers = Environment.ProcessorCount * 3;
        const int NFILES = 16;
        const int MAXSZ = 300_000;   // small: 16 * 300KB << 512MB pool, so no legitimate DISK_FULL

        var root = Path.Combine(fx.Root, $"corrupt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string P(int i) => Path.Combine(root, $"f{i}.dat");
        byte V(int i) => (byte)(i + 1);          // 1..16, all non-zero and distinct

        for (int i = 0; i < NFILES; i++)
        {
            var seed = new byte[4096]; Array.Fill(seed, V(i));
            File.WriteAllBytes(P(i), seed);
        }

        long reads = 0, corruptions = 0, ioerr = 0;
        var hits = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));

        var tasks = Enumerable.Range(0, workers).Select(wk => Task.Factory.StartNew(() =>
        {
            var rng = new Random(wk * 6151 + 17);
            var rbuf = new byte[MAXSZ];
            while (!cts.IsCancellationRequested)
            {
                int i = rng.Next(NFILES);
                byte v = V(i);
                try
                {
                    switch (rng.Next(6))
                    {
                        case 0: // full overwrite (truncate to 0, then write V_i)
                        {
                            int sz = rng.Next(0, MAXSZ);
                            using var fs = new FileStream(P(i), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                            fs.SetLength(0);
                            var b = new byte[sz]; Array.Fill(b, v); fs.Write(b);
                            break;
                        }
                        case 1: // write V_i at a random offset
                        {
                            using var fs = new FileStream(P(i), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                            long off = rng.NextInt64(0, MAXSZ);
                            int len = rng.Next(1, 40_000); var b = new byte[len]; Array.Fill(b, v);
                            fs.Seek(off, SeekOrigin.Begin); fs.Write(b);
                            break;
                        }
                        case 2: // truncate
                        {
                            using var fs = new FileStream(P(i), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                            if (fs.Length > 0) fs.SetLength(rng.NextInt64(0, fs.Length));
                            break;
                        }
                        case 3: // extend (zero-fills the gap)
                        {
                            using var fs = new FileStream(P(i), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                            fs.SetLength(Math.Min(MAXSZ, fs.Length + rng.Next(1, 80_000)));
                            break;
                        }
                        default: // read + verify: every byte must be V_i or 0x00
                        {
                            int n = 0;
                            using (var fs = new FileStream(P(i), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                            {
                                int r;
                                while (n < rbuf.Length && (r = fs.Read(rbuf, n, rbuf.Length - n)) > 0) n += r;
                            }
                            Interlocked.Increment(ref reads);
                            for (int k = 0; k < n; k++)
                            {
                                byte b = rbuf[k];
                                if (b != 0 && b != v)
                                {
                                    Interlocked.Increment(ref corruptions);
                                    if (hits.Count < 10)
                                        hits.Enqueue($"f{i}(V=0x{v:X2}) @off {k}: read 0x{b:X2}" +
                                            (b >= 1 && b <= NFILES ? $" == f{b - 1}'s value (CROSS-FILE ALIASING)" : " (alien/stale byte)"));
                                    break;
                                }
                            }
                            break;
                        }
                    }
                }
                catch (IOException) { Interlocked.Increment(ref ioerr); }
                catch (UnauthorizedAccessException) { Interlocked.Increment(ref ioerr); }
            }
        }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        try { Task.WaitAll(tasks); } catch { }
        try { Directory.Delete(root, true); } catch { }

        Console.WriteLine($"[corrupt] reads={reads} ioerr={ioerr} corruptions={corruptions}");
        foreach (var h in hits) Console.WriteLine("  " + h);

        corruptions.Should().Be(0, "a file must never contain bytes that were never written to it");
    }
}
