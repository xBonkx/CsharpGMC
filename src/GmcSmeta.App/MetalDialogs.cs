using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

internal abstract class MetalFormDialog : Window
{
    private readonly Grid _grid = new() { Margin = new Thickness(15) };
    private int _nextRow;

    protected MetalFormDialog(string title, double width = 490)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MinHeight = 200;
        MaxHeight = 750;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WorkspaceWindowUi.ApplyChrome(this);
        Background = System.Windows.Media.Brushes.White;
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1,GridUnitType.Star) });
        Content = new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    protected void Add(string caption, Control input)
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock
        {
            Text = caption, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0,0,9,9)
        };
        Grid.SetRow(text,_nextRow);
        Grid.SetColumn(text,0);
        _grid.Children.Add(text);
        input.Margin = new Thickness(0,0,0,9);
        input.MinHeight = 32;
        Grid.SetRow(input,_nextRow);
        Grid.SetColumn(input,1);
        _grid.Children.Add(input);
        _nextRow++;
    }

    protected void Submit(string caption, Func<bool> action)
    {
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var button = new Button
        {
            Content = caption, Padding = new Thickness(12,7,12,7),
            HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 116
        };
        WorkspaceWindowUi.StyleButton(button, primary: true);
        button.Click += (_, _) =>
        {
            try
            {
                if (action()) DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        Grid.SetRow(button,_nextRow);
        Grid.SetColumn(button,1);
        _grid.Children.Add(button);
        _nextRow++;
    }

    protected static decimal ParseDecimal(string text, string field)
    {
        var value = text.Trim().Replace(',', '.');
        if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var result))
            throw new ArgumentException($"Укажите число: {field}.");
        return result;
    }
}

