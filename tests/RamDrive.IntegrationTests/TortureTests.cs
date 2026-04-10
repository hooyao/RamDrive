using FluentAssertions;

namespace RamDrive.IntegrationTests;

[Collection("RamDrive")]
public class TortureTests(RamDriveFixture fx) : IDisposable
{
    private readonly string _root = Path.Combine(fx.Root, $"t_{Guid.NewGuid():N}");

    void EnsureRoot() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    static void Fill(Span<byte> buf, int seed, long off)
    {
        for (int i = 0; i < buf.Length; i++) buf[i] = (byte)((seed * 31 + (off + i)) & 0xFF);
    }
    static void Verify(ReadOnlySpan<byte> buf, int seed, long off, string ctx)
    {
        for (int i = 0; i < buf.Length; i++)
        {
            byte exp = (byte)((seed * 31 + (off + i)) & 0xFF);
            if (buf[i] != exp) throw new Exception($"Mismatch at {off + i}: 0x{buf[i]:X2}!=0x{exp:X2} [{ctx}]");
        }
    }

    [Fact]
    public async Task SequentialWriteRead()
    {
        EnsureRoot();
        const int tasks = 10, fileSize = 10 * 1024 * 1024, block = 64 * 1024;
        await Task.WhenAll(Enumerable.Range(0, tasks).Select(id => Task.Run(() =>
        {
            var path = Path.Combine(_root, $"seq_{id}.dat");
            var buf = new byte[block];
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, block))
                for (long o = 0; o < fileSize; o += block) { Fill(buf, id, o); fs.Write(buf); }
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, block))
                for (long o = 0; o < fileSize; o += block) { fs.Read(buf); Verify(buf, id, o, $"seq {id}"); }
        })));
    }

    [Fact]
    public async Task RandomOffsetReadWrite()
    {
        EnsureRoot();
        const int tasks = 10, ops = 500, maxSize = 2 * 1024 * 1024;
        await Task.WhenAll(Enumerable.Range(0, tasks).Select(id => Task.Run(() =>
        {
            var path = Path.Combine(_root, $"rnd_{id}.dat");
            var rng = new Random(id * 7919);
            using (var fs = new FileStream(path, FileMode.Create)) fs.SetLength(maxSize);
            var writes = new List<(long Off, int Len)>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                for (int i = 0; i < ops; i++)
                {
                    int len = rng.Next(1024, 65536);
                    long off = rng.NextInt64(0, maxSize - len);
                    var buf = new byte[len]; Fill(buf, id, off);
                    fs.Seek(off, SeekOrigin.Begin); fs.Write(buf);
                    writes.Add((off, len));
                }
            }
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                foreach (var (off, len) in writes.TakeLast(50))
                {
                    var buf = new byte[len]; fs.Seek(off, SeekOrigin.Begin);
                    int r = 0; while (r < len) { int n = fs.Read(buf, r, len - r); if (n == 0) break; r += n; }
                    Verify(buf.AsSpan(0, r), id, off, $"rnd {id}");
                }
            }
        })));
    }

    [Fact]
    public async Task ConcurrentAppend()
    {
        EnsureRoot();
        const int taskCount = 20, recs = 500, recSize = 512;
        var path = Path.Combine(_root, "append.dat");
        using var shared = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var lck = new object();
        await Task.WhenAll(Enumerable.Range(0, taskCount).Select(id => Task.Run(() =>
        {
            var rec = new byte[recSize];
            for (int s = 0; s < recs; s++)
            {
                BitConverter.TryWriteBytes(rec.AsSpan(0, 4), id);
                BitConverter.TryWriteBytes(rec.AsSpan(4, 4), s);
                for (int i = 8; i < recSize; i++) rec[i] = (byte)((id * 31 + s * 17 + i) & 0xFF);
                lock (lck) { shared.Write(rec); }
            }
        })));
        shared.Flush(); shared.Close();

        var data = File.ReadAllBytes(path);
        int total = taskCount * recs;
        data.Length.Should().Be(total * recSize);
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < total; i++)
        {
            var r = data.AsSpan(i * recSize, recSize);
            int tid = BitConverter.ToInt32(r[..4]), seq = BitConverter.ToInt32(r[4..8]);
            seen.Add((tid, seq)).Should().BeTrue($"dup ({tid},{seq})");
            for (int j = 8; j < recSize; j++)
            {
                byte exp = (byte)((tid * 31 + seq * 17 + j) & 0xFF);
                r[j].Should().Be(exp, $"rec {i} byte {j}");
            }
        }
        seen.Count.Should().Be(total);
    }

    [Fact]
    public async Task CreateDeleteChurn()
    {
        EnsureRoot();
        var churn = Path.Combine(_root, "churn"); Directory.CreateDirectory(churn);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(id => Task.Run(() =>
        {
            var td = Path.Combine(churn, $"t{id}"); Directory.CreateDirectory(td);
            for (int i = 0; i < 200; i++)
            {
                var p = Path.Combine(td, $"f{i}.dat");
                var buf = new byte[4096]; Fill(buf, id * 1000 + i, 0);
                File.WriteAllBytes(p, buf);
                Verify(File.ReadAllBytes(p), id * 1000 + i, 0, $"churn {id}/{i}");
                File.Delete(p);
            }
            Directory.GetFiles(td).Should().BeEmpty();
            Directory.Delete(td);
        })));
    }

    [Fact]
    public async Task DirectoryTreeStress()
    {
        EnsureRoot();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(id => Task.Run(() =>
        {
            var tr = Path.Combine(_root, $"tree_{id}");
            for (int a = 0; a < 4; a++) for (int b = 0; b < 4; b++) for (int c = 0; c < 4; c++)
            {
                var d = Path.Combine(tr, $"a{a}", $"b{b}", $"c{c}"); Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "d.txt"), $"{id}-{a}/{b}/{c}");
            }
            for (int a = 0; a < 4; a++) for (int b = 0; b < 4; b++) for (int c = 0; c < 4; c++)
                File.ReadAllText(Path.Combine(tr, $"a{a}", $"b{b}", $"c{c}", "d.txt"))
                    .Should().Be($"{id}-{a}/{b}/{c}");
            Directory.Delete(tr, true);
            Directory.Exists(tr).Should().BeFalse();
        })));
    }

    [Fact]
    public async Task OverwriteTruncateExtend()
    {
        EnsureRoot();
        const int sz = 1024 * 1024;
        await Task.WhenAll(Enumerable.Range(0, 10).Select(id => Task.Run(() =>
        {
            var p = Path.Combine(_root, $"ovr_{id}.dat");
            var a = new byte[sz]; Fill(a, id * 100, 0); File.WriteAllBytes(p, a);
            Verify(File.ReadAllBytes(p), id * 100, 0, "A");
            var b = new byte[sz]; Fill(b, id * 200, 0); File.WriteAllBytes(p, b);
            Verify(File.ReadAllBytes(p), id * 200, 0, "B");
            using (var fs = new FileStream(p, FileMode.Open)) fs.SetLength(sz / 2);
            var trunc = File.ReadAllBytes(p);
            trunc.Length.Should().Be(sz / 2);
            Verify(trunc, id * 200, 0, "trunc");
            using (var fs = new FileStream(p, FileMode.Open)) fs.SetLength(sz);
            var ext = File.ReadAllBytes(p);
            ext.Length.Should().Be(sz);
            Verify(ext.AsSpan(0, sz / 2), id * 200, 0, "ext-first");
            ext.AsSpan(sz / 2).ToArray().Should().OnlyContain(x => x == 0, "extended region should be zeros");
        })));
    }

    [Fact]
    public async Task MidOperationCancel()
    {
        EnsureRoot();
        const int target = 5 * 1024 * 1024, block = 64 * 1024;
        await Task.WhenAll(Enumerable.Range(0, 10).Select(id => Task.Run(() =>
        {
            var p = Path.Combine(_root, $"cancel_{id}.dat");
            var cts = new CancellationTokenSource();
            int written = 0;
            try
            {
                using var fs = new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None, block);
                var buf = new byte[block];
                for (long o = 0; o < target; o += block)
                {
                    if (o >= target / 2) cts.Cancel();
                    cts.Token.ThrowIfCancellationRequested();
                    Fill(buf, id, o); fs.Write(buf); written++;
                }
            }
            catch (OperationCanceledException) { }
            var data = File.ReadAllBytes(p);
            data.Length.Should().BeLessThanOrEqualTo(target);
            for (int b2 = 0; b2 < written; b2++)
                Verify(data.AsSpan(b2 * block, block), id, (long)b2 * block, $"cancel {id}");
            File.Delete(p);
        })));
    }

    [Fact]
    public async Task RenameUnderLoad()
    {
        EnsureRoot();
        var ren = Path.Combine(_root, "ren"); Directory.CreateDirectory(ren);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(id => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                var pA = Path.Combine(ren, $"t{id}_A_{i}.dat");
                var pB = Path.Combine(ren, $"t{id}_B_{i}.dat");
                var buf = new byte[4096]; Fill(buf, id * 10000 + i, 0);
                File.WriteAllBytes(pA, buf);
                File.Move(pA, pB);
                File.Exists(pA).Should().BeFalse();
                Verify(File.ReadAllBytes(pB), id * 10000 + i, 0, $"ren {id}");
                File.Move(pB, pA);
                Verify(File.ReadAllBytes(pA), id * 10000 + i, 0, $"ren back {id}");
                File.Delete(pA);
            }
        })));
    }

    [Fact]
    public void CapacityPressure()
    {
        EnsureRoot();
        var cap = Path.Combine(_root, "cap"); Directory.CreateDirectory(cap);
        const int chunk = 1024 * 1024;
        long target = (long)(fx.CapacityMb * 1024 * 1024 * 0.90);
        int count = 0;
        var buf = new byte[chunk];
        while (count * (long)chunk < target)
        {
            Fill(buf, count, 0);
            try { File.WriteAllBytes(Path.Combine(cap, $"f_{count}.dat"), buf); count++; }
            catch (IOException) { break; }
        }
        count.Should().BeGreaterThan(0);

        // Write under pressure must not hang (5s timeout)
        var task = Task.Run(() =>
        {
            try { File.WriteAllBytes(Path.Combine(cap, "overflow.dat"), new byte[chunk * 2]); }
            catch (IOException) { }
        });
        task.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("write under capacity pressure should not hang");

        // Verify existing files
        for (int i = 0; i < Math.Min(count, 5); i++)
            Verify(File.ReadAllBytes(Path.Combine(cap, $"f_{i}.dat")), i, 0, $"cap verify {i}");

        // Cleanup + write again
        for (int i = 0; i < count; i++)
            try { File.Delete(Path.Combine(cap, $"f_{i}.dat")); } catch { }
        try { File.Delete(Path.Combine(cap, "overflow.dat")); } catch { }

        Fill(buf, 999, 0);
        var after = Path.Combine(cap, "after.dat");
        File.WriteAllBytes(after, buf);
        Verify(File.ReadAllBytes(after), 999, 0, "cap after cleanup");
    }

    [Fact]
    public async Task MixedWorkload()
    {
        EnsureRoot();
        var mix = Path.Combine(_root, "mix"); Directory.CreateDirectory(mix);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var writtenFiles = new System.Collections.Concurrent.ConcurrentBag<string>();

        Task MakeWorkers(int n, Action<int, int, Random> body) =>
            Task.WhenAll(Enumerable.Range(0, n).Select(id => Task.Run(() =>
            {
                var rng = new Random(id * 31 + Environment.TickCount);
                int iter = 0;
                while (!cts.IsCancellationRequested)
                {
                    try { body(id, iter++, rng); }
                    catch (IOException) { }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { errors.Add(ex); break; }
                }
            }, cts.Token)));

        var writers = MakeWorkers(30, (id, iter, rng) =>
        {
            int sz = rng.Next(100 * 1024, 2 * 1024 * 1024);
            var buf = new byte[sz]; Fill(buf, id * 10000 + iter, 0);
            var p = Path.Combine(mix, $"w_{id}_{iter}.dat");
            File.WriteAllBytes(p, buf);
            Verify(File.ReadAllBytes(p), id * 10000 + iter, 0, $"mix w{id}");
            writtenFiles.Add(p);
        });

        var readers = MakeWorkers(30, (id, iter, rng) =>
        {
            var files = writtenFiles.ToArray();
            if (files.Length == 0) { Thread.Sleep(10); return; }
            var p = files[rng.Next(files.Length)];
            if (File.Exists(p)) _ = File.ReadAllBytes(p);
        });

        var churners = MakeWorkers(20, (id, iter, rng) =>
        {
            var p = Path.Combine(mix, $"ch_{id}_{iter}.dat");
            var buf = new byte[4096]; Fill(buf, id + 1000, iter);
            File.WriteAllBytes(p, buf);
            Verify(File.ReadAllBytes(p), id + 1000, iter, $"ch{id}");
            File.Delete(p);
        });

        var dirBuilders = MakeWorkers(10, (id, iter, rng) =>
        {
            var tr = Path.Combine(mix, $"dt_{id}_{iter}");
            for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++)
            {
                var d = Path.Combine(tr, $"a{a}", $"b{b}"); Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "f.txt"), $"{id}-{iter}");
            }
            Directory.Delete(tr, true);
        });

        var renamers = MakeWorkers(10, (id, iter, rng) =>
        {
            var pA = Path.Combine(mix, $"rn_{id}_{iter}_a.dat");
            var pB = Path.Combine(mix, $"rn_{id}_{iter}_b.dat");
            File.WriteAllBytes(pA, new byte[1024]);
            if (File.Exists(pA)) { File.Move(pA, pB); File.Delete(pB); }
        });

        try { await Task.WhenAll(writers, readers, churners, dirBuilders, renamers); }
        catch (OperationCanceledException) { }

        errors.Should().BeEmpty("no unexpected errors in mixed workload");
    }
}
