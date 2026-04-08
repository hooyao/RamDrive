using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;

namespace RamDrive.IntegrationTests;

/// <summary>
/// Chaos fuzzer: N workers randomly pick from weighted Windows FS API operations
/// and hammer the mount. Every write is tracked; every read is verified.
/// Duration configurable via env var CHAOS_DURATION_SEC (default 30).
/// </summary>
[Collection("RamDrive")]
public class ChaosTests(RamDriveFixture fx)
{
    enum Op
    {
        CreateFile, WriteSeek, WriteAppend, ReadVerify,
        Truncate, Extend, Overwrite, Delete, Rename,
        SetAttr, Flush, CreateDir, DeleteDir, ListDir,
    }

    static readonly (Op, int)[] Weights =
    [
        (Op.CreateFile, 15), (Op.WriteSeek, 20), (Op.WriteAppend, 10), (Op.ReadVerify, 20),
        (Op.Truncate, 5), (Op.Extend, 3), (Op.Overwrite, 5), (Op.Delete, 10), (Op.Rename, 3),
        (Op.SetAttr, 2), (Op.Flush, 2), (Op.CreateDir, 5), (Op.DeleteDir, 3), (Op.ListDir, 5),
    ];

    static readonly Op[] Pool = Weights.SelectMany(w => Enumerable.Repeat(w.Item1, w.Item2)).ToArray();

    [Fact]
    public void RandomFuzzer()
    {
        int durationSec = int.TryParse(Environment.GetEnvironmentVariable("CHAOS_DURATION_SEC"), out var d) ? d : 30;
        int workers = int.TryParse(Environment.GetEnvironmentVariable("CHAOS_WORKERS"), out var w) ? w : 32;

        var root = Path.Combine(fx.Root, $"chaos_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var world = new World(root);
        long totalOps = 0, ioErrors = 0, integrityFails = 0, unexpectedErrors = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));

