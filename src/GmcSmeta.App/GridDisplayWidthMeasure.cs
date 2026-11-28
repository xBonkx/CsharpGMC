using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GmcSmeta.App;

/// <summary>Measures displayed strings without realizing DataGrid containers.
/// A bounded per-pass cache avoids repeated shaping of money and units.</summary>
internal sealed class GridDisplayWidthMeasure(DataGrid grid)
{
    private readonly Typeface _typeface = new(grid.FontFamily, grid.FontStyle, FontWeights.Bold, grid.FontStretch);
    private readonly double _size = grid.FontSize;
    private readonly double _dpi = VisualTreeHelper.GetDpi(grid).PixelsPerDip;
    private readonly Dictionary<char, double> _digits = new();
    private readonly Dictionary<string, double> _texts = new(StringComparer.Ordinal);

    private double Shape(string text) => new FormattedText(text, CultureInfo.GetCultureInfo("ru-RU"),
        FlowDirection.LeftToRight, _typeface, _size, Brushes.Black, _dpi).WidthIncludingTrailingWhitespace;

    internal double Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (_texts.TryGetValue(text, out var cached)) return cached;
        double width;
        // Only simple numeric strings use the inexpensive additive path.
        if (text.All(ch => char.IsAsciiDigit(ch) || ch is ' ' or '\u00a0' or '\u202f' or ',' or '.' or '-' or '−' or '+' or '%' or '₽'))
        {
            width = 0;
            foreach (var ch in text)
            {
                if (!_digits.TryGetValue(ch, out var glyph))
                    _digits[ch] = glyph = Shape(ch.ToString());
                width += glyph;
            }
        }
        else width = Shape(text);
        if (_texts.Count < 4096) _texts.Add(text, width);
        return width;
    }

    internal double Header(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => Math.Ceiling(Shape(word) + 18)).DefaultIfEmpty(0).Max();

    // Match the estimate cell's horizontal inset and the optional 22-DIP
    // expand affordance, without changing its template or selection semantics.
    internal double Cell(string text, double extra = 0) => Math.Ceiling(Text(text) + 12 + extra);
}
