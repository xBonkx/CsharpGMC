using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Paints only the upper header tier. The native column headers
/// retain resize, mouse input and context menus; row virtualization is unchanged.</summary>
public sealed class SummaryHeaderBands : FrameworkElement
{
    public static readonly DependencyProperty SourceGridProperty = DependencyProperty.Register(
        nameof(SourceGrid), typeof(DataGrid), typeof(SummaryHeaderBands),
        new PropertyMetadata(null, SourceChanged));

    public DataGrid? SourceGrid
    {
        get => (DataGrid?)GetValue(SourceGridProperty);
        set => SetValue(SourceGridProperty, value);
    }

    private DataGrid? _subscribed;
    private DataGridColumnHeadersPresenter? _presenter;
    private SummaryHeaderLayout.Run[] _runs = [];
    private Rect _viewport = Rect.Empty;
    private double _dpi = 1;

    public SummaryHeaderBands()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Detach();
    }

    private static void SourceChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var bands = (SummaryHeaderBands)target;
        bands.Detach();
        if (bands.IsLoaded) bands.Subscribe();
    }

    private void Subscribe()
    {
        if (SourceGrid is not { } grid || ReferenceEquals(grid, _subscribed)) return;
        _subscribed = grid;
        grid.LayoutUpdated += LayoutChanged;
        Refresh();
    }

    private void Detach()
    {
        if (_subscribed is not null) _subscribed.LayoutUpdated -= LayoutChanged;
        _subscribed = null;
        _presenter = null;
        SetGeometry(Rect.Empty, []);
    }

    private void LayoutChanged(object? sender, EventArgs e) => Refresh();

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            else
                foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private void Refresh()
    {
        if (_subscribed is not { IsVisible: true } grid || !IsVisible)
        {
            SetGeometry(Rect.Empty, []);
            return;
        }
        if (_presenter is null || !_presenter.IsLoaded)
            _presenter = Descendants<DataGridColumnHeadersPresenter>(grid).FirstOrDefault();
        if (_presenter is not { IsVisible: true } presenter ||
            presenter.ActualWidth <= 0 || presenter.ActualHeight < 28 ||
            PresentationSource.FromVisual(this) is null ||
            PresentationSource.FromVisual(presenter) != PresentationSource.FromVisual(this))
        {
            SetGeometry(Rect.Empty, []);
            return;
        }

        var visible = grid.Columns.Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex).ToArray();
        var anchor = Descendants<DataGridColumnHeader>(presenter)
            .FirstOrDefault(h => h.Column is not null && visible.Contains(h.Column) && h.ActualWidth > 0);
        if (anchor is null)
        {
            SetGeometry(Rect.Empty, []);
            return;
        }
        // The realized header supplies the actual scroll origin, including
        // a partially visible first column; never guess from the row content.
        var origin = anchor.TranslatePoint(new Point(), this).X;
        foreach (var column in visible)
        {
            if (ReferenceEquals(column, anchor.Column)) break;
            origin -= column.ActualWidth;
        }
        var location = presenter.TranslatePoint(new Point(), this);
        var viewport = new Rect(location.X, location.Y, presenter.ActualWidth, 28);
        viewport.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
        SetGeometry(viewport, SummaryHeaderLayout.Build(
            visible.Select(c => new SummaryHeaderLayout.Column(c.SortMemberPath, c.ActualWidth)), origin).ToArray());
    }

    private void SetGeometry(Rect viewport, SummaryHeaderLayout.Run[] runs)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_viewport == viewport && _dpi == dpi && _runs.SequenceEqual(runs)) return;
        _viewport = viewport;
        _runs = runs;
        _dpi = dpi;
        InvalidateVisual();
    }

    private static SolidColorBrush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private sealed record Design(string Title, Brush Background, Brush Foreground, Geometry? Glyph, Brush Icon);
    private static Geometry Icon(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static readonly Dictionary<SummaryHeaderLayout.Band, Design> Designs = new()
    {
        [SummaryHeaderLayout.Band.Position] = new("ПОЗИЦИЯ", MakeBrush("#F0F4FA"), MakeBrush("#526782"), null, Brushes.Transparent),
        [SummaryHeaderLayout.Band.Materials] = new("МАТЕРИАЛЫ", MakeBrush("#E5F0FC"), MakeBrush("#286297"),
            Icon("M12 2l5 3-5 3-5-3z M7 5v5l5 3 5-3V5 M12 8v5 M6 11l5 3-5 3-5-3z M1 14v5l5 3 5-3v-5 M6 17v5 M18 11l5 3-5 3-5-3z M13 14v5l5 3 5-3v-5 M18 17v5"), MakeBrush("#2463EB")),
        [SummaryHeaderLayout.Band.Work] = new("СМР", MakeBrush("#F7E9D7"), MakeBrush("#92591D"),
            Icon("M14 6a5 5 0 0 0-6 6L2 18a2.8 2.8 0 0 0 4 4l6-6a5 5 0 0 0 6-6l-3 3-4-4 3-3z"), MakeBrush("#EA8313")),
        [SummaryHeaderLayout.Band.Total] = new("ИТОГО ПО ПОЗИЦИИ", MakeBrush("#DDEFE2"), MakeBrush("#2E7547"),
            Icon("M12 3 3 8l9 5 9-5z M3 13l9 5 9-5"), MakeBrush("#24934C"))
    };
    private static readonly Pen Separator = new(MakeBrush("#CBD8E9"), 1);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_viewport.IsEmpty || _runs.Length == 0 || SourceGrid is not { } grid) return;
        dc.PushClip(new RectangleGeometry(_viewport));
        foreach (var run in _runs)
        {
            var rect = new Rect(run.Left, _viewport.Y, run.Width, _viewport.Height);
            if (!rect.IntersectsWith(_viewport)) continue;
            var design = Designs[run.Group];
            dc.DrawRectangle(design.Background, null, rect);
            dc.DrawLine(Separator, rect.TopRight, rect.BottomRight);
            dc.PushClip(new RectangleGeometry(rect));
            var text = new FormattedText(design.Title, CultureInfo.GetCultureInfo("ru-RU"),
                FlowDirection.LeftToRight,
                new Typeface(grid.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                12, design.Foreground, _dpi);
            var extra = design.Glyph is null ? 0 : 24;
            var left = rect.Left + Math.Max(3, (rect.Width - text.Width - extra) / 2);
            if (design.Glyph is not null)
            {
                dc.PushTransform(new TranslateTransform(left, rect.Top + (rect.Height - 18) / 2));
                dc.PushTransform(new ScaleTransform(.75, .75));
                dc.DrawGeometry(null, new Pen(design.Icon, 1.9)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, design.Glyph);
                dc.Pop();
                dc.Pop();
            }
            dc.DrawText(text, new Point(left + extra, rect.Top + (rect.Height - text.Height) / 2));
            dc.Pop();
        }
        dc.Pop();
    }
}
