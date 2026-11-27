using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

internal sealed class EstimateFormulaAdorner : Adorner
{
    internal sealed record Reference(int Number, EditableEstimateRow Row, DataGridColumn Column);
    private readonly DataGrid _grid;
    private IReadOnlyList<Reference> _refs = Array.Empty<Reference>();
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private static readonly (SolidColorBrush Ink, SolidColorBrush Fill, Pen Border)[] Colors =
        KsFormulaReferenceColors.Palette.Select(value =>
        {
            var color = (Color)ColorConverter.ConvertFromString(value);
            var ink = new SolidColorBrush(color);
            var fill = new SolidColorBrush(Color.FromArgb(30, color.R, color.G, color.B));
            ink.Freeze();
            fill.Freeze();
            var border = new Pen(ink, 2);
            border.Freeze();
            return (ink, fill, border);
        }).ToArray();

    public EstimateFormulaAdorner(DataGrid grid) : base(grid)
    {
        _grid = grid;
        IsHitTestVisible = false;
    }

    internal void Update(IReadOnlyList<Reference> refs)
    {
        if (_refs.SequenceEqual(refs)) return;
        _refs = refs;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_refs.Count == 0) return;
        var bounds = new Rect(_grid.RenderSize);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.PushClip(new RectangleGeometry(bounds));
        foreach (var reference in _refs)
        {
            if (_grid.ItemContainerGenerator.ContainerFromItem(reference.Row) is not DataGridRow row ||
                !ReferenceEquals(row.Item, reference.Row) || reference.Column.Visibility != Visibility.Visible ||
                reference.Column.GetCellContent(row) is not FrameworkElement content) continue;
            var cell = content;
            while (cell is not DataGridCell && VisualTreeHelper.GetParent(cell) is FrameworkElement parent)
                cell = parent;
            if (cell is not DataGridCell || !cell.IsVisible || !cell.IsDescendantOf(_grid)) continue;
            var rect = cell.TransformToAncestor(_grid).TransformBounds(new Rect(cell.RenderSize));
            var clip = bounds;
            for (DependencyObject? node = VisualTreeHelper.GetParent(cell); node is not null &&
                !ReferenceEquals(node, _grid); node = VisualTreeHelper.GetParent(node))
                if (node is ScrollContentPresenter viewport)
                {
                    clip.Intersect(viewport.TransformToAncestor(_grid).TransformBounds(new Rect(viewport.RenderSize)));
                    break;
                }
            rect.Intersect(clip);
            if (rect.IsEmpty || rect.Width < 4 || rect.Height < 4) continue;
            var (ink, fill, border) = Colors[(reference.Number - 1) % Colors.Length];
            rect.Inflate(-1, -1);
            dc.DrawRectangle(fill, border, rect);
            var label = new FormattedText(reference.Number.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface,
                10, Brushes.White, pixelsPerDip);
            var badge = new Rect(rect.Left + 2, rect.Top + 2, label.Width + 6, label.Height + 2);
            if (badge.Width < rect.Width && badge.Height < rect.Height)
            {
                dc.DrawRoundedRectangle(ink, null, badge, 2, 2);
                dc.DrawText(label, new Point(badge.Left + 3, badge.Top + 1));
            }
        }
        dc.Pop();
    }
}
