using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Fits only the embedded summary to its real scroll viewport. The
/// default layout fits the viewport; explicit user widths take precedence.</summary>
internal sealed class SummaryColumnFit
{
    private readonly DataGrid _grid;
    private readonly Func<DataGridColumn, double> _preferred;
    private readonly Func<bool> _manual;
    private bool _queued;
    // Cache identity only: a hidden grid may replace its source without another fit.
    // Never keep a superseded collection (and all its rows) alive here.
    private WeakReference<object>? _measuredSource;
    private double _measuredFontSize, _measuredDpi, _measuredPixelsPerDip;
    private FontFamily? _measuredFamily;
    private FontStyle _measuredStyle;
    private FontStretch _measuredStretch;
    private (DataGridColumn Column, string Key, string Header)[] _measuredColumns = [];
    private Dictionary<DataGridColumn, double>? _minimums;
    private (FontFamily Family, double Size, FontStyle Style, FontStretch Stretch, double Dpi, double PixelsPerDip)? _observedMetrics;

    internal bool Suspended { get; set; }

    internal SummaryColumnFit(DataGrid grid, Func<DataGridColumn, double> preferred, Func<bool>? manual = null)
    {
        _grid = grid;
        _preferred = preferred;
        _manual = manual ?? (() => false);
        grid.Loaded += (_, _) => Request();
        grid.Unloaded += (_, _) => Suspended = false;
        grid.IsVisibleChanged += (_, _) => Request();
        grid.SizeChanged += (_, e) => { if (e.WidthChanged) Request(); };
        grid.Columns.CollectionChanged += (_, _) => Request();
        grid.LayoutUpdated += (_, _) => ObserveMetrics();
        grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(ViewportChanged), true);
    }

    private void ObserveMetrics()
    {
        if (!_grid.IsLoaded || !_grid.IsVisible || Suspended) return;
        var dpi = VisualTreeHelper.GetDpi(_grid);
        var metrics = (_grid.FontFamily, _grid.FontSize, _grid.FontStyle, _grid.FontStretch,
            dpi.DpiScaleX, dpi.PixelsPerDip);
        if (_observedMetrics is { } previous && previous.Equals(metrics) &&
            (_manual() || _minimums is null || HeadersUnchanged())) return;
        _observedMetrics = metrics;
        Request();
    }

    private static string HeaderText(DataGridColumn column) =>
        column.Header is TextBlock text ? text.Text : column.Header?.ToString() ?? "";

    private bool HeadersUnchanged()
    {
        if (_measuredColumns.Length != _grid.Columns.Count) return false;
        for (int i = 0; i < _measuredColumns.Length; i++)
        {
            var column = _grid.Columns[i];
            var measured = _measuredColumns[i];
            if (!ReferenceEquals(measured.Column, column) || measured.Key != column.SortMemberPath ||
                measured.Header != HeaderText(column)) return false;
        }
        return true;
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
        // Repeated formatted amounts, units and blanks dominate many summaries.
        // Bound the text cache so a document with unique values cannot retain
        // every cell. This cache lives only for the current measurement pass.
        const int maxCachedTexts = 4096;
        var measuredTexts = new Dictionary<string, double>(StringComparer.Ordinal);
        double ShapedWidth(string value) => new FormattedText(value, CultureInfo.GetCultureInfo("ru-RU"),
            FlowDirection.LeftToRight, typeface, grid.FontSize, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
        double Measure(string value)
        {
            if (value.Length == 0) return 0;
            if (measuredTexts.TryGetValue(value, out var cached)) return cached;
            double width = 0;
            // Names/units need shaping (kerning, combining marks, fallback).
            // Monetary strings stay cheap on the 25k-row hot path.
            if (value.Any(ch => !char.IsAsciiDigit(ch) && ch is not (' ' or '\u00a0' or '\u202f' or ',' or '.' or '-' or '−' or '+' or '%' or '₽')))
            {
                width = ShapedWidth(value);
                if (measuredTexts.Count < maxCachedTexts) measuredTexts.Add(value, width);
                return width;
            }
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
            if (measuredTexts.Count < maxCachedTexts)
                measuredTexts.Add(value, width);
            return width;
        }

        var columns = grid.Columns.ToArray();
        var result = columns.ToDictionary(c => c, c => BaseMinimum(c.SortMemberPath));
        foreach (var column in columns)
        {
            foreach (var word in HeaderText(column).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                result[column] = Math.Max(result[column], Math.Ceiling(ShapedWidth(word) + 10));
        }
        if (grid.ItemsSource is IEnumerable<EstimateView.SummaryRow> rows)
        {
            var measuredColumns = columns.Where(c => c.SortMemberPath != "name")
                .Select(c => (Column: c, Key: c.SortMemberPath, Minimum: result[c])).ToArray();
            var maximums = measuredColumns.Select(c => c.Minimum).ToArray();
            foreach (var row in rows)
                for (int i = 0; i < measuredColumns.Length; i++)
                {
                    var value = Value(row, measuredColumns[i].Key);
                    if (value.Length != 0)
                        maximums[i] = Math.Max(maximums[i], Math.Ceiling(Measure(value) + 14));
                }
            for (int i = 0; i < measuredColumns.Length; i++)
                result[measuredColumns[i].Column] = maximums[i];
        }
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

    private bool IsMeasuredSource(object? source) =>
        source is null ? _measuredSource is null :
        _measuredSource is not null && _measuredSource.TryGetTarget(out var measured) &&
        ReferenceEquals(measured, source);

    internal IReadOnlyDictionary<DataGridColumn, double> GetMinimums(double dpi, double pixelsPerDip)
    {
        var source = _grid.ItemsSource;
        var columns = _grid.Columns.Select(c => (Column: c, Key: c.SortMemberPath,
            Header: HeaderText(c))).ToArray();
        if (_minimums is null || !IsMeasuredSource(source) ||
            _measuredFontSize != _grid.FontSize || _measuredDpi != dpi || _measuredPixelsPerDip != pixelsPerDip ||
            !Equals(_measuredFamily, _grid.FontFamily) || _measuredStyle != _grid.FontStyle ||
            _measuredStretch != _grid.FontStretch || !_measuredColumns.SequenceEqual(columns))
        {
            _minimums = ReadMinimums(_grid, pixelsPerDip);
            _measuredSource = source is null ? null : new WeakReference<object>(source);
            _measuredFontSize = _grid.FontSize;
            _measuredDpi = dpi;
            _measuredPixelsPerDip = pixelsPerDip;
            _measuredFamily = _grid.FontFamily;
            _measuredStyle = _grid.FontStyle;
            _measuredStretch = _grid.FontStretch;
            _measuredColumns = columns;
        }
        return _minimums;
    }

    private void Apply()
    {
        var viewport = Viewer()?.ViewportWidth ?? 0;
        if (!double.IsFinite(viewport) || viewport <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(_grid).DpiScaleX;
        var manual = _manual();
        var minimums = manual ? null : GetMinimums(dpi, VisualTreeHelper.GetDpi(_grid).PixelsPerDip);
        var visible = _grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToArray();
        var inputs = visible.Select(c => new EstimateColumnLayout.Column(c.SortMemberPath,
            _preferred(c), minimums is null ? BaseMinimum(c.SortMemberPath) : minimums[c],
            c.SortMemberPath == "name"
                ? EstimateColumnLayout.Role.Name : EstimateColumnLayout.Role.Fixed)).ToArray();
        var fitted = manual ? EstimateColumnLayout.Fit(inputs, viewport, manual: true) :
            Calculate(inputs, viewport, dpi);
        foreach (var column in visible)
        {
            // Content measurements guide automatic sizing, not the mouse gripper.
            var minimum = BaseMinimum(column.SortMemberPath);
            if (Math.Abs(column.MinWidth - minimum) > .001) column.MinWidth = minimum;
            var width = fitted[column.SortMemberPath];
            if (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - width) > .001)
                column.Width = new DataGridLength(width);
        }
        // If readable minima exceed the viewport, Auto intentionally keeps
        // horizontal scrolling available instead of clipping money values.
    }
}
