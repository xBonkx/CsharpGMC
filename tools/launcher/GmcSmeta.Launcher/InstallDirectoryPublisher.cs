using System.Globalization;
using System.IO;

namespace GmcSmeta.Launcher;

/// <summary>Publish a complete staging directory without modifying any existing installation.</summary>
internal static class InstallDirectoryPublisher
{
    internal static string Publish(string staging, string target, DateTime utcNow)
    {
        // The six-digit suffix is also recognized by InstalledUpdateTarget.
        // Try the canonical directory first, then unused clock-shaped suffixes.
        for (var attempt = -1; attempt < 86400; attempt++)
        {
            var candidate = attempt < 0
                ? target
                : target + "-" + utcNow.AddSeconds(attempt).ToString("HHmmss", CultureInfo.InvariantCulture);
            if (Directory.Exists(candidate) || File.Exists(candidate))
                continue;
            try
            {
                Directory.Move(staging, candidate);
                return candidate;
            }
            catch (IOException) when (Directory.Exists(candidate) || File.Exists(candidate))
            {
                // A concurrent installer took this name; try another one.
            }
        }
        throw new IOException("Нет свободной папки для установки версии.");
    }
}
