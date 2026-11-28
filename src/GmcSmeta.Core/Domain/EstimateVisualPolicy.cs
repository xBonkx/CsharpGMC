using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>Presentation only. No document mutation, calculation, storage or permission grant.</summary>
public static class EstimateVisualPolicy
{
    public sealed record Header(string Title, string FullTitle, string Object, string Kind, string Company);
    public static Header HeaderText(string fullTitle, string? annexTitle, string objectName,
        string kindTitle, string? company) => new(
        string.IsNullOrWhiteSpace(annexTitle) ? fullTitle : annexTitle,
        fullTitle, objectName, kindTitle == "Договор" ? "Приложение" : kindTitle,
        string.IsNullOrWhiteSpace(company) ? "организация не указана" : company);

    public static bool ShowAddActions(bool locked, bool automaticRest, bool canEdit) =>
        !locked && !automaticRest && canEdit;

    public static bool ShowWorkColumns(string? smrMode) => smrMode == "pos";

    // A user choice (including false) always wins. Automatic visibility is only
    // calculated at first publication of a page, not during subsequent readback.
    public static bool ShowPurchase(bool? chosen, bool hasPurchase, bool firstPublication, bool current) =>
        chosen ?? (firstPublication ? hasPurchase : current);

    public static double NormalizeZoom(double zoom) =>
        double.IsFinite(zoom) ? Math.Clamp(zoom, 0.8, 1.8) : 1.0;

    // WPF's application layout transform scales this local DIP size once.
    public static (double Body, double Header) TableFont(double bodyDip, double headerRatio, double zoom)
    {
        var normalized = NormalizeZoom(zoom);
        return (bodyDip * normalized, bodyDip * headerRatio * normalized);
    }

    private static readonly NumberFormatInfo DisplayNumber = CreateDisplayNumber();
    private static NumberFormatInfo CreateDisplayNumber()
    {
        var number = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        number.NumberDecimalSeparator = ",";
        number.NumberGroupSeparator = " ";
        number.NumberGroupSizes = new[] { 3 };
        number.NumberNegativePattern = 1;
        return NumberFormatInfo.ReadOnly(number);
    }

    public static string Money(decimal? value) =>
        value?.ToString("N2", DisplayNumber) ?? "";
    public static string Quantity(decimal value) => value.ToString("0.###", DisplayNumber);
}