internal sealed class MetalOperationDialog : MetalFormDialog
{
    private sealed record MaterialOption(string Category, string Name)
    {
        public override string ToString() => Category + " · " + Name;
    }
    private sealed record ObjectOption(long? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly ComboBox _kind = new();
    private readonly ComboBox _material = new();
    private readonly ComboBox _source = new();
    private readonly ComboBox _target = new();
    private readonly ComboBox _object = new();
    private readonly TextBox _area = new();
    private readonly TextBox _pieces = new() { Text = "0" };
    private readonly TextBox _note = new();
    public MetalOperation? Result { get; private set; }

    public MetalOperationDialog(IReadOnlyList<MetalStockView> materials,
        IReadOnlyList<(long Id, string Name)> objects, MetalStockView? selected, string preset)
        : base("Складская операция")
    {
        _kind.ItemsSource = MetalWarehouseRules.Operations;
        _kind.SelectedItem = MetalWarehouseRules.Operations.Contains(preset) ? preset : "Приход";
        _material.ItemsSource = materials.Select(x => new MaterialOption(x.Category,x.Material)).ToList();
        _material.SelectedItem = _material.Items.OfType<MaterialOption>().FirstOrDefault(x =>
            selected is not null && x.Category == selected.Category && x.Name == selected.Material) ??
            _material.Items.OfType<MaterialOption>().FirstOrDefault();
        _source.ItemsSource = new[] { "—" }.Concat(MetalWarehouseRules.Places).ToList();
        _target.ItemsSource = new[] { "—" }.Concat(MetalWarehouseRules.Places).ToList();
        _source.SelectedIndex = 0;
        _target.SelectedIndex = 0;
        _object.ItemsSource = new[] { new ObjectOption(null,"—") }
            .Concat(objects.Select(x => new ObjectOption(x.Id,x.Name))).ToList();
        _object.SelectedIndex = 0;
        Add("Операция",_kind);
        Add("Материал",_material);
        Add("Площадь, м²",_area);
        Add("Бухты, шт.",_pieces);
        Add("Откуда",_source);
        Add("Куда",_target);
        Add("Объект",_object);
        Add("Причина / примечание",_note);
        _kind.SelectionChanged += (_, _) => Configure();
        _material.SelectionChanged += (_, _) => Configure();
        Configure();
        Submit("Провести",() =>
        {
            var kind = (string?)_kind.SelectedItem ?? "";
            var material = (MaterialOption?)_material.SelectedItem ??
                throw new ArgumentException("Выберите материал.");
            var input = new MetalOperation(material.Category,material.Name,kind,
                ParseDecimal(_area.Text,"Площадь, м²"),
                MetalWarehouseRules.Pieces(ParseDecimal(_pieces.IsEnabled ? _pieces.Text : "0","Бухты, шт.")),
                _source.IsEnabled && _source.SelectedIndex > 0 ? _source.SelectedItem?.ToString() ?? "" : "",
                _target.IsEnabled && _target.SelectedIndex > 0 ? _target.SelectedItem?.ToString() ?? "" : "",
                _object.IsEnabled ? ((ObjectOption?)_object.SelectedItem)?.Id : null,
                "",_note.Text,Guid.NewGuid());
            Result = input;
            return true;
        });
    }

    private void Configure()
    {
        var kind = _kind.SelectedItem?.ToString() ?? "";
        _source.IsEnabled = kind is "Перемещение" or "Выдача" or "Списание" or "Резерв" or "Снять резерв";
        _target.IsEnabled = kind is "Приход" or "Перемещение" or "Возврат" or "Корректировка";
        _object.IsEnabled = kind is "Потребность" or "Выдача" or "Возврат" or "Резерв" or "Снять резерв";
        _pieces.IsEnabled = (_material.SelectedItem as MaterialOption)?.Category == "Бухты" &&
            kind is not ("Потребность" or "Резерв" or "Снять резерв");
        _area.ToolTip = kind == "Потребность" ? "Новая общая потребность, не добавка" :
            kind == "Корректировка" ? "Отрицательная площадь уменьшает остаток в поле «Куда»" : null;
    }
}

internal sealed record MetalLotInput(string Material,string Supplier,string Invoice,decimal Area,decimal Pieces);
internal sealed class MetalLotDialog : MetalFormDialog
{
    public MetalLotInput? Result { get; private set; }
    public MetalLotDialog(IReadOnlyList<MetalStockView> materials) : base("Новая партия металла")
    {
        var material = new ComboBox
        {
            ItemsSource = materials.Where(x => x.Category == "Бухты")
                .Select(x => x.Material).Distinct().ToList()
        };
        material.SelectedIndex = 0;
        var supplier = new TextBox();
        var invoice = new TextBox();
        var area = new TextBox();
        var pieces = new TextBox();
        Add("Материал",material);
        Add("Поставщик",supplier);
        Add("Номер счёта",invoice);
        Add("Площадь, м²",area);
        Add("Бухты, шт.",pieces);
        Submit("Добавить",() =>
        {
            if (material.SelectedItem is not string name)
                throw new ArgumentException("Сначала импортируйте металл в бухтах.");
            Result = new MetalLotInput(name,supplier.Text,invoice.Text,
                ParseDecimal(area.Text,"Площадь"),ParseDecimal(pieces.Text,"Бухты"));
            return true;
        });
    }
}

internal sealed record MetalReceived(decimal Area,decimal Pieces,string Place);
internal sealed class MetalReceiveDialog : MetalFormDialog
{
    public MetalReceived? Result { get; private set; }
    public MetalReceiveDialog(MetalLotView lot) : base("Получение партии · " + lot.Invoice)
    {
        var destination = new ComboBox
        {
            ItemsSource = MetalWarehouseRules.Places
                .Where(p => p != MetalWarehouseRules.SupplierPlace).ToList()
        };
        destination.SelectedIndex = 0;
        var area = new TextBox { Text = MetalWarehouseRules.SqmText(lot.AtSupplier) };
        var pieces = new TextBox { Text = lot.PiecesAtSupplier.ToString(CultureInfo.InvariantCulture) };
        Add("Место получения",destination);
        Add("Площадь, м²",area);
        Add("Бухты, шт.",pieces);
        Submit("Получить",() =>
        {
            Result = new MetalReceived(ParseDecimal(area.Text,"Площадь"),
                ParseDecimal(pieces.Text,"Количество"),destination.SelectedItem?.ToString() ?? "");
            return true;
        });
    }
}

internal sealed class MetalOpeningPreviewDialog : Window
{
    private sealed record PreviewRow(string Category,string Material,string Stock,
        string Demand,string Balance,string Cached,string Pieces,string Match);

