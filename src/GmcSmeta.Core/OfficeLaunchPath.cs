namespace GmcSmeta.Core;

/// <summary>Recognize only a versioned office installation. No SQL configuration
/// is read, stored, or passed to the shell by this policy.</summary>
public static class OfficeLaunchPath
{
    public const string LauncherFileName = "GMC-Smeta-CSharp-Start.exe";

    public static string? Resolve(string? localAppData, string? currentExe)
    {
        if (string.IsNullOrWhiteSpace(localAppData) || string.IsNullOrWhiteSpace(currentExe) ||
            !Path.IsPathFullyQualified(localAppData) || !Path.IsPathFullyQualified(currentExe))
            return null;
        try
        {
            var root = Path.Combine(Path.GetFullPath(localAppData), "GMC-Smeta-Office");
            var exe = Path.GetFullPath(currentExe);
            var folder = Path.GetDirectoryName(exe);
            if (folder is null || !string.Equals(Path.GetFileName(exe), InstalledUpdateTarget.AppExeName,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(folder), Path.Combine(root, "apps"),
                    StringComparison.OrdinalIgnoreCase))
                return null;
            var parts = Path.GetFileName(folder).Split('-');
            if (parts.Length is < 2 or > 3 || !Version.TryParse(parts[0], out _) ||
                parts[1] is not ("fx" or "sc") ||
                (parts.Length == 3 && (parts[2].Length != 6 || !parts[2].All(char.IsAsciiDigit))))
                return null;
            var launcher = Path.Combine(root, LauncherFileName);
            return File.Exists(launcher) ? launcher : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
