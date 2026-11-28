using System.IO;
using System.Text.Json;

namespace GmcSmeta.App;

/// <summary>
/// Local C# presentation preferences. Kept separate from the Python settings.json,
/// and never stored in SQL Server or used to change document data.
/// </summary>
internal sealed class EstimateUiSettings
{
    public bool TotalsHidden { get; set; } = false;
    // Python HintBar: «Понятно» закрывает подсказку навсегда (settings.hint_estimate).
    public bool EstimateHintHidden { get; set; } = false;
    // Python HintBar("cost"): подсказка вкладки «Материалы» закрывается так же.
    public bool MaterialsHintHidden { get; set; } = false;
    // Python HintBar("ks") и состояние вкладки «КС-2 / КС-3» (ks_view settings).
    public bool KsHintHidden { get; set; } = false;
    public bool KsListOpen { get; set; } = true;
    public bool KsTilesHidden { get; set; } = false;
    public bool PurchaseVisible { get; set; }
    public bool AreaUnitVisible { get; set; } = false;
    public bool AreaTotalVisible { get; set; } = false;
    public bool BasisVisible { get; set; } = false;
    public double TableZoom { get; set; } = 1.0;
    // Python main_window.set_font_scale: масштаб всего интерфейса (theme.FONT_SCALES).
    public double FontScale { get; set; } = 1.0;
    public bool WrapNames { get; set; } = true;
    public List<int> StageModeKp { get; set; } = new();
    public Dictionary<string, double> ColumnWidths { get; set; } = new();
    public List<string> ColumnOrder { get; set; } = new();
    public List<string> HiddenColumns { get; set; } = new();

    private static string FilePath => Path.Combine(
        GmcSmeta.Core.Demo.DemoMode.ApplicationData,
        "GMC-Smeta-Next", "estimate-ui.json");

    /// <summary>Путь к настройкам — для «О программе» (config.settings_path() в Python).</summary>
    internal static string SettingsFilePath => FilePath;

    public static EstimateUiSettings Load() => LoadFromPath(FilePath);

    /// <summary>Read preferences without allowing a damaged JSON file to break WPF startup.</summary>
    internal static EstimateUiSettings LoadFromPath(string path)
    {
        try
        {
            return File.Exists(path)
                ? Normalize(JsonSerializer.Deserialize<EstimateUiSettings>(File.ReadAllText(path))
                    ?? new EstimateUiSettings())
                : new EstimateUiSettings();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Do not silently overwrite the user's damaged preferences on the next Save().
            try { File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N")); }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                // Best-effort recovery; starting the app is more important than the backup.
            }
            return new EstimateUiSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EstimateUiSettings();
        }
    }

    /// <summary>Sanitize optional lists and UI zoom without changing document data.</summary>
    private static EstimateUiSettings Normalize(EstimateUiSettings settings)
    {
        if (!double.IsFinite(settings.FontScale) || settings.FontScale is < 0.9 or > 1.3)
            settings.FontScale = 1.0;
        settings.TableZoom = double.IsFinite(settings.TableZoom)
            ? Math.Clamp(settings.TableZoom, 0.8, 1.8) : 1.0;
        settings.StageModeKp ??= new List<int>();
        settings.ColumnWidths = (settings.ColumnWidths ?? new Dictionary<string, double>())
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) &&
                double.IsFinite(pair.Value) && pair.Value is >= 35 and <= 1600)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        settings.ColumnOrder = (settings.ColumnOrder ?? new List<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        settings.HiddenColumns = (settings.HiddenColumns ?? new List<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        return settings;
    }

    public void Save()
    {
        try
        {
            Normalize(this);
            var directory = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(directory);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Personal UI settings are best-effort; never fail editing or SQL saves.
        }
    }
}
