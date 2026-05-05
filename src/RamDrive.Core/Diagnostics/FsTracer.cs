// FsTracer — generic path-filtered FS callback tracer.
//
// Marked [Conditional("TRACE_FS")] so the Roslyn compiler removes every call
// site (and its argument expressions) at IL emission time when the consuming
// project does not define the TRACE_FS symbol. Production RamDrive.Cli does
// not define it; RamDrive.Cli.Diag does. AOT-safe: the IL never contains the
// call, so ILC has nothing to keep or strip.
//
// To enable at runtime in Cli.Diag (or any consumer that defines TRACE_FS),
// set the environment variable RAMDRIVE_TRACE_PATH to a substring filter, and
// optionally RAMDRIVE_TRACE_FILE to the output path (default %TEMP%\ramdrive_trace.log).

using System.Diagnostics;
using System.Globalization;

namespace RamDrive.Core.Diagnostics;

public static class FsTracer
{
    private static readonly string? _filter = Environment.GetEnvironmentVariable("RAMDRIVE_TRACE_PATH");
    private static readonly string _file = Environment.GetEnvironmentVariable("RAMDRIVE_TRACE_FILE")
                                            ?? Path.Combine(Path.GetTempPath(), "ramdrive_trace.log");
    private static readonly object _lock = new();
    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static StreamWriter? _writer;
    private static readonly bool _enabled;

    static FsTracer()
    {
        if (string.IsNullOrEmpty(_filter)) return;
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _writer = new StreamWriter(new FileStream(_file, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = false,
            };
            _writer.WriteLine($"# RamDrive trace started {DateTime.UtcNow:O} filter='{_filter}'");
            _writer.Flush();
            _enabled = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
            Console.CancelKeyPress += (_, _) => Flush();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FsTracer] init failed: {ex.Message}");
        }
    }

    /// <summary>
    /// True when the env var <c>RAMDRIVE_TRACE_PATH</c> was set at startup AND the
    /// log file opened successfully. Always false in assemblies that did not define
    /// <c>TRACE_FS</c> (the call below is also stripped at the call site, so this
    /// only matters inside the assembly that hosts the tracer).
    /// </summary>
    public static bool Enabled => _enabled;

    /// <summary>
    /// Append one trace line if <paramref name="path"/> matches the active filter.
    /// Marked <see cref="ConditionalAttribute"/>: callers compiled without the
    /// <c>TRACE_FS</c> symbol have this call (and its argument expressions) erased
    /// from IL by the C# compiler — zero runtime cost, zero AOT footprint.
    /// </summary>
    [Conditional("TRACE_FS")]
    public static void Trace(string op, string? path, string? extra = null)
    {
        if (!_enabled) return;
        if (path == null) return;
        if (path.IndexOf(_filter!, StringComparison.OrdinalIgnoreCase) < 0) return;
        var ts = _sw.Elapsed.TotalSeconds.ToString("0.000000", CultureInfo.InvariantCulture);
        var tid = Environment.CurrentManagedThreadId;
        var line = extra is null
            ? $"{ts}  T{tid,-4}  {op,-22}  {path}"
            : $"{ts}  T{tid,-4}  {op,-22}  {path}  | {extra}";
        lock (_lock)
        {
            _writer!.WriteLine(line);
        }
    }

    public static void Flush()
    {
        if (!_enabled) return;
        lock (_lock)
        {
            try { _writer?.Flush(); } catch { }
        }
    }
}
