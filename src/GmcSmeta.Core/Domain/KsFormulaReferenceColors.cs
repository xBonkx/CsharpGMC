using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Python 0.67.9 ks_view.py QtyDelegate reference highlight: the exact colour
/// palette, its cycling order, the translucent fill alpha and the border width.
/// Shared by the WPF editor and the offline parity checks so these constants
/// cannot drift from the Python source.
/// </summary>
public static class KsFormulaReferenceColors
{
    /// <summary>ks_view.py REFERENCE_COLORS, in the same order.</summary>
    public static readonly IReadOnlyList<string> Palette = new[]
    {
        "#2563EB", "#DC2626", "#16803C", "#9333EA",
        "#C2410C", "#087F8C", "#A21CAF", "#946200"
    };

    /// <summary>QColor.setAlpha(30) fill drawn over a referenced cell.</summary>
    public const byte ShadeAlpha = 30;

    /// <summary>QPen(QColor(color), 2) border drawn around a referenced cell.</summary>
    public const int BorderThickness = 2;

    /// <summary>ks_view.py col_tint for S_NOW ("В этом акте"): the fill base.</summary>
    public const string NowCellBackground = "#DCE8FF";

    public static string ColorAt(int index) => Palette[Wrap(index)];

    public static (byte R, byte G, byte B) RgbAt(int index)
    {
        var hex = ColorAt(index);
        return (
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
    }

    public static (byte A, byte R, byte G, byte B) ShadeAt(int index)
    {
        var (r, g, b) = RgbAt(index);
        return (ShadeAlpha, r, g, b);
    }

    /// <summary>
    /// Opaque equivalent of Python's <c>fillRect</c> with <c>setAlpha(30)</c> over
    /// <paramref name="backgroundHex"/>. Qt blends as
    /// <c>(src*alpha + dst*(255-alpha)) / 255</c> with half-up rounding; WPF cannot
    /// layer the translucent fill over the cell's own background, so the editor
    /// binds this pre-composited colour instead.
    /// </summary>
    public static string ShadeOver(int index, string backgroundHex)
    {
        if (backgroundHex is not { Length: 7 } || backgroundHex[0] != '#')
            throw new ArgumentException("Expected #RRGGBB.", nameof(backgroundHex));
        var (r, g, b) = RgbAt(index);
        return $"#{Blend(r, Parse(backgroundHex, 1)):X2}" +
               $"{Blend(g, Parse(backgroundHex, 3)):X2}" +
               $"{Blend(b, Parse(backgroundHex, 5)):X2}";
    }

    private static byte Parse(string hex, int start) =>
        Convert.ToByte(hex.Substring(start, 2), 16);

    private static byte Blend(byte source, byte background) =>
        (byte)((source * ShadeAlpha + background * (255 - ShadeAlpha) + 127) / 255);

    private static int Wrap(int index)
    {
        var count = Palette.Count;
        var wrapped = index % count;
        return wrapped < 0 ? wrapped + count : wrapped;
    }
}
