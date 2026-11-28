using System.Windows;
using System.Windows.Controls;

namespace GmcSmeta.App;

/// <summary>Equal-width cards with gaps only between visible cards; never extend the viewport
/// to compensate for per-card margins. Shadows are presentation only, not layout measurements.</summary>
public sealed class EqualGapTilePanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(EqualGapTilePanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is int n && n > 0);
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(EqualGapTilePanel),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double d && double.IsFinite(d) && d >= 0);
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private double _rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToArray();
        _rowHeight = 0;
        if (visible.Length == 0) return new Size();
        var columns = Math.Min(Columns, visible.Length);
        var rows = (visible.Length + columns - 1) / columns;
        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(0, (availableSize.Width - (columns - 1) * Gap) / columns)
            : double.PositiveInfinity;
        double naturalWidth = 0;
        foreach (var child in visible)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
            naturalWidth = Math.Max(naturalWidth, child.DesiredSize.Width);
        }
        var desiredWidth = double.IsFinite(width) ? width : naturalWidth;
        return new Size(desiredWidth * columns + (columns - 1) * Gap,
            _rowHeight * rows + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToArray();
        if (visible.Length == 0) return finalSize;
        var columns = Math.Min(Columns, visible.Length);
        var horizontalGap = columns == 1 ? 0 : Math.Min(Gap, finalSize.Width / (columns - 1));
        var width = Math.Max(0, (finalSize.Width - (columns - 1) * horizontalGap) / columns);
        for (var i = 0; i < visible.Length; i++)
            visible[i].Arrange(new Rect((i % columns) * (width + horizontalGap),
                (i / columns) * (_rowHeight + Gap), width, _rowHeight));
        return finalSize;
    }
}