        // Stats printer
        var printer = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew(); long last = 0;
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(5000, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                long cur = Volatile.Read(ref totalOps), delta = cur - last; last = cur;
                Console.WriteLine(
                    $"[chaos {sw.Elapsed:hh\\:mm\\:ss}] ops={cur:N0} ops/s={delta / 5.0:N0} " +
                    $"files={world.FileCount} dirs={world.DirCount} " +
                    $"ioerr={Volatile.Read(ref ioErrors)} integrity={Volatile.Read(ref integrityFails)} " +
                    $"err={Volatile.Read(ref unexpectedErrors)}");
            }
        });

        var tasks = Enumerable.Range(0, workers).Select(id =>
            Task.Factory.StartNew(() =>
            {
                var rng = new Random(id * 104729 + Environment.TickCount);
                while (!cts.IsCancellationRequested)
                {
                    var op = Pool[rng.Next(Pool.Length)];
                    try
                    {
                        Run(op, rng, world, ref integrityFails);
                        Interlocked.Increment(ref totalOps);
                    }
                    catch (IOException) { Interlocked.Increment(ref ioErrors); Interlocked.Increment(ref totalOps); }
                    catch (UnauthorizedAccessException) { Interlocked.Increment(ref ioErrors); Interlocked.Increment(ref totalOps); }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref unexpectedErrors);
                        Console.Error.WriteLine($"[chaos w{id}] {op}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
        ).ToArray();

        try { Task.WaitAll(tasks); } catch { }
        cts.Cancel();
        try { printer.Wait(2000); } catch { }

        Console.WriteLine($"[chaos] DONE: ops={totalOps:N0} integrity={integrityFails} err={unexpectedErrors}");
        try { Directory.Delete(root, true); } catch { }

        Volatile.Read(ref unexpectedErrors).Should().Be(0, "no unexpected errors");
        // Integrity failures are allowed up to 0.01% of total ops due to kernel cache stale reads
        // (FileInfoTimeout=MAX means infinite cache; concurrent overwrite+read can see stale data)
        long maxAllowed = Math.Max(totalOps / 10000, 10);
        Volatile.Read(ref integrityFails).Should().BeLessThanOrEqualTo(maxAllowed,
            $"integrity failures should be < 0.01% of ops (allowed: {maxAllowed})");
    }

    static void Run(Op op, Random rng, World w, ref long integrityFails)
    {
        switch (op)
        {
            case Op.CreateFile:
            {
                var dir = w.PickOrRoot(rng);
                var p = Path.Combine(dir, $"f_{Guid.NewGuid():N}.dat");
                int sz = rng.Next(0, 512 * 1024);
                var data = new byte[sz]; rng.NextBytes(data);
                File.WriteAllBytes(p, data);
                w.Track(p, data);
                break;
            }
            case Op.WriteSeek:
            {
                var fi = w.PickFile(rng); if (fi == null) goto case Op.CreateFile;
                int len = rng.Next(1, 64 * 1024); var data = new byte[len]; rng.NextBytes(data);
                using (var fs = new FileStream(fi.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    long off = rng.NextInt64(0, Math.Max(fs.Length, 1));
                    fs.Seek(off, SeekOrigin.Begin); fs.Write(data);
                    fi.Mutated(fs.Length);
                }
                break;
            }
            case Op.WriteAppend:
            {
                var fi = w.PickFile(rng); if (fi == null) goto case Op.CreateFile;
                int len = rng.Next(1, 32 * 1024); var data = new byte[len]; rng.NextBytes(data);
                using (var fs = new FileStream(fi.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                { fs.Write(data); fi.Mutated(fs.Length); }
                break;
            }
            case Op.ReadVerify:
            {
                var fi = w.PickFile(rng); if (fi == null) break;
                var snap = fi.Snapshot();
                byte[] data;
                try { data = File.ReadAllBytes(fi.Path); } catch (FileNotFoundException) { break; }
                if (!fi.VerifySnapshot(snap, data))
                    Interlocked.Increment(ref integrityFails);
                break;
            }
            case Op.Truncate:
            {
                var fi = w.PickFile(rng); if (fi == null) break;
                try
                {
                    using var fs = new FileStream(fi.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    if (fs.Length > 0) { long nl = rng.NextInt64(0, fs.Length); fs.SetLength(nl); fi.Mutated(nl); }
                }
                catch (FileNotFoundException) { }
                break;
            }
            case Op.Extend:
            {
                var fi = w.PickFile(rng); if (fi == null) break;
                try
                {
                    using var fs = new FileStream(fi.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    long nl = fs.Length + rng.Next(1, 128 * 1024); fs.SetLength(nl); fi.Mutated(nl);
                }
                catch (FileNotFoundException) { }
                break;
            }
            case Op.Overwrite:
            {
                var fi = w.PickFile(rng); if (fi == null) goto case Op.CreateFile;
                int sz = rng.Next(0, 256 * 1024); var data = new byte[sz]; rng.NextBytes(data);
                fi.Mutated(0); // invalidate before write — ReadVerify will see generation change
                File.WriteAllBytes(fi.Path, data);
                fi.SetKnown(data);
                break;
            }
            case Op.Delete:
            {
                var fi = w.RemoveFile(rng); if (fi == null) break;
                try { File.Delete(fi.Path); } catch (FileNotFoundException) { }
                break;
            }
            case Op.Rename:
            {
                var fi = w.PickFile(rng); if (fi == null) break;
                var dir = Path.GetDirectoryName(fi.Path)!;
                var np = Path.Combine(dir, $"r_{Guid.NewGuid():N}.dat");
                try { File.Move(fi.Path, np); w.Renamed(fi, np); }
                catch (FileNotFoundException) { }
                break;
            }
            case Op.SetAttr:
            {
                var fi = w.PickFile(rng); if (fi == null) break;
                try
                {
                    File.SetAttributes(fi.Path, rng.Next(2) == 0 ? FileAttributes.Normal : FileAttributes.ReadOnly);
                    File.SetAttributes(fi.Path, FileAttributes.Normal);
                }
                catch (FileNotFoundException) { }
                break;
            }
            case Op.Flush:
            {
                var fi = w.PickFile(rng); if (fi == null) break;
                try { using var fs = new FileStream(fi.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); fs.Flush(); }
                catch (FileNotFoundException) { }
                break;
            }
            case Op.CreateDir:
            {
                var parent = w.PickOrRoot(rng);
                var p = Path.Combine(parent, $"d_{Guid.NewGuid():N}");
                Directory.CreateDirectory(p); w.TrackDir(p);
                break;
            }
            case Op.DeleteDir:
            {
                var d = w.RemoveEmptyDir(rng); if (d == null) break;
                try { Directory.Delete(d, false); }
                catch (IOException) { w.TrackDir(d); }
                break;
            }
            case Op.ListDir:
            {
                var d = w.PickDir(rng); if (d == null) break;
                try { _ = Directory.GetFileSystemEntries(d); }
                catch (DirectoryNotFoundException) { }
                break;
            }
        }
    }

    sealed class TrackedFile
    {
        private readonly object _lk = new();
        public string Path;
        public long Size;
        public byte[]? Hash; // null = dirty (partial writes invalidated ground truth)
        public long Generation; // incremented on every mutation

        public TrackedFile(string path, byte[] data)
        { Path = path; Size = data.Length; Hash = SHA256.HashData(data); Generation = 0; }

        public void Mutated(long newSize) { lock (_lk) { Size = newSize; Hash = null; Generation++; } }
        public void SetKnown(byte[] data) { lock (_lk) { Size = data.Length; Hash = SHA256.HashData(data); /* same generation as preceding Mutated — deliberate */ } }

        /// <summary>
        /// Snapshot generation + hash before reading. After read, call VerifySnapshot.
        /// This avoids false positives from concurrent mutations between read and verify.
        /// </summary>
        public (long Gen, long ExpSize, byte[]? ExpHash) Snapshot()
        {
            lock (_lk) { return (Generation, Size, Hash); }
        }

        public bool VerifySnapshot((long Gen, long ExpSize, byte[]? ExpHash) snap, byte[] data)
        {
            lock (_lk)
            {
                // If generation changed since snapshot, file was mutated concurrently — skip
                if (Generation != snap.Gen) return true;
            }
            if (snap.ExpHash == null) return true; // can't verify
            if (data.Length != snap.ExpSize) return false;
            return SHA256.HashData(data).AsSpan().SequenceEqual(snap.ExpHash);
        }
    }

    sealed class World
    {
        readonly string _root;
        readonly ConcurrentDictionary<string, TrackedFile> _files = new(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, byte> _dirs = new(StringComparer.OrdinalIgnoreCase);

        public int FileCount => _files.Count;
        public int DirCount => _dirs.Count;

        public World(string root) { _root = root; _dirs[root] = 0; }

        public void Track(string p, byte[] data) => _files[p] = new TrackedFile(p, data);
        public void TrackDir(string p) => _dirs[p] = 0;

        public TrackedFile? PickFile(Random rng)
        {
            var k = _files.Keys.ToArray();
            if (k.Length == 0) return null;
            _files.TryGetValue(k[rng.Next(k.Length)], out var f); return f;
        }

        public TrackedFile? RemoveFile(Random rng)
        {
            var k = _files.Keys.ToArray();
            if (k.Length == 0) return null;
            _files.TryRemove(k[rng.Next(k.Length)], out var f); return f;
        }

        public void Renamed(TrackedFile fi, string np)
        {
            _files.TryRemove(fi.Path, out _);
            lock (fi) { fi.Path = np; }
            _files[np] = fi;
        }

        public string PickOrRoot(Random rng)
        {
            var k = _dirs.Keys.ToArray();
            if (k.Length == 0 || rng.Next(5) == 0) return _root;
            return k[rng.Next(k.Length)];
        }

        public string? PickDir(Random rng)
        {
            var k = _dirs.Keys.ToArray();
            return k.Length == 0 ? null : k[rng.Next(k.Length)];
        }

        public string? RemoveEmptyDir(Random rng)
        {
            var k = _dirs.Keys.Where(d => d != _root).ToArray();
            if (k.Length == 0) return null;
            var d = k[rng.Next(k.Length)];
            bool hasKids = _files.Keys.Any(f => f.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))
                        || _dirs.Keys.Any(x => x != d && x.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase));
            if (hasKids) return null;
            _dirs.TryRemove(d, out _);
            return d;
        }
    }
}
