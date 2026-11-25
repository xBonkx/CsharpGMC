using System.IO;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.App;

/// <summary>Exercises the production layout/menu callbacks without SQL or
/// existing user settings. Does not simulate physical mouse routing.</summary>
internal static class ColumnMenuChecks
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GmcSmeta-column-check-" + Guid.NewGuid().ToString("N"));
        TableColumnMenu.TestProfileDirectory = directory;
        try
        {
            var grid = CreateGrid("first");
            var menu = Menu(grid);
            var name = Item(menu, "Наименование");
            var cost = Item(menu, "Закупка");
            Require(!name.IsEnabled, "Required name must be protected.");
            Require(cost.IsChecked && cost.IsEnabled && cost.StaysOpenOnClick, "Optional column must be checkable.");
            Require(!menu.Items.OfType<MenuItem>().Any(i => Equals(i.Header, "Режим")), "Mode-only column must be excluded.");
            Click(cost, false);
            Require(grid.Columns[1].Visibility == Visibility.Collapsed, "Hiding did not change visibility.");
            Require(File.Exists(Path.Combine(directory, "first.json")), "Layout was not saved.");

            var reopened = CreateGrid("first");
            Require(reopened.Columns[1].Visibility == Visibility.Collapsed, "Hidden column did not survive reopening.");
            var other = CreateGrid("second");
            Require(other.Columns[1].Visibility == Visibility.Visible, "Different table inherited hidden column.");
            // Reuse the same grid under a different key and restore original defaults.
            TableColumnMenu.RefreshDynamic(reopened, "second", ownHeaderMenu: true);
            Require(reopened.Columns[1].Visibility == Visibility.Visible, "Changing profile retained old hidden columns.");
            TableColumnMenu.RefreshDynamic(reopened, "first", ownHeaderMenu: true);
            Require(reopened.Columns[1].Visibility == Visibility.Collapsed, "Returning to profile lost hidden columns.");
            var restoredMenu = Menu(reopened);
            var restored = Item(restoredMenu, "Закупка");
            Require(!restored.IsChecked, "Hidden column must still appear in menu.");
            Click(restored, true);
            Require(reopened.Columns[1].Visibility == Visibility.Visible, "Showing column did not work.");
            Require(CreateGrid("first").Columns[1].Visibility == Visibility.Visible, "Show state did not persist.");
            // Show-all must not expose the mode-controlled column.
            Click(Item(restoredMenu, "Закупка"), false);
            Item(restoredMenu, "Показать все столбцы").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(reopened.Columns[1].Visibility == Visibility.Visible &&
                reopened.Columns[2].Visibility == Visibility.Collapsed, "Show-all changed a mode-controlled column.");
            var optional = CreateGrid("optional");
            var optionalMenu = Menu(optional);
            var legacy = Item(optionalMenu, "Доп. маржа");
            Require(!legacy.IsChecked && legacy.IsEnabled, "Default-hidden column must be available in menu.");
            Click(legacy, true);
            Require(CreateGrid("optional").Columns[3].Visibility == Visibility.Visible, "Explicit show did not persist.");
            Require(CreateGrid("another").Columns[3].Visibility == Visibility.Collapsed, "Default hidden leaked to another profile.");
            var again = CreateGrid("optional");
            TableColumnMenu.RefreshDynamic(again, "another", ownHeaderMenu: true);
            Require(again.Columns[3].Visibility == Visibility.Collapsed, "Profile switch did not restore hidden default.");
            TableColumnMenu.RefreshDynamic(again, "optional", ownHeaderMenu: true);
            Require(again.Columns[3].Visibility == Visibility.Visible, "Explicit show lost on profile switch.");
            Click(Item(Menu(again), "Доп. маржа"), false);
            Require(CreateGrid("optional").Columns[3].Visibility == Visibility.Collapsed, "Explicit hide did not persist.");
            Console.WriteLine("Column menu profile/visibility checks passed (no SQL).");
        }
        finally
        {
            TableColumnMenu.TestProfileDirectory = null;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static DataGrid CreateGrid(string key)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Наименование", SortMemberPath = "name" });
        grid.Columns.Add(new DataGridTextColumn { Header = "Закупка", SortMemberPath = "cost" });
        grid.Columns.Add(new DataGridTextColumn { Header = "Режим", SortMemberPath = "mode", Visibility = Visibility.Collapsed });
        var legacy = new DataGridTextColumn { Header = "Доп. маржа", SortMemberPath = "legacy", Visibility = Visibility.Collapsed };
        TableColumnMenu.SetDefaultHidden(legacy, true);
        grid.Columns.Add(legacy);
        TableColumnMenu.RefreshDynamic(grid, key, ownHeaderMenu: true);
        return grid;
    }

    private static MenuItem Menu(DataGrid grid)
    {
        var root = new ContextMenu();
        TableColumnMenu.AppendDynamicMenu(grid, root);
        return Item(root, "Столбцы");
    }

    private static MenuItem Item(ItemsControl menu, string header) =>
        menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, header));

    private static void Click(MenuItem item, bool show)
    {
        item.IsChecked = show;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
