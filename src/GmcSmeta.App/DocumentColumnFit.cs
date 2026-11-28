using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace GmcSmeta.App;

/// <summary>Opt-in display measurement for document DTO columns. Only explicitly
/// annotated simple properties are read; no row containers, SQL or edit writes.
/// Manual widths and flex columns remain the user's responsibility.</summary>
internal sealed class DocumentColumnFit
{
    internal sealed record Input(DataGridColumn Column, string Path, string Header,
        double Preferred, string? Format, CultureInfo Culture, double Extra);
    private readonly DataGrid _grid;
    private readonly Func<DataGridColumn, double?> _preferred;
    private WeakReference<object>? _source;
    private Input[] _last = [];
    private (FontFamily Family, double Size, FontStyle Style, FontStretch Stretch, double Pixels) _font;
    private bool _valid, _queued;
    internal bool Suspended { get; set; }

    internal DocumentColumnFit(DataGrid grid, Func<DataGridColumn, double?> preferred)
    {
        _grid = grid;
        _preferred = preferred;
        grid.Loaded += (_, _) => Invalidate();
        grid.Unloaded += (_, _) => Suspended = false;
        grid.IsVisibleChanged += (_, _) => Request();
        // ItemCollection remains stable when its underlying source changes.
        // Observe replace/add/remove/reset as well as a fresh ItemsSource.
        CollectionChangedEventManager.AddHandler(grid.Items, (_, _) => Invalidate());
        grid.CellEditEnding += (_, e) =>
        {
            if (e.EditAction == DataGridEditAction.Commit) Invalidate();
        };
        grid.LayoutUpdated += (_, _) => Request();
    }

    private bool Ready() => !Suspended && !_queued && _grid.IsLoaded && _grid.IsVisible &&
        _grid.ActualWidth > 0 && _grid.Items is not IEditableCollectionView { IsEditingItem: true } and
        not IEditableCollectionView { IsAddingNew: true };

    private (FontFamily Family, double Size, FontStyle Style, FontStretch Stretch, double Pixels) Font() =>
        (_grid.FontFamily, _grid.FontSize, _grid.FontStyle, _grid.FontStretch,
            VisualTreeHelper.GetDpi(_grid).PixelsPerDip);

    private IEnumerable<Input> Columns()
    {
        foreach (var column in _grid.Columns)
        {
            var path = TableColumnMenu.GetFitDisplayPath(column);
            if (column.Visibility != Visibility.Visible || !IsSimplePath(path) ||
                _preferred(column) is not { } width || !double.IsFinite(width) || width <= 0) continue;
            var culture = _grid.Language.GetSpecificCulture();
            string? format = null;
            if (column is DataGridTextColumn)
            {
                if (column is not DataGridTextColumn { Binding: Binding binding } ||
                    binding.Path?.Path != path || binding.Converter is not null ||
                    binding.Source is not null || binding.RelativeSource is not null ||
                    !string.IsNullOrEmpty(binding.ElementName)) continue;
                culture = binding.ConverterCulture ?? culture;
                format = binding.StringFormat;
            }
            else if (column is not DataGridTemplateColumn) continue;
            yield return new Input(column, path, TableColumnMenu.Title(column), width, format, culture,
                TableColumnMenu.GetFitExtra(column));
        }
    }

    private static bool IsSimplePath(string path) => path.Length > 0 &&
        (char.IsAsciiLetter(path[0]) || path[0] == '_') &&
        path.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    internal void Invalidate()
    {
        _valid = false;
        Request();
    }

    private bool Current(Input[] inputs) => _valid && _last.SequenceEqual(inputs) && _font.Equals(Font()) &&
        (_grid.ItemsSource is { } source ? _source is not null && _source.TryGetTarget(out var prior) &&
            ReferenceEquals(prior, source) : _source is null);

    internal void Request()
    {
        if (!Ready()) return;
        var inputs = Columns().ToArray();
        if (Current(inputs)) return;
        _queued = true;
        _grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _queued = false;
            if (!Ready()) return;
            var current = Columns().ToArray();
            if (!Current(current)) Apply(current);
        }));
    }

    internal static string Display(object? value, string? format, CultureInfo culture)
    {
        if (value is null) return "";
        // Match ordinary WPF StringFormat rules for explicitly annotated
        // scalar DTO bindings. No custom converters or nested binding paths.
        if (value is not (string or decimal or double or float or int or long or short or uint or ulong or ushort
            or byte or sbyte or DateTime or DateOnly or DateTimeOffset or TimeSpan)) return "";
        if (string.IsNullOrEmpty(format)) return Convert.ToString(value, culture) ?? "";
        if (format.StartsWith("{}", StringComparison.Ordinal)) format = format[2..];
        if (!format.Contains('{')) format = "{0:" + format + "}";
        try { return string.Format(culture, format, value); }
        catch (FormatException) { return Convert.ToString(value, culture) ?? ""; }
    }

    internal static double[] ReadWidths(DataGrid grid, Input[] inputs)
    {
        var measure = new GridDisplayWidthMeasure(grid);
        var widths = inputs.Select(c => Math.Max(c.Preferred, measure.Header(c.Header))).ToArray();
        if (inputs.Length == 0 || grid.ItemsSource is not { } source) return widths;
        // Cache public scalar property descriptors by type for this pass only.
        // The metadata is an explicit opt-in on each column; never walk a
        // nested path or invoke a value converter.
        var descriptors = new Dictionary<Type, PropertyDescriptor?[]>();
        foreach (var item in source)
        {
            if (item is null) continue;
            var type = item.GetType();
            if (!descriptors.TryGetValue(type, out var properties))
            {
                var all = TypeDescriptor.GetProperties(type);
                descriptors[type] = properties = inputs.Select(c => all.Find(c.Path, false)).ToArray();
            }
            for (int i = 0; i < inputs.Length; i++)
            {
                var property = properties[i];
                if (property is null) continue;
                string display;
                try { display = Display(property.GetValue(item), inputs[i].Format, inputs[i].Culture); }
                catch (Exception ex) when (ex is OverflowException or System.Reflection.TargetInvocationException)
                { continue; }
                widths[i] = Math.Max(widths[i], measure.Cell(display, inputs[i].Extra));
            }
        }
        return widths;
    }

    private void Apply(Input[] inputs)
    {
        var widths = ReadWidths(_grid, inputs);
        for (int i = 0; i < inputs.Length; i++)
        {
            var column = inputs[i].Column;
            var width = Math.Clamp(widths[i], column.MinWidth, Math.Max(column.MinWidth, column.MaxWidth));
            if (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - width) > .01)
                column.Width = new DataGridLength(width);
        }
        _source = _grid.ItemsSource is { } source ? new WeakReference<object>(source) : null;
        _last = inputs;
        _font = Font();
        _valid = true;
    }
}
