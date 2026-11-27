using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GmcSmeta.App;

/// <summary>Paints the insertion edge without altering the measured row or intercepting the mouse.</summary>
internal sealed class EstimateInsertionAdorner : Adorner
{
    private readonly DataGrid _grid;
    private DataGridRow? _row;
    private object? _item;
    private bool _before;
    private static readonly Pen Line = CreatePen();

    private static Pen CreatePen()
    {
        var pen = new Pen(Brushes.DodgerBlue, 2);
        pen.Freeze();
        return pen;
    }

    public EstimateInsertionAdorner(DataGrid grid) : base(grid)
    {
        _grid = grid;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public void Show(DataGridRow row, bool before)
    {
        _row = row;
        _item = row.Item;
        _before = before;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_row is not { } row || !ReferenceEquals(_item, row.Item) ||
            !row.IsVisible || !row.IsDescendantOf(_grid)) return;
        var edge = row.TranslatePoint(new Point(0, _before ? 0 : row.ActualHeight), _grid).Y;
        var bounds = new Rect(_grid.RenderSize);
        // Keep the edge inside the rows viewport, never over the header or scrollbars.
        for (DependencyObject? node = VisualTreeHelper.GetParent(row);
            node is not null && !ReferenceEquals(node, _grid);
            node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ScrollContentPresenter viewport) continue;
            bounds.Intersect(viewport.TransformToAncestor(_grid)
                .TransformBounds(new Rect(viewport.RenderSize)));
            break;
        }
        if (bounds.IsEmpty || edge < bounds.Top - 0.5 || edge > bounds.Bottom + 0.5 ||
            bounds.Height < 2 || bounds.Width < 2) return;
        var y = Math.Clamp(edge, bounds.Top + 1, bounds.Bottom - 1);
        dc.PushClip(new RectangleGeometry(bounds));
        dc.DrawLine(Line, new Point(bounds.Left, y), new Point(bounds.Right, y));
        dc.Pop();
    }
}
