using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Supplier chooser, editable request preview and final Excel save (Python request_dialog.py).</summary>
public sealed class ProcurementRequestDialog : Window
{
    private sealed record SupplierChoice(string Name, int NewCount, int TotalCount)
    {
        public string Label => $"{Name} — {NewCount} не заказано из {TotalCount}";
    }

    public sealed class PreviewLine
    {
        public string Name { get; set; } = "";
        public string Unit { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal OriginalQuantity { get; init; }
        public decimal? UnitPrice { get; set; }
        public string Note { get; set; } = "";
        public string Systems { get; init; } = "";
        public IReadOnlyList<long> PositionIds { get; init; } = Array.Empty<long>();
    }

    private readonly ProcurementRequestPlanner.DocumentInfo _document;
    private readonly IReadOnlyList<SqlServerProcurement.BuyRow> _source;
    private readonly IReadOnlySet<long> _children;
    private readonly ComboBox _supplier;
    private readonly TextBox _number;
    private readonly TextBox _due;
    private readonly TextBox _note;
    private readonly CheckBox _onlyNew;
    private readonly CheckBox _merge;
    private readonly CheckBox _withPrices;
    private readonly CheckBox _mark;
    private readonly DataGrid _grid;
    private readonly TextBlock _count;
    private readonly Button _make;
    private readonly ObservableCollection<PreviewLine> _display = new();

    public bool FileSaved { get; private set; }
    public IReadOnlyList<long> MarkedIds { get; private set; } = Array.Empty<long>();
    public string RequestNumber => _number.Text.Trim();
    public string Supplier => (_supplier.SelectedItem as SupplierChoice)?.Name ?? "";

