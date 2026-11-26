namespace GmcSmeta.Core.Domain;

/// <summary>Russian position count as in Python ship_view._pos.</summary>
public static class ShipmentCardText
{
    public static string PositionCount(int count)
    {
        var last = count % 10;
        var lastTwo = count % 100;
        var word = last == 1 && lastTwo != 11 ? "позиция"
            : last is 2 or 3 or 4 && lastTwo is not (12 or 13 or 14) ? "позиции"
            : "позиций";
        return $"{count} {word}";
    }
}
