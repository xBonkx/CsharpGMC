using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class CostSummaryPane
{
    // Group legacy IDs are identical. Use the complete membership and label,
    // never select a changed group or an ambiguous duplicate.
    private static string? CostViewKey(object? item) => item switch
    {
        CostSummaryPreviewRow { IsGroup: true } group => "g:" +
            group.Name.Length.ToString(CultureInfo.InvariantCulture) + ":" + group.Name + ":" +
            string.Join(",", group.MemberIds.OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture))),
        CostSummaryPreviewRow { Id: > 0 } row => "p:" + row.Id.ToString(CultureInfo.InvariantCulture),
        CostSummaryPreviewRow row => "x:" + row.Id.ToString(CultureInfo.InvariantCulture) + ":" + row.Name,
        SmrExpensePreviewRow row => "e:" + row.Id.ToString(CultureInfo.InvariantCulture),
        CostTotalsPreviewRow row => "t:" + row.Key,
        _ => null
    };

    private static ScrollViewer? CostScroll(DataGrid grid) =>
        grid.Template?.FindName("DG_ScrollViewer", grid) as ScrollViewer;

    // Inspect only realized visual rows; never walk the entire data source to
    // obtain containers or disable recycling virtualization.
    private static IEnumerable<DataGridRow> RealizedCostRows(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is DataGridRow row) yield return row;
            else foreach (var nested in RealizedCostRows(child)) yield return nested;
        }
    }

    private static ScrollContentPresenter? CostViewport(ScrollViewer? scroll) =>
        scroll?.Template?.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter;

    private static double? VisibleCostRowY(DataGridRow row, ScrollContentPresenter viewport)
    {
        if (!row.IsVisible || row.ActualHeight <= 0 || !viewport.IsAncestorOf(row)) return null;
        var y = row.TranslatePoint(new Point(0, 0), viewport).Y;
        return double.IsFinite(y) && y < viewport.ActualHeight && y + row.ActualHeight > 0 ? y : null;
    }

    // Preserve identities rather than indices. Never reselect an unrelated row
    // after deletion, filtering or group rebuild. Offscreen rows stay virtual.
    private void ReplaceCostRows(DataGrid grid, IEnumerable source)
    {
        if (ReferenceEquals(grid.ItemsSource, source)) return;
        var hadSource = grid.ItemsSource is not null;
        var selected = grid.SelectionUnit != DataGridSelectionUnit.Cell
            ? grid.SelectedItems.Cast<object>().Select(CostViewKey).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var selectedCells = grid.SelectionUnit == DataGridSelectionUnit.Cell
            ? grid.SelectedCells.Where(cell => cell.IsValid && cell.Column.Visibility == Visibility.Visible)
                .Select(cell => (Key: CostViewKey(cell.Item), Column: cell.Column))
                .Where(cell => cell.Key is not null).ToArray()
            : [];
        var selectedCellKeys = selectedCells.Select(cell => cell.Key!).ToHashSet(StringComparer.Ordinal);
        var current = grid.CurrentCell;
        var currentKey = CostViewKey(current.Item);
        var scroll = CostScroll(grid);
        var vertical = scroll?.VerticalOffset ?? 0;
        var horizontal = scroll?.HorizontalOffset ?? 0;
        SummaryViewportAnchor.Row? top = null, active = null;
        if (scroll is not null && (!scroll.CanContentScroll ||
            VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Pixel) &&
            CostViewport(scroll) is { } viewport)
        {
            foreach (var row in RealizedCostRows(viewport))
            {
                if (CostViewKey(row.Item) is not { } key || VisibleCostRowY(row, viewport) is not { } y) continue;
                var anchor = new SummaryViewportAnchor.Row(key, y);
                if (top is null || y < top.Value.Y) top = anchor;
                if (ReferenceEquals(row.Item, current.Item)) active = anchor;
            }
        }
        var anchorCandidates = new Dictionary<string, object>(StringComparer.Ordinal);
        var duplicateAnchors = new HashSet<string>(StringComparer.Ordinal);
        grid.ItemsSource = source;
        if (!hadSource) return;
        if (top is not null || active is not null)
        {
            foreach (var item in grid.Items)
            {
                if (CostViewKey(item) is not { } key || key != top?.Key && key != active?.Key) continue;
                if (!anchorCandidates.TryAdd(key, item)) duplicateAnchors.Add(key);
            }
            foreach (var key in duplicateAnchors) anchorCandidates.Remove(key);
        }
        var anchorToRestore = SummaryViewportAnchor.Choose(active, top,
            anchorCandidates.Keys.ToHashSet(StringComparer.Ordinal));

        if (selected.Count > 0 || selectedCells.Length > 0 || currentKey is not null)
        {
            var candidates = new Dictionary<string, object>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in grid.Items)
            {
                var key = CostViewKey(item);
                if (key is null || key != currentKey && !selected.Contains(key) && !selectedCellKeys.Contains(key)) continue;
                if (!candidates.TryAdd(key, item)) duplicates.Add(key);
            }
            foreach (var key in duplicates) candidates.Remove(key);
            foreach (var (key, item) in candidates)
                if (selected.Contains(key) && !grid.SelectedItems.Contains(item)) grid.SelectedItems.Add(item);
            foreach (var (key, selectedColumn) in selectedCells)
            {
                if (key is null || !candidates.TryGetValue(key, out var item) ||
                    selectedColumn.Visibility != Visibility.Visible || !grid.Columns.Contains(selectedColumn)) continue;
                var cell = new DataGridCellInfo(item, selectedColumn);
                if (!grid.SelectedCells.Contains(cell)) grid.SelectedCells.Add(cell);
            }
            if (currentKey is not null && candidates.TryGetValue(currentKey, out var restored) &&
                current.Column is { Visibility: Visibility.Visible } column && grid.Columns.Contains(column))
            {
                var cell = new DataGridCellInfo(restored, column);
                grid.CurrentCell = cell;
                if (grid.SelectionUnit == DataGridSelectionUnit.Cell && selectedCells.Length == 0 && !grid.SelectedCells.Contains(cell))
                    grid.SelectedCells.Add(cell);
            }
        }

        // Guard delayed restoration against another reload, filter, or detach.
        var generation = _loadGeneration;
        if (grid.IsVisible)
            grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (!_attached || generation != _loadGeneration ||
                    !grid.IsVisible || !ReferenceEquals(grid.ItemsSource, source) ||
                    CostScroll(grid) is not { } target) return;
                target.ScrollToHorizontalOffset(horizontal);
                target.ScrollToVerticalOffset(vertical);
                if (anchorToRestore is not { } anchor ||
                    !anchorCandidates.TryGetValue(anchor.Key, out var item)) return;
                // ScrollIntoView realizes just the anchor, then correct its pixel
                // position. Recheck the source/generation after the layout pass.
                grid.ScrollIntoView(item);
                grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (!_attached || generation != _loadGeneration || !grid.IsVisible ||
                        !ReferenceEquals(grid.ItemsSource, source) || CostScroll(grid) is not { } updated ||
                        CostViewport(updated) is not { } updatedViewport ||
                        grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row ||
                        !ReferenceEquals(row.Item, item) || VisibleCostRowY(row, updatedViewport) is not { } actualY)
                        return;
                    updated.ScrollToVerticalOffset(SummaryViewportAnchor.Offset(
                        updated.VerticalOffset, actualY, anchor.Y, updated.ScrollableHeight));
                    updated.ScrollToHorizontalOffset(horizontal);
                }));
            }));
    }
}