    public ProcurementRequestDialog(
        ProcurementRequestPlanner.DocumentInfo document,
        IReadOnlyList<SqlServerProcurement.BuyRow> source,
        IReadOnlySet<long> childIds, string nextNumber, string preferredSupplier)
    {
        _document = document;
        _source = source;
        _children = childIds;
        Title = "Заявка поставщику";
        Width = 1000;
        Height = 700;
        MinWidth = 720;
        MinHeight = 490;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ProcurementDialogChrome.Apply(this);
        Background = System.Windows.Media.Brushes.White;

        var root = new Grid { Margin = ProcurementDialogChrome.RequestOuter };
        for (var i = 0; i < 5; i++)
            root.RowDefinitions.Add(new RowDefinition
            { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        Content = root;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(ProcurementDialogChrome.Heading("Заявка поставщику"));
        heading.Children.Add(new TextBlock
        {
            Text = document.ObjectName + " · " + document.Title,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0)
        });
        root.Children.Add(heading);

        var form = new WrapPanel { Margin = ProcurementDialogChrome.SectionGap };
        static StackPanel Field(string caption, Control control, int width)
        {
            var wrapper = new StackPanel
            {
                Width = width, Margin = new Thickness(0, 0, 12, 5)
            };
            wrapper.Children.Add(new TextBlock { Text = caption, Margin = new Thickness(0, 0, 0, 4) });
            control.MinHeight = 30;
            wrapper.Children.Add(control);
            return wrapper;
        }
        var all = ProcurementRequestPlanner.SupplierCounts(source, childIds, onlyNew: false);
        var fresh = ProcurementRequestPlanner.SupplierCounts(source, childIds)
            .ToDictionary(x => x.Supplier, x => x.Count, StringComparer.Ordinal);
        _supplier = new ComboBox
        {
            ItemsSource = all.Select(x => new SupplierChoice(
                x.Supplier, fresh.GetValueOrDefault(x.Supplier), x.Count)).ToArray(),
            DisplayMemberPath = nameof(SupplierChoice.Label),
            MaxDropDownHeight = 420, IsTextSearchEnabled = true
        };
        TextSearch.SetTextPath(_supplier, nameof(SupplierChoice.Label));
        _supplier.SelectionChanged += (_, _) => Rebuild();
        form.Children.Add(Field("Поставщик", _supplier, 380));
        _number = new TextBox { Text = nextNumber };
        form.Children.Add(Field("Номер заявки", _number, 120));
        _due = new TextBox { ToolTip = "например: до 15.11.2026 или «как можно скорее»" };
        form.Children.Add(Field("Срок поставки", _due, 195));
        _note = new TextBox { ToolTip = "Доставка на объект / самовывоз, контакт для связи…" };
        form.Children.Add(Field("Примечание", _note, 225));
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        _onlyNew = new CheckBox { Content = "Только ещё не заказанное",
            ToolTip = "Строки со статусом «Заказано», «Оплачено», «На складе»… в заявку не попадут",
            IsChecked = true, Margin = new Thickness(0, 0, 14, 8) };
        _merge = new CheckBox { Content = "Одинаковые из разных систем — одной строкой",
            IsChecked = true, Margin = new Thickness(0, 0, 14, 8) };
        _withPrices = new CheckBox { Content = "С ценами (наш закуп)",
            ToolTip = "Обычно заявку шлют без цен — поставщик сам выставит счёт",
            Margin = new Thickness(0, 0, 14, 8) };
        _mark = new CheckBox { Content = "Отметить строки «Заказано» с номером заявки",
            IsChecked = true, Margin = new Thickness(0, 0, 14, 8) };
        options.Children.Add(_onlyNew);
        options.Children.Add(_merge);
        options.Children.Add(_withPrices);
        options.Children.Add(_mark);
        _onlyNew.Checked += (_, _) => Rebuild();
        _onlyNew.Unchecked += (_, _) => Rebuild();
        _merge.Checked += (_, _) => Rebuild();
        _merge.Unchecked += (_, _) => Rebuild();
        Grid.SetRow(options, 2);
        root.Children.Add(options);

        _grid = new DataGrid
        {
            ItemsSource = _display, AutoGenerateColumns = false,
            CanUserAddRows = false, CanUserDeleteRows = false,
            MinRowHeight = 27, GridLinesVisibility = DataGridGridLinesVisibility.All,
            HeadersVisibility = DataGridHeadersVisibility.Column
        };
        void Col(string title, string property, int width, bool readOnly)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = title,
                MinWidth = width == 0 ? 180 : 0,
                Width = width == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star)
                                  : new DataGridLength(width),
                IsReadOnly = readOnly,
                Binding = new Binding(property)
                {
                    Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus,
                    ValidatesOnExceptions = true
                }
            });
        }
        Col("Наименование", nameof(PreviewLine.Name), 0, false);
        Col("Ед.", nameof(PreviewLine.Unit), 55, false);
        Col("Кол-во", nameof(PreviewLine.Quantity), 80, false);
        Col("Закуп, ед.", nameof(PreviewLine.UnitPrice), 100, false);
        Col("Примечание", nameof(PreviewLine.Note), 165, false);
        Col("Системы", nameof(PreviewLine.Systems), 165, true);
        ProcurementDialogChrome.Table(_grid);
        foreach (var column in _grid.Columns.OfType<DataGridTextColumn>())
            ProcurementDialogChrome.Column(column, column.Header?.ToString() switch
            {
                "Ед." or "Кол-во" => TextAlignment.Center,
                "Закуп, ед." => TextAlignment.Right,
                _ => TextAlignment.Left
            });
        TableColumnMenu.SetProtect(_grid, "Наименование|Ед.|Кол-во|Закуп, ед.");
        TableColumnMenu.SetKey(_grid, "procurement-request-preview");
        Grid.SetRow(_grid, 3);
        root.Children.Add(_grid);

        var footer = new DockPanel { Margin = ProcurementDialogChrome.FooterGap };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right
        };
        _make = new Button
        {
            Content = "Сформировать заявку (Excel)",
            MinWidth = 210, Height = 32,
            Margin = new Thickness(6, 0, 0, 0)
        };
        _make.Click += Make;
        buttons.Children.Add(_make);
        var cancel = new Button
        {
            Content = "Закрыть", MinWidth = 90, Height = 32,
            Margin = new Thickness(8, 0, 0, 0)
        };
        cancel.Click += (_, _) => DialogResult = false;
        ProcurementDialogChrome.Buttons(_make, cancel, "ProcurementRequestPrimary");
        _make.SetResourceReference(ContentControl.ContentTemplateProperty, "IconLabelExcel");
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        _count = ProcurementDialogChrome.Hint("");
        _count.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_count);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        var select = _supplier.Items.OfType<SupplierChoice>()
            .FirstOrDefault(x => x.Name == preferredSupplier);
        _supplier.SelectedItem = select ?? _supplier.Items.OfType<SupplierChoice>().FirstOrDefault();
        Rebuild();
    }

    private void Rebuild()
    {
        if (_display is null || _onlyNew is null || _merge is null ||
            _count is null || _make is null)
            return;
        _display.Clear();
        var supplier = Supplier;
        if (supplier.Length > 0)
            foreach (var p in ProcurementRequestPlanner.CreateRows(_source, _children, supplier,
                         _onlyNew.IsChecked == true, _merge.IsChecked == true))
                _display.Add(new PreviewLine
                {
                    Name = p.Name, Unit = p.Unit, Quantity = p.Quantity,
                    OriginalQuantity = p.Quantity, UnitPrice = p.UnitPrice,
                    Note = p.Note, Systems = p.SystemsText,
                    PositionIds = p.PositionIds.ToArray()
                });
        _count.Text = _display.Count == 0
            ? "Нет строк: назначьте поставщика или снимите «Только ещё не заказанное»."
            : $"В заявке строк: {_display.Count} · кол-во и примечания можно изменить перед выгрузкой.";
        _make.IsEnabled = _display.Count != 0;
    }

    private void Make(object sender, RoutedEventArgs args)
    {
        if (!_grid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !_grid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            MessageBox.Show(this, "Исправьте ошибки в таблице.", "Заявка");
            return;
        }
        if (_display.Any(x => string.IsNullOrWhiteSpace(x.Name) ||
                              string.IsNullOrWhiteSpace(x.Unit) ||
                              x.Quantity <= 0m || x.Quantity > 1_000_000_000m ||
                              decimal.Round(x.Quantity, 6) != x.Quantity ||
                              x.UnitPrice < 0m))
        {
            MessageBox.Show(this, "Проверьте наименования, ед., количества и цены.", "Заявка");
            return;
        }
        if (_mark.IsChecked == true && _display.Any(x => x.Quantity != x.OriginalQuantity))
        {
            MessageBox.Show(this, "Изменённое количество в печатной заявке не равно " +
                "количеству исходных строк. Снимите «Отметить строки Заказано», " +
                "чтобы не отметить заказанной полную позицию по ошибке.", "Частичная заявка");
            return;
        }
        var number = string.IsNullOrWhiteSpace(_number.Text) ? "б/н" : _number.Text.Trim();
        var rows = _display.Select(x =>
        {
            var line = new ProcurementRequestPlanner.RequestRow
            {
                Name = x.Name, Unit = x.Unit, Quantity = x.Quantity,
                UnitPrice = x.UnitPrice, Note = x.Note
            };
            line.PositionIds.AddRange(x.PositionIds);
            return line;
        }).ToArray();
        var filename = $"Заявка №{number} {Supplier} {_document.ObjectName} {DateTime.Today:dd.MM.yyyy}.xlsx";
        var success = XlsxExport.SaveCreatedWorkbook(this, filename,
            () => ProcurementRequestExcel.Create(_document, Supplier, number, DateTime.Today,
                _due.Text.Trim(), _note.Text.Trim(), Environment.UserName,
                rows, _withPrices.IsChecked == true));
        if (!success) return;
        FileSaved = true;
        MarkedIds = _mark.IsChecked == true
            ? rows.SelectMany(r => r.PositionIds).Distinct().ToArray()
            : Array.Empty<long>();
        DialogResult = true;
    }
}
