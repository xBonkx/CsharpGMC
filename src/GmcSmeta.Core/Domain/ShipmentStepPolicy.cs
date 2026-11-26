namespace GmcSmeta.Core.Domain;

/// <summary>Current step in the Python shipment workflow (ship_view.py). Four means all done.</summary>
public static class ShipmentStepPolicy
{
    public static int Current(bool isFixed, int lines, string? recipient, bool exported)
    {
        if (isFixed) return 4;
        if (lines <= 0) return 0;
        if (string.IsNullOrWhiteSpace(recipient)) return 1;
        return exported ? 3 : 2;
    }
}
