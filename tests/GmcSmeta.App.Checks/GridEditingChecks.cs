using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.ComponentModel;
using System.Windows.Controls.Primitives;
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
        RunSelectionAndSort();
        RunPreviewRangePaste();
        var row = new Row();
        var font = GmcSmeta.Core.Domain.EstimateVisualPolicy.TableFont(
            (double)Application.Current.FindResource("UiBodySize"), .95, 1.2);
        var editorStyle = (Style)Application.Current.FindResource("PythonGridEditor");
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false,
            FontSize = font.Body, FontFamily = (FontFamily)Application.Current.FindResource("UiFontFamily") };
        grid.Columns.Add(new DataGridTextColumn { Binding = new Binding(nameof(Row.Value)), Width = 240,
            EditingElementStyle = editorStyle });
        // Estimate Name/Price are template columns, unlike the default WPF
        // text column. Editing must focus their inner TextBox as well.
        var display = new FrameworkElementFactory(typeof(TextBlock));
        display.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Value)));
        var input = new FrameworkElementFactory(typeof(TextBox));
        input.SetValue(FrameworkElement.StyleProperty, editorStyle);
        input.SetBinding(TextBox.TextProperty, new Binding(nameof(Row.Value))
        {
            Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
        });
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Width = 240,
            CellTemplate = new DataTemplate { VisualTree = display },
            CellEditingTemplate = new DataTemplate { VisualTree = input }
        });
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
                Require(editor is not null && editor.IsKeyboardFocusWithin,
                    "The visible editor did not own keyboard focus after edit activation.");
                Require(editor is not null && editor.SelectionLength == editor.Text.Length,
                    "Editor did not select the original text.");
                Require(editor is not null && Math.Abs(editor.FontSize - grid.FontSize) < .001 &&
                    editor.FontFamily.Source == grid.FontFamily.Source,
                    "Text and template editors must inherit table font at the same zoom.");
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

    public sealed class PreviewRow
    {
        public string Name { get; set; } = "";
        public decimal Quantity { get; set; }
        public string Locked { get; set; } = "";
    }

    private static void RunPreviewRangePaste()
    {
        Require(PreviewGridPaste.TryParse("\"А\tБ\"\t\"строка\r\n2\"\r\n\"цитата \"\"да\"\"\"\t\"\"\r\n", out var parsed) &&
            parsed.Count == 2 && parsed[0][0] == "А\tБ" && parsed[0][1] == "строка\r\n2" &&
            parsed[1][0] == "цитата \"да\"" && parsed[1][1] == "", "Excel quoted TSV parsing");
        Require(!PreviewGridPaste.TryParse("a\tb\r\nc", out _) &&
            !PreviewGridPaste.TryParse("\"unterminated", out _), "Invalid TSV must be rejected");
        var rows = new[] { new PreviewRow { Name = "A", Quantity = 1 }, new PreviewRow { Name = "B", Quantity = 2 } };
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false,
            SelectionUnit = DataGridSelectionUnit.Cell, SelectionMode = DataGridSelectionMode.Extended };
        var name = new DataGridTextColumn { Binding = new Binding(nameof(PreviewRow.Name)) { Mode = BindingMode.TwoWay }, Width = 120 };
        var qty = new DataGridTextColumn { Binding = new Binding(nameof(PreviewRow.Quantity)) { Mode = BindingMode.TwoWay }, Width = 120 };
        var locked = new DataGridTextColumn { Binding = new Binding(nameof(PreviewRow.Locked)), IsReadOnly = true, Width = 120 };
        grid.Columns.Add(name); grid.Columns.Add(qty); grid.Columns.Add(locked); grid.ItemsSource = rows;
        var host = new Window { Content = grid, Width = 400, Height = 260, ShowInTaskbar = false };
        host.Show();
        try
        {
            host.UpdateLayout();
            grid.CurrentCell = new DataGridCellInfo(rows[0], name);
            string[] allow = [nameof(PreviewRow.Name), nameof(PreviewRow.Quantity)];
            Require(!PreviewGridPaste.TryApply<PreviewRow>(grid, "X\t3\r\nY\tbad", allow, out _) &&
                rows[0].Name == "A" && rows[0].Quantity == 1, "Invalid last cell must not change earlier fields");
            Require(!PreviewGridPaste.TryApply<PreviewRow>(grid, "X\t3\tlocked", allow, out _) &&
                rows[0].Name == "A", "Locked target must refuse whole rectangle");
            Require(PreviewGridPaste.TryApply<PreviewRow>(grid, "Z\t3,25\r\nY\t4", allow, out var error),
                "Valid preview paste: " + error);
            Require(rows[0].Name == "Z" && rows[0].Quantity == 3.25m && rows[1].Name == "Y" &&
                rows[1].Quantity == 4 && grid.SelectedCells.Count == 4, "Range content and selected cells");
            CollectionViewSource.GetDefaultView(rows).SortDescriptions.Add(new SortDescription(nameof(PreviewRow.Name), ListSortDirection.Ascending));
            Pump();
            grid.CurrentCell = new DataGridCellInfo(rows[1], name);
            Require(PreviewGridPaste.TryApply<PreviewRow>(grid, "ZZ\t5", allow, out error) &&
                rows[1].Name == "ZZ" && rows[1].Quantity == 5 && rows[0].Name == "Z" &&
                grid.SelectedCells.Contains(new DataGridCellInfo(rows[1], name)),
                "Paste must select affected identity after resort: " + error);
        }
        finally { host.Close(); }
    }

    private static void RunSelectionAndSort()
    {
        var rows = new[] { new Row { Value = "Б" }, new Row { Value = "А" }, new Row { Value = "В" } };
        var grid = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false,
            SelectionUnit = DataGridSelectionUnit.Cell, SelectionMode = DataGridSelectionMode.Extended,
            ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader };
        var first = new DataGridTextColumn { Header = "A", Binding = new Binding(nameof(Row.Value)), SortMemberPath = nameof(Row.Value), Width = 120 };
        var second = new DataGridTextColumn { Header = "B", Binding = new Binding(nameof(Row.Value)), Width = 120 };
        grid.Columns.Add(first); grid.Columns.Add(second); grid.ItemsSource = rows;
        var host = new Window { Content = grid, Width = 400, Height = 260, ShowInTaskbar = false };
        host.Show();
        try
        {
            host.UpdateLayout();
            grid.SelectedCells.Clear();
            foreach (var row in rows.Take(2))
                foreach (var col in grid.Columns)
                    grid.SelectedCells.Add(new DataGridCellInfo(row, col));
            Require(grid.SelectedCells.Count == 4, "Rectangular cell selection must retain all four cells");
            // Clipboard content is generated by the actual column binding. Do not write to the user's clipboard.
            var copied = new List<string>();
            foreach (var row in rows.Take(2))
                foreach (var column in grid.Columns.OrderBy(c => c.DisplayIndex))
                {
                    Require(grid.SelectedCells.Contains(new DataGridCellInfo(row, column)), "Selected cell missing");
                    copied.Add(Convert.ToString(column.OnCopyingCellClipboardContent(row)) ?? "");
                }
            Require(copied.SequenceEqual(new[] { "Б", "Б", "А", "А" }), "Copy must reflect selected row/column values");
            var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
            view.SortDescriptions.Add(new SortDescription(nameof(Row.Value), ListSortDirection.Ascending));
            Pump();
            Require(ReferenceEquals(grid.Items[0], rows[1]) && ReferenceEquals(grid.Items[1], rows[0]),
                "Sorting must reorder the view, not mutate source values");
            Require(rows.Select(r => r.Value).SequenceEqual(new[] { "Б", "А", "В" }), "Sort mutated source rows");
            grid.SelectedCells.Clear();
            grid.CurrentCell = new DataGridCellInfo(rows[1], first);
            grid.SelectedCells.Add(grid.CurrentCell);
            Require(grid.SelectedCells.Count == 1 && ReferenceEquals(grid.CurrentCell.Item, rows[1]),
                "Current cell must preserve row identity after sorting");
            Require(!GridEditInteraction.TryPasteInEditingCell(grid),
                "A non-editing cell must not claim text-editor paste");
            // Exercise WPF's actual column paste binding without replacing the OS clipboard.
            first.OnPastingCellClipboardContent(rows[1], "Проверка");
            Pump();
            Require(rows[1].Value == "Проверка" && rows[0].Value == "Б" && rows[2].Value == "В",
                "Pasting cell content must affect only its target row");
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
