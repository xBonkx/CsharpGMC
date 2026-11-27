namespace GmcSmeta.Core.Domain;

/// <summary>View-only row geometry in DIPs; 27 at the normal 10pt body size.</summary>
public static class TableRowHeightPolicy
{
    public static double Minimum(double fontSize)
    {
        if (!double.IsFinite(fontSize) || fontSize <= 0) fontSize = 40d / 3d;
        // Reserve room for the line box, 4 DIP top/bottom cell padding
        // and a grid line. Never use a fixed height at enlarged fonts.
        return Math.Ceiling(fontSize * 1.35 + 9 - 1e-8);
    }

    public static double RowHeight(double fontSize, bool wrap) =>
        wrap ? double.NaN : Minimum(fontSize);
}
