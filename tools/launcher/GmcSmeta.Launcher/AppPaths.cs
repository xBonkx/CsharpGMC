namespace GmcSmeta.Launcher;

/// <summary>
/// Где лаунчер хранит скачанные версии и своё состояние.
/// Настройки самой WPF-программы лежат отдельно, в %APPDATA%\GMC-Smeta-Next.
/// </summary>
internal sealed class AppPaths
{
    public string Root { get; }
    public string Apps { get; }
    public string Downloads { get; }
    public string StateFile { get; }

    private AppPaths(string root)
    {
        Root = root;
        Apps = Path.Combine(root, "apps");
        Downloads = Path.Combine(root, "cache");
        StateFile = Path.Combine(root, "installed.txt");
    }

    public static AppPaths Create()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Path.GetTempPath();
        var paths = new AppPaths(Path.Combine(baseDir, "GMC-Smeta-Next"));
        Directory.CreateDirectory(paths.Apps);
        Directory.CreateDirectory(paths.Downloads);
        return paths;
    }

    /// <summary>Файл с токеном GitHub, если пользователь не хочет полагаться на git credential manager.</summary>
    public static string TokenFile
    {
        get
        {
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(roaming)) roaming = Path.GetTempPath();
            return Path.Combine(roaming, "GMC-Smeta-Next", "github-token.txt");
        }
    }
}
