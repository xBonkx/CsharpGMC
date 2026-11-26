using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Fits only the embedded summary to its real scroll viewport. The
/// persisted widths are preferences, not absolute constraints on small screens.</summary>
internal sealed class SummaryColumnFit
{
    private readonly DataGrid _grid;
    private readonly Func<DataGridColumn, double> _preferred;
    private bool _queued;
    private object? _measuredSource;
    private double _measuredFontSize, _measuredDpi;
    private Dictionary<DataGridColumn, double>? _minimums;

    internal bool Suspended { get; set; }

    internal SummaryColumnFit(DataGrid grid, Func<DataGridColumn, double> preferred)
    {
        _grid = grid;
        _preferred = preferred;
        grid.Loaded += (_, _) => Request();
        grid.Unloaded += (_, _) => Suspended = false;
        grid.IsVisibleChanged += (_, _) => Request();
        grid.SizeChanged += (_, e) => { if (e.WidthChanged) Request(); };
        grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ViewportChanged), true);
    }

    private ScrollViewer? Viewer() =>
        _grid.Template?.FindName("DG_ScrollViewer", _grid) as ScrollViewer;

    private void ViewportChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Viewer()) && Math.Abs(e.ViewportWidthChange) > .01)
            Request();
    }

    internal void Request()
    {
        if (_queued || Suspended || !_grid.IsLoaded || !_grid.IsVisible) return;
        _queued = true;
        _grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _queued = false;
            if (!Suspended && _grid.IsLoaded && _grid.IsVisible) Apply();
        }));
    }

    internal static double DefaultPreferred(string key) => key switch
    {
        "name" => 300, "unit" => 44, "qty" => 60, "markup" => 72,
        "mat_margin" or "work_margin" or "m_legacy_margin" or "w_legacy_margin" or "t_legacy_margin" => 90,
        _ => 80
    };

    private static double BaseMinimum(string key) => key switch
    {
        "name" => 160, "unit" => 36, "qty" => 44, "markup" => 54,
        "mat_margin" or "work_margin" or "m_legacy_margin" or "w_legacy_margin" or "t_legacy_margin" => 72,
        _ => 64
    };

    private static string Value(EstimateView.SummaryRow row, string key) => key switch
    {
        "unit" => row.Unit, "qty" => row.QtyText,
        "m_ucost" => row.CostUnitText, "m_uprice" => row.PriceUnitText,
        "m_mcost" => row.MCostText, "m_msell" => row.MSellText,
        "m_legacy_margin" => row.MatLegacyMarginText,
        "w_ucost" => row.WorkCostText, "w_usell" => row.WorkSellText,
        "w_wcost" => row.WCostText, "w_wsell" => row.WSellText,
        "w_legacy_margin" => row.WorkLegacyMarginText, "markup" => row.MarkupText,
        "t_cost" => row.TotalCostText, "t_sell" => row.TotalSellText,
        "t_legacy_margin" => row.TotalLegacyMarginText,
        "mat_margin" => row.MatMarginText, "work_margin" => row.WorkMarginText,
        _ => ""
    };

    // Measure the formatted values, including group/total rows, once per
    // source/font/DPI change. Cache individual glyph widths rather than
    // allocating FormattedText for every cell of a large document.
    internal static Dictionary<DataGridColumn, double> ReadMinimums(DataGrid grid, double dpi)
    {
        var typeface = new Typeface(grid.FontFamily, grid.FontStyle, FontWeights.Bold, grid.FontStretch);
        var glyphs = new Dictionary<char, double>();
        double Measure(string value)
        {
            double width = 0;
            foreach (var ch in value)
            {
                if (!glyphs.TryGetValue(ch, out var glyph))
                {
                    glyph = new FormattedText(ch.ToString(), CultureInfo.GetCultureInfo("ru-RU"),
                        FlowDirection.LeftToRight, typeface, grid.FontSize, Brushes.Black, dpi)
                        .WidthIncludingTrailingWhitespace;
                    glyphs[ch] = glyph;
                }
                width += glyph;
            }
            return width;
        }

        var columns = grid.Columns.ToArray();
        var result = columns.ToDictionary(c => c, c => BaseMinimum(c.SortMemberPath));
        foreach (var column in columns)
        {
            if (column.Header is TextBlock header)
                foreach (var word in header.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    result[column] = Math.Max(result[column], Math.Ceiling(Measure(word) + 10));
        }
        if (grid.ItemsSource is IEnumerable<EstimateView.SummaryRow> rows)
            foreach (var row in rows)
                foreach (var column in columns)
                    if (column.SortMemberPath != "name")
                        result[column] = Math.Max(result[column],
                            Math.Ceiling(Measure(Value(row, column.SortMemberPath)) + 14));
        return result;
    }

    // Work in integral physical pixels so independent WPF layout rounding
    // cannot add a fraction of a DIP per column and bring the scrollbar back.
    // Keep one physical pixel of slack for grid chrome.
    internal static IReadOnlyDictionary<string, double> Calculate(
        IReadOnlyList<EstimateColumnLayout.Column> inputs, double viewport, double dpi)
    {
        var pixels = inputs.Select(c => c with
        {
            Preferred = Math.Round(c.Preferred * dpi),
            Minimum = Math.Ceiling(c.Minimum * dpi)
        }).ToArray();
        var result = EstimateColumnLayout.Fit(pixels, Math.Max(0, Math.Floor(viewport * dpi) - 1));
        return result.ToDictionary(p => p.Key, p => p.Value / dpi);
    }

    private void Apply()
    {
        var viewport = Viewer()?.ViewportWidth ?? 0;
        if (!double.IsFinite(viewport) || viewport <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(_grid).DpiScaleX;
        if (_minimums is null || !ReferenceEquals(_measuredSource, _grid.ItemsSource) ||
            _measuredFontSize != _grid.FontSize || _measuredDpi != dpi)
        {
            _minimums = ReadMinimums(_grid, VisualTreeHelper.GetDpi(_grid).PixelsPerDip);
            _measuredSource = _grid.ItemsSource;
            _measuredFontSize = _grid.FontSize;
            _measuredDpi = dpi;
        }
        var visible = _grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToArray();
        var inputs = visible.Select(c => new EstimateColumnLayout.Column(c.SortMemberPath,
            _preferred(c), _minimums[c], c.SortMemberPath == "name"
                ? EstimateColumnLayout.Role.Name : EstimateColumnLayout.Role.Fixed)).ToArray();
        var fitted = Calculate(inputs, viewport, dpi);
        foreach (var column in visible)
        {
            var minimum = Math.Ceiling(_minimums[column] * dpi) / dpi;
            if (Math.Abs(column.MinWidth - minimum) > .001) column.MinWidth = minimum;
            var width = fitted[column.SortMemberPath];
            if (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - width) > .001)
                column.Width = new DataGridLength(width);
        }
        // If readable minima exceed the viewport, Auto intentionally keeps
        // horizontal scrolling available instead of clipping money values.
    }
}
