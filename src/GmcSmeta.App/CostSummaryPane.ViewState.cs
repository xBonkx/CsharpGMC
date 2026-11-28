using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

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

    // Preserve identities rather than indices. Never reselect an unrelated row
    // after deletion, filtering or group rebuild. Offscreen rows stay virtual.
    private void ReplaceCostRows(DataGrid grid, IEnumerable source)
    {
        if (ReferenceEquals(grid.ItemsSource, source)) return;
        var hadSource = grid.ItemsSource is not null;
        var selected = grid.SelectionUnit != DataGridSelectionUnit.Cell
            ? grid.SelectedItems.Cast<object>().Select(CostViewKey).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var current = grid.CurrentCell;
        var currentKey = CostViewKey(current.Item);
        var scroll = CostScroll(grid);
        var vertical = scroll?.VerticalOffset ?? 0;
        var horizontal = scroll?.HorizontalOffset ?? 0;
        grid.ItemsSource = source;
        if (!hadSource) return;

        if (selected.Count > 0 || currentKey is not null)
        {
            var candidates = new Dictionary<string, object>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in grid.Items)
            {
                var key = CostViewKey(item);
                if (key is null || key != currentKey && !selected.Contains(key)) continue;
                if (!candidates.TryAdd(key, item)) duplicates.Add(key);
            }
            foreach (var key in duplicates) candidates.Remove(key);
            foreach (var (key, item) in candidates)
                if (selected.Contains(key) && !grid.SelectedItems.Contains(item)) grid.SelectedItems.Add(item);
            if (currentKey is not null && candidates.TryGetValue(currentKey, out var restored) &&
                current.Column is { Visibility: Visibility.Visible } column && grid.Columns.Contains(column))
            {
                var cell = new DataGridCellInfo(restored, column);
                grid.CurrentCell = cell;
                if (grid.SelectionUnit == DataGridSelectionUnit.Cell && !grid.SelectedCells.Contains(cell))
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
            }));
    }
}
