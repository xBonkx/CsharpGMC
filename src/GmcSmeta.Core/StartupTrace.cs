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

    public static void Mark(string phase)
    {
        if (string.IsNullOrWhiteSpace(DirectoryPath)) return;
        var timestamp = Stopwatch.GetTimestamp();
        try
        {
            lock (Gate)
            {
                if (_file is null)
                {
                    Directory.CreateDirectory(DirectoryPath);
                    var path = Path.Combine(DirectoryPath, $"startup-{Environment.ProcessId}-{Guid.NewGuid():N}.csv");
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
}
