namespace GmcSmeta.Core.Domain;

/// <summary>Shipment quantities independent from SQL and UI formatting.</summary>
public static class ShipmentQuantityPolicy
{
    private const decimal Eps = 0.000001m;

    // Zero removes the line; nonzero values at/below the tolerance must not silently delete it.
    public static bool IsValidEdit(decimal quantity) =>
        quantity == 0m || (quantity > Eps && quantity <= 1_000_000_000m &&
            decimal.Round(quantity, 6) == quantity);

    public static string Format(decimal quantity, System.Globalization.CultureInfo culture) =>
        quantity.ToString("0.######", culture);

    public static bool TryParseEdit(string text, out decimal quantity)
    {
        quantity = 0m;
        if (string.IsNullOrWhiteSpace(text)) return true;
        return ProcurementDisplay.TryParseNumber(text, out quantity) && IsValidEdit(quantity);
    }

    public static bool ExceedsRemaining(decimal remaining, decimal requested) =>
        requested > remaining + Eps;

    // Python warns only if there have actually been receipts.
    public static bool ExceedsStock(decimal received, decimal issued, decimal requested) =>
        received > Eps && requested > Math.Max(0m, received - issued) + Eps;

    public static decimal Remaining(decimal byEstimate, decimal issued) =>
        Math.Max(0m, byEstimate - issued);

    public static decimal AvailableForChange(decimal byEstimate, decimal issued,
        decimal alreadyInFixedShipment) =>
        Remaining(byEstimate, issued) + Math.Max(0m, alreadyInFixedShipment);

    public static decimal SuggestedFromStock(decimal byEstimate, decimal issued,
        decimal received)
    {
        var remaining = Remaining(byEstimate, issued);
        var onStock = Math.Max(0m, received - issued);
        return onStock > 0m ? Math.Min(onStock, remaining) : remaining;
    }
}
