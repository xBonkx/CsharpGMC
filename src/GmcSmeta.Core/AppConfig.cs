using System.Text.Encodings.Web;
using System.Text.Json;

namespace GmcSmeta.Core;

/// <summary>
/// Настройки и пути — точная копия поведения smeta/config.py:
/// программа видит «домашнюю» папку (GMC_SMETA_HOME), база лежит РЯДОМ с папкой программы,
/// персональные настройки — в %APPDATA%\GMC-Smeta\settings.json.
/// </summary>
public static class AppConfig
{
    public const string AppName = "ГМЦ-Смета";
    public const string AppId = "GMC-Smeta";
    public const string AppVersion = "0.66.4";
    public const decimal DefaultVatRate = 22.0m;
    public const string LauncherName = "GMC-Smeta-Start.exe";
    public const string DataDirName = "GMC-Smeta-данные";

    /// <summary>Папка, рядом с которой ищется общая база (её же сообщает лаунчер).</summary>
    public static string ProgramDir()
    {
        var home = Environment.GetEnvironmentVariable("GMC_SMETA_HOME");
        return !string.IsNullOrWhiteSpace(home) ? home! : AppContext.BaseDirectory.TrimEnd('\\', '/');
    }

    /// <summary>Папка, из которой реально запущен exe (локальная копия при старте через лаунчер).</summary>
    public static string RunningDir() => AppContext.BaseDirectory.TrimEnd('\\', '/');

    /// <summary>Старое место базы: <программа>\data\base.db.</summary>
    public static string LegacyDbPath() => Path.Combine(ProgramDir(), "data", "base.db");

    /// <summary>Обычное место: <рядом с программой>\GMC-Smeta-данные\base.db.</summary>
    public static string DefaultDbPath() =>
        Path.Combine(Path.GetDirectoryName(ProgramDir()) ?? ProgramDir(), DataDirName, "base.db");

    public static string SettingsPath()
    {
        var root = Environment.GetEnvironmentVariable("APPDATA");
        if (string.IsNullOrWhiteSpace(root))
            root = GmcSmeta.Core.Demo.DemoMode.ApplicationData;
        var dir = Path.Combine(root!, AppId);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    /// <summary>
    /// Настройки пользователя. Читаются как «сырой» JSON: неизвестные ключи нужно сохранять
    /// как есть (их пишет Python-версия, и обратно она их ждёт).
    /// </summary>
    public static Dictionary<string, JsonElement> LoadSettings()
    {
        var path = SettingsPath();
        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);          // BOM (файл из Блокнота) снимается сам
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
                if (parsed is not null) data = parsed;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            }
        }

        var docs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");

        if (GmcSmeta.Core.Demo.DemoMode.Active)
        {
            var workspace = Path.Combine(GmcSmeta.Core.Demo.DemoMode.ProfileRoot, "workspace");
            Directory.CreateDirectory(workspace);
            data["db_path"] = ToElement(Path.Combine(workspace, "workspace.marker"));
        }
        else if (!data.ContainsKey("db_path"))
            data["db_path"] = ToElement(DefaultDbPath());

        foreach (var key in new[] { "last_import_dir", "last_export_dir", "last_attach_dir" })
        {
            if (data.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String
                && IsProgramDir(v.GetString() ?? ""))
                data[key] = ToElement(docs);
            if (!data.ContainsKey(key))
                data[key] = ToElement(docs);
        }
        return data;
    }

    public static void SaveSettings(Dictionary<string, JsonElement> data)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(SettingsPath(), JsonSerializer.Serialize(data, options));
    }

    public static string DbPath(Dictionary<string, JsonElement> settings) =>
        settings.TryGetValue("db_path", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? DefaultDbPath()
            : DefaultDbPath();

    /// <summary>Путь внутри папки программы (её нельзя предлагать для сохранения: обновление её заменит).</summary>
    public static bool IsProgramDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Inside(path, ProgramDir()) || Inside(path, RunningDir());
    }

    private static bool Inside(string path, string dir)
    {
        var a = Path.GetFullPath(path).TrimEnd('\\', '/');
        var b = Path.GetFullPath(dir).TrimEnd('\\', '/');
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
               || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string CurrentUser()
    {
        var u = Environment.UserName;
        return string.IsNullOrWhiteSpace(u) ? "?" : u;
    }

    private static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value);
}
