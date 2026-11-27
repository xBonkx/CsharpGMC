using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace GmcSmeta.App;

internal static class GridSortInteraction
{
    internal static void Install() =>
        EventManager.RegisterClassHandler(typeof(DataGrid), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnLoaded));

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || !ReferenceEquals(e.OriginalSource, grid)) return;
        grid.Sorting -= OnSorting;
        grid.Sorting += OnSorting;
    }

    private static void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        // Custom projections handle their own order, including EstimateView.
        // Leave Shift multi-sort, custom comparers and grouped views unchanged.
        if (e.Handled || sender is not DataGrid grid || grid.Name == "EstimateGrid" ||
            grid.ItemsSource is null || Keyboard.Modifiers != ModifierKeys.None ||
            e.Column.SortDirection != ListSortDirection.Descending ||
            CollectionViewSource.GetDefaultView(grid.ItemsSource) is not ListCollectionView view ||
            !view.CanSort || view.IsEditingItem || view.IsAddingNew ||
            view.CustomSort is not null || view.GroupDescriptions.Count != 0 ||
            view.SortDescriptions.Count != 1) return;
        var descriptor = view.SortDescriptions[0];
        if (descriptor.Direction != ListSortDirection.Descending ||
            descriptor.PropertyName != e.Column.SortMemberPath) return;

        // Only the collection view changes; the underlying list is untouched.
        e.Handled = true;
        view.SortDescriptions.Clear();
        foreach (var column in grid.Columns) column.SortDirection = null;
    }
}
