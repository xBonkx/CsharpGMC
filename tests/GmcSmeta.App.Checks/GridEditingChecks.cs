using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GmcSmeta.App;

/// <summary>Real WPF routed events and editors on synthetic rows; no SQL or physical input.</summary>
internal static class GridEditingChecks
{
    public sealed class Row { public string Value { get; set; } = "Исходное значение"; }

    internal static void Run()
    {
        GridEditInteraction.Install();
        var row = new Row();
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false };
        grid.Columns.Add(new DataGridTextColumn { Binding = new Binding(nameof(Row.Value)), Width = 240 });
        grid.Columns.Add(new DataGridTextColumn { Binding = new Binding(nameof(Row.Value)), Width = 240 });
        grid.ItemsSource = new[] { row };
        grid.PreparingCellForEdit += (_, e) => GridEditInteraction.FocusTextEditor(grid, e);
        var host = new Window { Content = grid, Width = 600, Height = 240, ShowInTaskbar = false, ShowActivated = false };
        host.Show();
        host.Activate();
        try
        {
            host.UpdateLayout();
            var cells = Descendants<DataGridCell>(grid).OrderBy(c => c.Column.DisplayIndex).ToArray();
            Require(cells.Length == 2, "Synthetic cells were not realized.");
            foreach (var cell in cells.Concat(cells.Take(1)))
            {
                grid.CurrentCell = new DataGridCellInfo(row, cell.Column);
                Require(cell.Focus(), "Synthetic cell did not acquire keyboard focus.");
                Click(cell, 2);
                Pump();
                Require(cell.IsEditing, "Second click did not open the current cell editor.");
                var editor = GridEditInteraction.FindTextEditor(cell)!;
                Require(editor is not null && editor.SelectionLength == editor.Text.Length,
                    "Editor did not select the original text.");
                editor!.Text = "Новый ввод";
                grid.CancelEdit();
                Pump();
                Require(row.Value == "Исходное значение", "Cancel changed the source row.");
            }
            var target = cells[0];
            grid.CurrentCell = new DataGridCellInfo(row, target.Column);
            target.Focus();
            grid.IsReadOnly = true;
            Click(target, 2);
            Require(!target.IsEditing, "Read-only grid entered edit.");
            grid.IsReadOnly = false;
            EventHandler<DataGridBeginningEditEventArgs> reject = (_, e) => e.Cancel = true;
            grid.BeginningEdit += reject;
            Click(target, 2);
            Require(!target.IsEditing, "Double-click bypassed BeginningEdit guard.");
            grid.BeginningEdit -= reject;
            Click(target, 2);
            Pump();
            GridEditInteraction.FindTextEditor(target)!.Text = "Сохранено";
            Require(grid.CommitEdit(DataGridEditingUnit.Row, true) && row.Value == "Сохранено",
                "Native commit lost the entered text.");
            Console.WriteLine("Grid routed double-click, adjacent/repeated cells, cancel, commit and edit guards passed (no SQL).");
        }
        finally { host.Close(); }
    }

    private static void Click(UIElement source, int count)
    {
        var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent };
        // WPF normally fills this during physical input; keep the production routed handler unchanged.
        typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!.SetValue(e, count);
        source.RaiseEvent(e);
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
