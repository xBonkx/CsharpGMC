using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace GmcSmeta.App;

/// <summary>Content-aware default widths for the compatibility cost tables.
/// Only plain, already formatted DTO values are inspected; explicit manual
/// widths, textual flex columns and all edit/write paths remain untouched.</summary>
internal sealed class LegacyCostColumnFit
{
    private readonly DataGrid _grid;
    private readonly Func<DataGridColumn, double?> _preferred;
    private bool _queued;
    private WeakReference<object>? _source;
    private (FontFamily Family, double Size, FontStyle Style, FontStretch Stretch, double Dpi, double Pixels) _font;
    private (DataGridColumn Column, string Path, string Header, double Preferred)[] _columns = [];
    private bool _measured;

    internal bool Suspended { get; set; }

    internal LegacyCostColumnFit(DataGrid grid, Func<DataGridColumn, double?> preferred)
    {
        _grid = grid;
        _preferred = preferred;
        grid.Loaded += (_, _) => Request();
        grid.Unloaded += (_, _) => Suspended = false;
        grid.IsVisibleChanged += (_, _) => Request();
        grid.LayoutUpdated += (_, _) =>
        {
            if (Ready() && !Current()) Request();
        };
    }

    private bool Ready() => !Suspended && !_queued && _grid.IsLoaded && _grid.IsVisible &&
        _grid.ActualWidth > 0 && _grid.Items is not IEditableCollectionView { IsEditingItem: true } and
        not IEditableCollectionView { IsAddingNew: true };

    private (FontFamily Family, double Size, FontStyle Style, FontStretch Stretch, double Dpi, double Pixels) Font()
    {
        var dpi = VisualTreeHelper.GetDpi(_grid);
        return (_grid.FontFamily, _grid.FontSize, _grid.FontStyle, _grid.FontStretch, dpi.DpiScaleX, dpi.PixelsPerDip);
    }

    private static string? NumericPath(DataGridColumn column)
    {
        if (column is not DataGridTextColumn { Binding: Binding binding } ||
            binding.Converter is not null || !string.IsNullOrEmpty(binding.StringFormat)) return null;
        var path = binding.Path?.Path;
        return path is "Unit" or "Qty" or "UCost" or "UPrice" or "SummaryUnitPrice" or "UWcost" or "UWsell"
            or "MCost" or "MSell" or "MatMargin" or "WCost" or "WSell" or "WorkMargin" or "TotalCost" or "TotalSell"
            or "TotalMargin" or "MarkupPct" or "MatMarkupPct" or "Amount" or "Date" or "Cost" or "Sell" or "Margin" or "MarginPct"
                ? path : null;
    }

    private IEnumerable<(DataGridColumn Column, string Path, string Header, double Preferred)> Columns()
    {
        foreach (var column in _grid.Columns)
            if (column.Visibility == Visibility.Visible && NumericPath(column) is { } path &&
                _preferred(column) is { } width && double.IsFinite(width) && width > 0)
                yield return (column, path, column.Header?.ToString() ?? "", width);
    }

    private bool Current()
    {
        var source = _grid.ItemsSource;
        return _measured && (source is null ? _source is null :
            _source is not null && _source.TryGetTarget(out var prior) && ReferenceEquals(prior, source)) &&
            _font.Equals(Font()) && _columns.SequenceEqual(Columns());
    }

    internal void Request()
    {
        if (!Ready()) return;
        _queued = true;
        _grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _queued = false;
            if (Ready() && !Current()) Apply();
        }));
    }

    internal static string Value(object item, string path) => item switch
    {
        CostSummaryPreviewRow r => path switch
        {
            "Unit" => r.Unit, "Qty" => r.Qty, "UCost" => r.UCost, "UPrice" => r.UPrice,
            "SummaryUnitPrice" => r.SummaryUnitPrice, "UWcost" => r.UWcost, "UWsell" => r.UWsell,
            "MCost" => r.MCost, "MSell" => r.MSell, "MatMargin" => r.MatMargin, "WCost" => r.WCost,
            "WSell" => r.WSell, "WorkMargin" => r.WorkMargin, "TotalCost" => r.TotalCost,
            "TotalSell" => r.TotalSell, "TotalMargin" => r.TotalMargin,
            "MarkupPct" => r.MarkupPct, "MatMarkupPct" => r.MatMarkupPct, _ => ""
        },
        CostSummaryPane.SmrExpensePreviewRow r => path switch { "Date" => r.Date, "Amount" => r.Amount, _ => "" },
        CostSummaryPane.CostTotalsPreviewRow r => path switch
        {
            "Cost" => r.Cost, "Sell" => r.Sell, "Margin" => r.Margin, "MarginPct" => r.MarginPct, _ => ""
        },
        _ => ""
    };

    internal static double[] ReadWidths(DataGrid grid,
        (DataGridColumn Column, string Path, string Header, double Preferred)[] columns)
    {
        var measure = new GridDisplayWidthMeasure(grid);
        var widths = columns.Select(c => Math.Max(c.Preferred, measure.Header(c.Header))).ToArray();
        if (grid.ItemsSource is { } source)
            foreach (var row in source)
                if (row is not null)
                    for (int i = 0; i < columns.Length; i++)
                        widths[i] = Math.Max(widths[i], measure.Cell(Value(row, columns[i].Path)));
        return widths;
    }

    private void Apply()
    {
        var columns = Columns().ToArray();
        if (columns.Length > 0)
        {
            var widths = ReadWidths(_grid, columns);
            for (int i = 0; i < columns.Length; i++)
            {
                var column = columns[i].Column;
                var width = Math.Clamp(widths[i], column.MinWidth, Math.Max(column.MinWidth, column.MaxWidth));
                if (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - width) > .01)
                    column.Width = new DataGridLength(width);
            }
        }
        _source = _grid.ItemsSource is { } source ? new WeakReference<object>(source) : null;
        _font = Font();
        _columns = columns;
        _measured = true;
    }

    internal void Invalidate()
    {
        _measured = false;
        Request();
    }
}
