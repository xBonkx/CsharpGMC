using System.IO;

namespace GmcSmeta.Core;

/// <summary>Reads the launcher's atomically published installation state.
/// A newer executable must be in the same versioned installation root.</summary>
public sealed record InstalledUpdateTarget(string Exe, Version Version)
{
    public const string AppExeName = "GMC-Smeta-Next.exe";

    public static InstalledUpdateTarget? TryRead(string currentExe, string appsRoot)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appsRoot));
            var current = Path.GetFullPath(currentExe);
            var currentDir = Path.GetDirectoryName(current)!;
            if (!Same(Path.GetDirectoryName(currentDir), root) ||
                !Same(Path.GetFileName(current), AppExeName) ||
                !TryVersion(Path.GetFileName(currentDir).Split('-')[0], out var currentVersion))
                return null;

            var statePath = Path.Combine(Path.GetDirectoryName(root)!, "installed.txt");
            using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (reader.ReadLine() is { } line)
            {
                var i = line.IndexOf('=');
                if (i > 0) state[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
            if (!state.TryGetValue("version", out var version) ||
                !TryVersion(version, out var newer) || newer <= currentVersion ||
                !state.TryGetValue("flavor", out var flavor) || flavor is not ("fx" or "sc") ||
                !state.TryGetValue("directory", out var directory) ||
                !state.TryGetValue("exe", out var exe) ||
                !Path.IsPathFullyQualified(directory) || !Path.IsPathFullyQualified(exe))
                return null;

            var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var fullExe = Path.GetFullPath(exe);
            var expectedFolder = version + "-" + flavor;
            var folder = Path.GetFileName(fullDir);
            // AppInstaller can add an HHmmss suffix when a same-version folder
            // was locked. Staging and arbitrary sibling folders are never targets.
            var suffixed = folder.StartsWith(expectedFolder + "-", StringComparison.OrdinalIgnoreCase) &&
                folder.Length == expectedFolder.Length + 7 &&
                folder[(expectedFolder.Length + 1)..].All(char.IsAsciiDigit);
            if (!Same(Path.GetDirectoryName(fullDir), root) ||
                !(Same(folder, expectedFolder) || suffixed) ||
                !Same(fullExe, Path.Combine(fullDir, AppExeName)) ||
                Same(fullExe, current) || !File.Exists(fullExe))
                return null;

            return new InstalledUpdateTarget(fullExe, newer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Installer has not published a usable state yet. Keep working.
            return null;
        }
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool TryVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (!Version.TryParse(text, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor,
            Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }
}
