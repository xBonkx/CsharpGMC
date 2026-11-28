using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    // null = automatic; older true/false JSON values retain the explicit choice.
    public bool? PurchaseVisible { get; set; }
    private bool _purchaseChoiceChanged;
    private JsonObject? _localBaseline;
    private static readonly object SaveGate = new();

    internal void ChoosePurchaseVisibility(bool visible)
    {
        PurchaseVisible = visible;
        _purchaseChoiceChanged = true;
    }

    internal void ResetPurchaseVisibility()
    {
        PurchaseVisible = null;
        _purchaseChoiceChanged = true;
    }

    internal void RefreshPurchasePreference()
    {
        if (!_purchaseChoiceChanged && File.Exists(FilePath))
            PurchaseVisible = LoadFromPath(FilePath).PurchaseVisible;
    }
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
            var settings = File.Exists(path)
                ? Normalize(JsonSerializer.Deserialize<EstimateUiSettings>(File.ReadAllText(path))
                    ?? new EstimateUiSettings())
                : new EstimateUiSettings();
            settings._localBaseline = Snapshot(settings);
            return settings;
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

    private static JsonObject Snapshot(EstimateUiSettings settings) =>
        JsonSerializer.SerializeToNode(settings)!.AsObject();

    // Cached pages have separate settings instances. On reattach apply the
    // latest layout, without treating it as a new user modification.
    internal void RefreshColumnPreferences()
    {
        if (!File.Exists(FilePath)) return;
        var latest = LoadFromPath(FilePath);
        ColumnWidths = latest.ColumnWidths;
        ColumnOrder = latest.ColumnOrder;
        HiddenColumns = latest.HiddenColumns;
        AreaUnitVisible = latest.AreaUnitVisible;
        AreaTotalVisible = latest.AreaTotalVisible;
        BasisVisible = latest.BasisVisible;
        TableZoom = latest.TableZoom;
        RefreshPurchasePreference();
        if (_localBaseline is not null)
        {
            var refreshed = Snapshot(this);
            foreach (var name in new[] { nameof(ColumnWidths), nameof(ColumnOrder), nameof(HiddenColumns),
                nameof(AreaUnitVisible), nameof(AreaTotalVisible), nameof(BasisVisible), nameof(TableZoom) })
                _localBaseline[name] = refreshed[name]?.DeepClone();
        }
    }

    public void Save() => SaveToPath(FilePath);

    internal void SaveToPath(string path)
    {
        lock (SaveGate)
        {
            string? temporary = null;
            try
            {
                Normalize(this);
                var directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                var latest = File.Exists(path) ? LoadFromPath(path) : new EstimateUiSettings();
                // Only fields explicitly changed by this instance since its
                // last local snapshot may replace values from another page.
                if (!_purchaseChoiceChanged) PurchaseVisible = latest.PurchaseVisible;
                var local = Snapshot(this);
                var merged = Snapshot(latest);
                foreach (var (name, value) in local)
                {
                    if (name == nameof(PurchaseVisible) && _purchaseChoiceChanged ||
                        _localBaseline is null || !JsonNode.DeepEquals(_localBaseline[name], value))
                        merged[name] = value?.DeepClone();
                }
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, path, overwrite: true);
                // Keep the local baseline, not the merged snapshot: a hidden
                // cached page still has its old controls until reattached.
                _localBaseline = local;
                _purchaseChoiceChanged = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Personal UI settings are best-effort; never fail editing or SQL saves.
            }
            finally
            {
                if (temporary is not null)
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
