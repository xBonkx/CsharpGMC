using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Workshop PDF preview. It owns only detached print data; no SQL writes.</summary>
internal sealed class ProcurementShopOrderPreview : Window
{
    public sealed class PrintRow
    {
        public long PositionId { get; init; }
        public string System { get; init; } = "";
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "";
        public decimal Quantity { get; set; }
        public string Note { get; set; } = "";
    }

    private readonly ObservableCollection<PrintRow> _rows;
    private readonly TextBox _number = new() { Width = 135, MaxLength = 64 };
    private readonly TextBox _due = new() { Width = 210, MaxLength = 120 };
    private readonly DataGrid _grid;
    public string Number => _number.Text.Trim();
    public string Due => _due.Text.Trim();
    public IReadOnlyList<ProcurementRequestPlanner.ShopPrintLine> Lines { get; private set; } =
        Array.Empty<ProcurementRequestPlanner.ShopPrintLine>();

    public ProcurementShopOrderPreview(ProcurementRequestPlanner.DocumentInfo document,
        IReadOnlyList<ProcurementRequestPlanner.ShopPrintLine> source, bool selectedOnly)
    {
        Title = "Заявка в цех";
        Width = 980;
        Height = 620;
        MinWidth = 680;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ProcurementDialogChrome.Apply(this);
        _rows = new ObservableCollection<PrintRow>(source.Select(row => new PrintRow
        {
            PositionId = row.PositionId, System = row.System, Name = row.Name,
            Unit = row.Unit, Quantity = row.Quantity, Note = row.Note
        }));
        var root = new Grid { Margin = ProcurementDialogChrome.Outer };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var title = ProcurementDialogChrome.Heading("Заявка в цех");
        top.Children.Add(title);
        top.Children.Add(new TextBlock { Text = document.ObjectName + " · " + document.Title,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        top.Children.Add(new TextBlock
        {
            Text = (selectedOnly ? "Выбранные позиции цеха" : "Все ожидающие изготовления позиции") +
                $" · {source.Count} поз. · Изменения только для PDF, без записи в смету и закупки.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
        });
        root.Children.Add(top);
        var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        void Field(string caption, TextBox field)
        {
            var pair = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            pair.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 0, 0, 4) });
            field.MinHeight = 30;
            pair.Children.Add(field);
            form.Children.Add(pair);
        }
        Field("Номер (необязательно)", _number);
        Field("Срок (необязательно)", _due);
        Grid.SetRow(form, 1);
        root.Children.Add(form);
        _grid = new DataGrid
        {
            ItemsSource = _rows, AutoGenerateColumns = false, CanUserAddRows = false,
            CanUserDeleteRows = false, EnableRowVirtualization = true,
            SelectionUnit = DataGridSelectionUnit.FullRow, SelectionMode = DataGridSelectionMode.Extended,
            HeadersVisibility = DataGridHeadersVisibility.Column, MinRowHeight = 24
        };
        ProcurementDialogChrome.Table(_grid);
        void Column(string header, string property, double width, bool readOnly = false)
        {
            var col = new DataGridTextColumn
            {
                Header = header, IsReadOnly = readOnly,
                Width = width == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
                Binding = new Binding(property) { Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus, ValidatesOnExceptions = true }
            };
            ProcurementDialogChrome.Column(col, property is nameof(PrintRow.Quantity) or nameof(PrintRow.Unit)
                ? TextAlignment.Center : TextAlignment.Left);
            _grid.Columns.Add(col);
        }
        Column("Система", nameof(PrintRow.System), 145, true);
        Column("Наименование", nameof(PrintRow.Name), 0);
        Column("Ед.", nameof(PrintRow.Unit), 60);
        Column("Кол-во", nameof(PrintRow.Quantity), 100);
        Column("Примечание", nameof(PrintRow.Note), 220);
        TableColumnMenu.SetProtect(_grid, "Наименование|Ед.|Кол-во");
        TableColumnMenu.SetKey(_grid, "procurement-shop-preview");
        Grid.SetRow(_grid, 2);
        root.Children.Add(_grid);
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var print = new Button { Content = "Сохранить PDF", MinWidth = 144, IsDefault = true };
        print.Click += Accept;
        var cancel = new Button { Content = "Отмена", MinWidth = 90, IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0) };
        ProcurementDialogChrome.Buttons(print, cancel);
        actions.Children.Add(print);
        actions.Children.Add(cancel);
        Grid.SetRow(actions, 3);
        root.Children.Add(actions);
    }

    private void Accept(object sender, RoutedEventArgs e)
    {
        if (!_grid.CommitEdit(DataGridEditingUnit.Cell, true) || !_grid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            MessageBox.Show(this, "Исправьте ошибки в таблице.", "Заявка в цех");
            return;
        }
        var rows = _rows.Select(p => new ProcurementRequestPlanner.ShopPrintLine(p.PositionId,
            p.System, p.Name.Trim(), p.Unit.Trim(), p.Quantity, p.Note.Trim())).ToArray();
        if (ProcurementRequestPlanner.ShopPrintError(rows) is { } error)
        {
            MessageBox.Show(this, error, "Заявка в цех");
            return;
        }
        Lines = rows;
        DialogResult = true;
    }
}
