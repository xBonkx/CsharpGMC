using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>Presentation-only selected quantities; does not change act lines or money.</summary>
public static class KsSelectionQuantity
{
    public static string Describe(IEnumerable<(string Unit, decimal NowValue)> rows, CultureInfo culture)
    {
        var selected = rows.ToList();
        if (selected.Count == 0) return "";
        var sums = new Dictionary<string, decimal>();
        var units = new Dictionary<string, string>();
        foreach (var r in selected)
        {
            var key = UnitNorm(r.Unit);
            if (!units.ContainsKey(key))
                units[key] = key switch
                {
                    "м2" => "м²",
                    "м3" => "м³",
                    "шт" => "шт.",
                    _ => r.Unit.Trim(),
                };
            sums[key] = sums.GetValueOrDefault(key) + r.NowValue;
        }
        var totals = sums.Select(kv => $"{Qty(decimal.Round(kv.Value, 3), culture)} {units[kv.Key]}".Trim());
        return $"Выделено: {selected.Count}  ·  Объём в акте: " + string.Join("  ·  ", totals);
    }

    private static string UnitNorm(string unit) => unit.Trim().ToLowerInvariant()
        .TrimEnd('.').Replace("²", "2").Replace("³", "3");

    private static string Qty(decimal v, CultureInfo culture) => v == decimal.Truncate(v)
        ? v.ToString("0", culture)
        : v.ToString("0.###", culture);
}
