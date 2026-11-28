using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmcSmeta.Core;

/// <summary>Preferences for the WPF client only. Never use Python's GMC-Smeta directory.</summary>
public sealed record WpfSettings
{
    public int FormatVersion { get; init; } = 1;
    public string FontFamily { get; init; } = "Segoe UI";
    public decimal Scale { get; init; } = 1.00m;
    public string Theme { get; init; } = "light";
    public string ExportDirectory { get; init; } = "";
    public bool ConfirmDeletion { get; init; } = true;
    public bool ShowHelpHints { get; init; } = true;
    public string NavigationMode { get; init; } = "contracts";
    // Размер главного окна в «нормальном» состоянии (0 — не задан, берётся 1480×880 по умолчанию).
    public double WindowWidth { get; init; }
    public double WindowHeight { get; init; }
    public bool WindowMaximized { get; init; }

    public void Validate()
    {
        if (FormatVersion != 1)
            throw new InvalidDataException("Неподдерживаемая версия настроек WPF.");
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 80 ||
            FontFamily.Any(char.IsControl) ||
            FontFamily.IndexOfAny(['\\', '/', '<', '>']) >= 0)
            throw new InvalidDataException("Недопустимое название шрифта.");
        if (Scale is < 0.75m or > 1.75m)
            throw new InvalidDataException("Масштаб должен быть от 75% до 175%.");
        if (Theme is not ("light" or "dark" or "system"))
            throw new InvalidDataException("Недопустимая тема.");
        if (NavigationMode is not ("contracts" or "kp"))
            throw new InvalidDataException("Недопустимая вкладка навигации.");
        if (ExportDirectory is null)
            throw new InvalidDataException("Папка экспорта не может быть null.");
        if (ExportDirectory.Length > 0)
        {
            if (ExportDirectory.Any(char.IsControl) ||
                !Path.IsPathFullyQualified(ExportDirectory))
                throw new InvalidDataException("Папка экспорта должна быть абсолютным путём.");
            try { _ = Path.GetFullPath(ExportDirectory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException("Некорректная папка экспорта.", ex);
            }
        }
        if (WindowWidth == 0 && WindowHeight == 0)
            return;
        if (!double.IsFinite(WindowWidth) || !double.IsFinite(WindowHeight) ||
            WindowWidth is < 640 or > 16000 || WindowHeight is < 480 or > 16000)
            throw new InvalidDataException("Размер окна должен быть от 640×480 до 16000×16000.");
    }
}

public sealed record WpfSettingsLoad(WpfSettings Settings, bool Recovered,
    string? CorruptCopyPath);

/// <summary>
/// Atomic settings.json writer in %APPDATA%\GMC-Smeta-Next.
/// Invalid JSON is quarantined, never silently overwritten. Previous valid file is
/// retained as settings.json.bak. I/O failures are propagated to the caller.
/// </summary>
public sealed class WpfSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public string FilePath { get; }

    public WpfSettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            GmcSmeta.Core.Demo.DemoMode.ApplicationData,
            "GMC-Smeta-Next", "settings.json");
        if (string.IsNullOrWhiteSpace(FilePath))
            throw new ArgumentException("Путь настроек не задан.", nameof(filePath));
        FilePath = Path.GetFullPath(FilePath);
    }

    public WpfSettingsLoad Load()
    {
        if (!File.Exists(FilePath))
        {
            // A previous recovery may have quarantined settings.json without
            // saving again. Keep using the valid backup on subsequent launches.
            var backup = FilePath + ".bak";
            if (File.Exists(backup))
            {
                try { return new(ReadValidFile(backup), true, null); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
            }
            return new(new WpfSettings(), false, null);
        }

        try
        {
            return new(ReadValidFile(FilePath), false, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // A malformed file is evidence. Preserve it before using defaults or a .bak.
            var quarantine = FilePath + ".corrupt-" + Guid.NewGuid().ToString("N");
            File.Move(FilePath, quarantine);
            var backup = FilePath + ".bak";
            if (File.Exists(backup))
            {
                try { return new(ReadValidFile(backup), true, quarantine); }
                catch (Exception e) when (e is JsonException or InvalidDataException)
                {
                    // Keep the broken backup for inspection; do not delete it.
                }
            }
            return new(new WpfSettings(), true, quarantine);
        }
    }

    public void Save(WpfSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, "." + Path.GetFileName(FilePath) +
            "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output, settings, JsonOptions);
                output.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath))
                File.Replace(temp, FilePath, FilePath + ".bak");
            else
                File.Move(temp, FilePath);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static WpfSettings ReadValidFile(string path)
    {
        var settings = JsonSerializer.Deserialize<WpfSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Пустые настройки.");
        settings.Validate();
        return settings;
    }
}
