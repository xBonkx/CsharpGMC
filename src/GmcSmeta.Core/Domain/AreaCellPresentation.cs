using System.Globalization;
using System.Text.Json;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Python kp_editor._fill_area presentation; pure calculation without a
/// database dependency. This is evaluated only when an area column is shown.
/// </summary>
public static class AreaCellPresentation
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    public sealed record Cell(string Unit, string UnitEditor, string Total,
        string UnitTip, string TotalTip, bool Warning, bool Manual);

    public static Cell For(string? name, string? unit, decimal quantity,
        double? manual, bool isSummary)
    {
        if (isSummary)
        {
            var summaryText = Display(quantity, 3);
            return new Cell("", "", summaryText, "",
                "Сумма площадей вложенных строк. Раскрой строку для просмотра.", false, false);
        }

        var (area, how) = Area.AreaPerUnit(name, unit, manual);
        if (area is not double per || !double.IsFinite(per))
        {
            var warning = Area.IsDuctFamily(name) || Area.Material(name, unit) is not null;
            return new Cell("", "", "",
                warning ? $"Площадь не посчитана: {how}. Впиши м² на единицу."
                        : how, "", warning, false);
        }

        // Python rounds per-unit presentation to 3 places, raw editor to 4,
        // total to 2, with nearest-even (Python round).
        var roundedUnit = Math.Round(per, 3, MidpointRounding.ToEven);
        var rawUnit = Math.Round(per, 4, MidpointRounding.ToEven);
        var totalValue = per * (double)quantity;
        if (!double.IsFinite(totalValue))
            return new Cell("", "", "", "Некорректное значение площади.", "", true, manual is not null);

        var total = Math.Round(totalValue, 2, MidpointRounding.ToEven);
        return new Cell(
            Display(roundedUnit), Display(rawUnit, "0.####"), Display(total),
            $"{Display(rawUnit, "0.####")} м² на 1 {unit} — {how}. Двойной щелчок: задать свою площадь; пустое — считать автоматически.",
            $"{Display(rawUnit, "0.####")} × {Display(quantity, 3)} = {Display(Math.Round(totalValue, 3, MidpointRounding.ToEven))} м²",
            false, manual is not null);
    }

    public static string WorkCode(string basis, string? extraJson)
    {
        if (!string.IsNullOrWhiteSpace(basis) || string.IsNullOrWhiteSpace(extraJson))
            return basis ?? "";
        try
        {
            using var json = JsonDocument.Parse(extraJson);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("B", out var value))
                return value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? "" : value.ToString();
        }
        catch (JsonException) { /* bad import metadata; don't hide the estimate */ }
        return "";
    }

    /// <summary>Python area.refresh_summary: round only after summing child areas.</summary>
    public static decimal SummaryQuantity(
        IEnumerable<(string Name, string Unit, double Qty, double? Manual)> children)
    {
        double total = 0d;
        foreach (var child in children)
            total += Area.RowArea(child.Name, child.Unit, child.Qty, child.Manual) ?? 0d;
        if (!double.IsFinite(total) || total < 0d ||
            total > (double)NumberText.MaxNumber)
            throw new InvalidOperationException("Сводная площадь выходит за допустимый диапазон.");
        return decimal.Round(Convert.ToDecimal(total), 3, MidpointRounding.ToEven);
    }

    public static string BasisHeader(string form) =>
        string.Equals(form, "r4", StringComparison.OrdinalIgnoreCase)
            ? "Код работ" : "Обоснование";

    public static bool IsBasisAllowed(string form, bool basisRule) =>
        string.Equals(form, "r4", StringComparison.OrdinalIgnoreCase) || basisRule;

    public static bool TryParseManual(string? input, out decimal? manual)
    {
        manual = null;
        if (string.IsNullOrWhiteSpace(input)) return true;
        var value = NumberText.Parse(input);
        if (value is null || value < 0m || value > NumberText.MaxNumber)
            return false;
        manual = value.Value;
        return true;
    }

    private static string Display(double value, string format = "0.###") =>
        value.ToString(format, Ru);

    private static string Display(decimal value, int digits) =>
        Math.Round(value, digits, MidpointRounding.ToEven).ToString("0.###", Ru);
}
