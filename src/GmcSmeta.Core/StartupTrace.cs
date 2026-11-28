using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace GmcSmeta.Core;

/// <summary>Opt-in, per-process startup trace. Only fixed phase names and timings; no SQL or credentials.</summary>
public static class StartupTrace
{
    private static readonly string? DirectoryPath = Environment.GetEnvironmentVariable("GMC_SMETA_STARTUP_TRACE_DIR");
    private static readonly long Started = Stopwatch.GetTimestamp();
    private static readonly object Gate = new();
    private static string? _file;

    public static bool Enabled => !string.IsNullOrWhiteSpace(DirectoryPath);

    public static void MarkBuild()
    {
#if DEBUG
        Mark("trace_build_debug");
#else
        Mark("trace_build_release");
#endif
    }

    public static void Mark(string phase)
    {
        if (!Enabled) return;
        var timestamp = Stopwatch.GetTimestamp();
        try
        {
            lock (Gate)
            {
                if (_file is null)
                {
                    Directory.CreateDirectory(DirectoryPath!);
                    var path = Path.Combine(DirectoryPath!, $"startup-{Environment.ProcessId}-{Guid.NewGuid():N}.csv");
                    File.WriteAllText(path, "Utc,ProcessId,Phase,ElapsedMs,MonotonicTicks,Frequency\n");
                    _file = path;
                }
                File.AppendAllText(_file, string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.UtcNow:O},{Environment.ProcessId},{phase},{Stopwatch.GetElapsedTime(Started, timestamp).TotalMilliseconds:F3},{timestamp},{Stopwatch.Frequency}\n"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Diagnostics must never prevent the program from starting.
        }
    }

    // Separate file, so existing startup CSV consumers retain their schema.
    // These measurements are opt-in and contain no document IDs or SQL values.
    private static string? _metricsFile;

    private static readonly System.Threading.AsyncLocal<long> Operation = new();
    private static long _operationSequence;

    public static IDisposable? BeginOperation() => Enabled ? new OperationScope() : null;
    public static IDisposable? Measure(string phase) => Enabled ? new PhaseScope(phase, true) : null;
    public static IDisposable? MeasureElapsed(string phase) => Enabled ? new PhaseScope(phase, false) : null;

    private sealed class OperationScope : IDisposable
    {
        private readonly long _previous = Operation.Value;
        public OperationScope() => Operation.Value = System.Threading.Interlocked.Increment(ref _operationSequence);
        public void Dispose() => Operation.Value = _previous;
    }

    // Value1/Value2 are phase-specific counts (cache entries/row weight,
    // undo rows/result sets). Never pass names, IDs, money or credentials.
    public static void Sample(string phase, long value1 = 0, long value2 = 0)
    {
        if (!Enabled) return;
        WriteMetrics(phase, 0, 0, value1, value2, Operation.Value);
    }

    private sealed class PhaseScope : IDisposable
    {
        private readonly string _phase;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly long _operation = Operation.Value;
        private readonly long _allocated = GC.GetAllocatedBytesForCurrentThread();
        private readonly bool _trackAllocation;
        private bool _disposed;

        public PhaseScope(string phase, bool trackAllocation)
        {
            _phase = phase;
            _trackAllocation = trackAllocation;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            var allocated = _trackAllocation && _thread == Environment.CurrentManagedThreadId
                ? GC.GetAllocatedBytesForCurrentThread() - _allocated : -1;
            WriteMetrics(_phase, elapsed, allocated, 0, 0, _operation);
        }
    }

    private static void WriteMetrics(string phase, double elapsedMs, long threadAllocated, long value1, long value2, long operation)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var ticks = Stopwatch.GetTimestamp();
            var cpuMs = process.TotalProcessorTime.TotalMilliseconds;
            var privateBytes = process.PrivateMemorySize64;
            var workingSet = process.WorkingSet64;
            var heap = GC.GetTotalMemory(forceFullCollection: false);
            lock (Gate)
            {
                if (_metricsFile is null)
                {
                    Directory.CreateDirectory(DirectoryPath!);
                    var path = Path.Combine(DirectoryPath!, $"performance-{Environment.ProcessId}-{Guid.NewGuid():N}.csv");
                    File.WriteAllText(path, "Utc,ProcessId,Operation,Phase,MonotonicTicks,Frequency,DurationMs,ThreadAllocatedBytes,ProcessCpuMs,PrivateBytes,WorkingSetBytes,ManagedHeapBytes,Value1,Value2\n");
                    _metricsFile = path;
                }
                File.AppendAllText(_metricsFile, string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.UtcNow:O},{Environment.ProcessId},{operation},{phase},{ticks},{Stopwatch.Frequency},{elapsedMs:F3},{threadAllocated},{cpuMs:F3},{privateBytes},{workingSet},{heap},{value1},{value2}\n"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            // Diagnostics cannot interfere with editing or saving.
        }
    }
}