    public MetalOpeningPreviewDialog(MetalOpeningPreview data)
    {
        Title = "Сверка исходных остатков металла";
        Width = 1050; Height = 680; MinWidth = 750; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WorkspaceWindowUi.ApplyChrome(this);
        Background = System.Windows.Media.Brushes.White;
        var root = new DockPanel { Margin = new Thickness(14) };
        Content = root;
        var top = new TextBlock
        {
            Text = $"Источник: {data.FileName} · материалов: {data.Records.Count} · " +
                   $"запас: {MetalWarehouseRules.SqmText(data.TotalStock)} м² · " +
                   $"потребность: {MetalWarehouseRules.SqmText(data.TotalDemand)} м²",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,10)
        };
        top.TextWrapping = TextWrapping.Wrap;
        DockPanel.SetDock(top,Dock.Top); root.Children.Add(top);
        var bottom = new StackPanel { Margin = new Thickness(0,8,0,0) };
        DockPanel.SetDock(bottom,Dock.Bottom);
        root.Children.Add(bottom);
        var warn = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 116, Height = data.Warnings.Count > 0 ? 95 : 40,
            Text = data.Warnings.Count > 0 ? string.Join(Environment.NewLine,data.Warnings) :
                "Расхождений с сохранёнными итогами Excel не найдено.",
            Margin = new Thickness(0,0,0,9)
        };
        bottom.Children.Add(warn);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Закрыть", Width = 105, Padding = new Thickness(8,6,8,6) };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);
        var approve = new Button
        {
            Content = "Перенести начальные остатки", Width = 230,
            Margin = new Thickness(9,0,0,0), Padding = new Thickness(8,6,8,6)
        };
        approve.Click += (_, _) => DialogResult = true;
        WorkspaceWindowUi.StyleButton(cancel);
        WorkspaceWindowUi.StyleButton(approve, primary: true);
        buttons.Children.Add(approve);
        bottom.Children.Add(buttons);
        var grid = new DataGrid
        {
            IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false,
            AlternatingRowBackground = System.Windows.Media.Brushes.WhiteSmoke,
            EnableRowVirtualization = true
        };
        WorkspaceWindowUi.StyleTable(grid);
        grid.MinRowHeight = 27;
        void Col(string header,string binding,DataGridLength width)
        {
            var column = new DataGridTextColumn { Header = header, Binding = new Binding(binding), Width = width };
            WorkspaceWindowUi.StyleColumn(column, binding is "Category" or "Material" ? TextAlignment.Left :
                binding == "Match" ? TextAlignment.Center : TextAlignment.Right);
            if (binding == "Balance")
            {
                var style = new Style(typeof(DataGridCell),(Style)Application.Current.FindResource("CompactCostCell"));
                var mismatch = new DataTrigger { Binding = new Binding("Match"), Value = "⚠" };
                mismatch.Setters.Add(new Setter(Control.BackgroundProperty,new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF,0xF2,0xD7))));
                style.Triggers.Add(mismatch);
                var selection = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
                selection.Setters.Add(new Setter(Control.BackgroundProperty,Application.Current.FindResource("GridSelectionBrush")));
                style.Triggers.Add(selection);
                column.CellStyle = style;
            }
            grid.Columns.Add(column);
        }
        Col("Категория","Category",125); Col("Материал","Material",new DataGridLength(1,DataGridLengthUnitType.Star));
        Col("Запас, м²","Stock",100); Col("Потребность, м²","Demand",120);
        Col("Остаток, м²","Balance",110); Col("В Excel, м²","Cached",110);
        Col("Бухты, шт.","Pieces",88); Col("Сверка","Match",95);
        TableColumnMenu.SetProtect(grid, "Категория|Материал|Остаток, м²|В Excel, м²|Сверка");
        TableColumnMenu.SetKey(grid, "metal-reconcile-preview");
        grid.ItemsSource = data.Records.Select(r =>
        {
            var stock = r.Stock.Sum(x => MetalWarehouseRules.Units(x.Area));
            var need = r.Demand.Sum(x => MetalWarehouseRules.Units(x.Area));
            var cached = r.CachedBalance is decimal c ? MetalWarehouseRules.Units(c) : (long?)null;
            var match = cached is null ? "нет итога" :
                decimal.Round(MetalWarehouseRules.Sqm(stock-need),2) ==
                decimal.Round(MetalWarehouseRules.Sqm(cached.Value),2) ? "✓" : "⚠";
            var pcs = r.Pieces.Sum(x => x.Pieces);
            return new PreviewRow(r.Category,r.Material,
                MetalWarehouseRules.SqmText(stock), MetalWarehouseRules.SqmText(need),
                MetalWarehouseRules.SqmText(stock-need),
                cached is long n ? MetalWarehouseRules.SqmText(n) : "—",
                r.Pieces.Count > 0 ? pcs.ToString("0",CultureInfo.InvariantCulture) : "—", match);
        }).ToList();
        root.Children.Add(grid);
    }
}
