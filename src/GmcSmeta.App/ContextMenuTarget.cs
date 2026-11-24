using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace GmcSmeta.App;

/// <summary>Resolve the actual row of a context-menu invocation, not a stale selection.
/// Full-row grids preserve a multi-selection only when the clicked row belongs to it.</summary>
internal static class ContextMenuTarget
{
    /// <summary>The exact cell targeted by the invocation; no fallback for mouse clicks.</summary>
    internal static DataGridCellInfo TargetCell(DataGrid grid, ContextMenuEventArgs e)
    {
        if (e.CursorLeft < 0 && e.CursorTop < 0) return grid.CurrentCell;
        for (DependencyObject? node = grid.InputHitTest(Mouse.GetPosition(grid)) as DependencyObject;
             node is not null && node != grid;)
        {
            if (node is DataGridCell cell)
                return new DataGridCellInfo(cell);
            node = node is Visual || node is Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return default;
    }

    internal static T? SelectRow<T>(DataGrid grid, ContextMenuEventArgs e) where T : class
    {
        if (e.CursorLeft < 0 && e.CursorTop < 0)
            return grid.CurrentItem is T current && grid.SelectedItems.Contains(current)
                ? current
                : grid.SelectedItem as T;

        var point = Mouse.GetPosition(grid);
        for (DependencyObject? node = grid.InputHitTest(point) as DependencyObject;
             node is not null && node != grid;)
        {
            if (node is DataGridRow row)
            {
                if (row.Item is not T item || !grid.Items.Contains(item)) return null;
                if (!grid.SelectedItems.Contains(item))
                {
                    grid.UnselectAll();
                    grid.SelectedItem = item;
                }
                return grid.SelectedItems.Contains(item) ? item : null;
            }
            node = node is Visual || node is Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        // Headers, scrollbar and empty background have no row target.
        return null;
    }
}
