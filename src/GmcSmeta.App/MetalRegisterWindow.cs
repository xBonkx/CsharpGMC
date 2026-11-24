using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClosedXML.Excel;
using GmcSmeta.Core.Db;
using Microsoft.Win32;

namespace GmcSmeta.App;

/// <summary>Shared, versioned Excel metal register. Edits preserve the workbook structure and cell styles.</summary>
internal sealed class MetalRegisterWindow : Window
{
    private readonly SqlServerMetalWarehouse _warehouse;
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = new() { Foreground = Brushes.SlateGray };
    private XLWorkbook? _book;
    private long? _version;
    private bool _dirty;

    public MetalRegisterWindow(string connectionString)
    {
        Title = "Учёт металла";
        Width = 1200; Height = 700; MinWidth = 820; MinHeight = 490;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;
        _warehouse = new SqlServerMetalWarehouse(connectionString);
        var dock = new DockPanel { Margin = new Thickness(12) };
        Content = dock;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,9) };
        DockPanel.SetDock(header,Dock.Top);
        dock.Children.Add(header);
        void Button(string title, RoutedEventHandler action)
        {
            var button = new Button { Content=title,Padding=new Thickness(11,6,11,6),Margin=new Thickness(0,0,8,0) };
            button.Click += action; header.Children.Add(button);
        }
        Button("Загрузить Excel…",Import_Click);
        Button("Сохранить для всех",Save_Click);
        Button("Выгрузить Excel…",Export_Click);
        Button("Сверка для склада…",Opening_Click);
        Button("Обновить",Refresh_Click);
        DockPanel.SetDock(_status,Dock.Bottom);
        dock.Children.Add(_status);
        dock.Children.Add(_tabs);
        Closing += (_, e) => { if (!ConfirmDiscard()) e.Cancel = true; };
        Loaded += async (_, _) => await Reload();
    }

    private bool ConfirmDiscard() => !_dirty || MessageBox.Show(this,
        "Отбросить несохранённые изменения?", "Учёт металла",
        MessageBoxButton.YesNo,MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void MarkDirty()
    {
        _dirty = true;
        Title = "Учёт металла *";
        _status.Text = "Есть несохранённые изменения; при конфликте можно выгрузить локальный Excel.";
    }

    private void Display(XLWorkbook? workbook)
    {
        _tabs.Items.Clear();
        _book?.Dispose();
        _book = workbook;
        if (_book is null)
        {
            _status.Text = "Общая Excel-книга ещё не загружена.";
            return;
        }
        foreach (var sheet in _book.Worksheets)
        {
            var page = new MetalRegisterSheet(sheet, MarkDirty);
            _tabs.Items.Add(new TabItem
            {
                Header = sheet.Name == "Лист1" && page.HasSections ? "Объёмы по объектам" : sheet.Name,
                Content = page
            });
        }
        _dirty = false; Title = "Учёт металла";
    }

    private async Task Reload()
    {
        if (!ConfirmDiscard()) return;
        try
        {
            _status.Text = "Загрузка общей книги…";
            var shared = await Task.Run(_warehouse.ReadRegister);
            XLWorkbook? book = shared is null ? null :
                await Task.Run(() => new XLWorkbook(new MemoryStream(shared.Data, false)));
            Display(book);
            _version = shared?.Revision;
            _status.Text = shared is null ? "Нет общей Excel-книги." :
                "Версия " + shared.Revision + " · изменения коллег защищены от перезаписи.";
        }
        catch (Exception ex) { Error(ex); }
    }

    private static XLWorkbook OpenLimited(string path)
    {
        if (new FileInfo(path).Length is 0 or > 5_000_000)
            throw new InvalidDataException("Excel-книга должна быть не больше 5 МБ.");
        var book = new XLWorkbook(path);
        if (!book.Worksheets.Any() || book.Worksheets.Any(s =>
            (long)(s.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? 0) *
            (s.LastColumnUsed(XLCellsUsedOptions.Contents)?.ColumnNumber() ?? 0) > 250_000))
        {
            book.Dispose();
            throw new InvalidDataException("В книге нет листов либо есть лист больше 250 000 ячеек.");
        }
        return book;
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var picker = new OpenFileDialog { Title = "Загрузить таблицу металла",Filter = "Excel (*.xlsx)|*.xlsx" };
        if (picker.ShowDialog(this) != true) return;
        if (_book is not null && MessageBox.Show(this,
            "Заменить текущую таблицу выбранным Excel при сохранении для всех?",
            "Учёт металла",MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try
        {
            var workbook = await Task.Run(() => OpenLimited(picker.FileName));
            Display(workbook);
            MarkDirty();
        }
        catch (Exception ex) { Error(ex); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_book is null || !_dirty) return;
        try
        {
            var output = new MemoryStream();
            _book.SaveAs(output);
            var mainRevision = await Task.Run(_warehouse.Revision);
            var next = await Task.Run(() => _warehouse.SaveRegister(output.ToArray(),_version,mainRevision));
            _version = next;
            _dirty = false;
            Title = "Учёт металла";
            _status.Text = "Сохранено для всех · версия " + next;
        }
        catch (Exception ex)
        {
            _status.Text = "Сохранение не подтверждено. Локальная книга сохранена в редакторе.";
            Error(ex);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_book is null) return;
        var picker = new SaveFileDialog
        {
            Title = "Выгрузить таблицу металла", FileName = "Учёт металла.xlsx",
            DefaultExt = ".xlsx",Filter = "Excel (*.xlsx)|*.xlsx"
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            _book.SaveAs(picker.FileName);
            _status.Text = "Файл сохранён: " + picker.FileName;
        }
        catch (Exception ex) { Error(ex); }
    }

    private async void Opening_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Сверка исходных остатков",Filter = "Excel (*.xlsx)|*.xlsx" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var preview = await Task.Run(() => MetalOpeningReader.Read(picker.FileName));
            if (new MetalOpeningPreviewDialog(preview) { Owner = this }.ShowDialog() != true) return;
            if (MessageBox.Show(this, "Перенести остатки в склад один раз?",
                "Первоначальный импорт",MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var revision = await Task.Run(_warehouse.Revision);
            await Task.Run(() => _warehouse.ImportOpening(
                preview.Records,preview.FileName,preview.Original,revision));
            MessageBox.Show(this,"Начальные остатки и потребности перенесены.","Склад металла");
        }
        catch (Exception ex) { Error(ex); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Reload();

    private void Error(Exception ex) => MessageBox.Show(this,ex.Message,"Учёт металла",
        MessageBoxButton.OK,MessageBoxImage.Warning);
}

/// <summary>Editable worksheet projection; only source cells are changed, never merged/formula cells.</summary>
internal sealed class MetalRegisterSheet : UserControl
{
    private readonly IXLWorksheet _sheet;
    private readonly Action _markDirty;
    private readonly MetalFormulaEvaluator _formulas;
    private readonly DataGrid _table = new()
    {
        AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
        EnableRowVirtualization = true,
        EnableColumnVirtualization = true, SelectionUnit = DataGridSelectionUnit.Cell,
        GridLinesVisibility = DataGridGridLinesVisibility.All
    };
    private readonly ComboBox _sections = new() { MinWidth = 230, Margin = new Thickness(6,0,0,4) };
    private readonly List<(int Header,string Name)> _sectionRows = [];
    private readonly int _rowLimit;
    private readonly int _colLimit;
    private IReadOnlyList<int> _columns = [];
    private int _heading;

    public bool HasSections => _sectionRows.Count != 0;

    public MetalRegisterSheet(IXLWorksheet sheet, Action markDirty)
    {
        _sheet = sheet; _markDirty = markDirty;
        _formulas = new MetalFormulaEvaluator(sheet);
        _rowLimit = sheet.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? 0;
        _colLimit = sheet.LastColumnUsed(XLCellsUsedOptions.Contents)?.ColumnNumber() ?? 0;
        var names = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["металл/объект"] = "Металл в бухтах",
            ["металл в штрипсах"] = "Металл в штрипсах",
            ["металл в листах"] = "Металл в листах",
            ["огнезащита / объект"] = "Огнезащита"
        };
        for (var row = 1; row <= _rowLimit; row++)
            if (names.TryGetValue(sheet.Cell(row,1).CachedValue.ToString().Trim(),out var title))
                _sectionRows.Add((row,title));
        var dock = new DockPanel();
        Content = dock;
        if (HasSections)
        {
            var tools = new StackPanel { Orientation = Orientation.Horizontal,Margin = new Thickness(3,8,0,5) };
            DockPanel.SetDock(tools,Dock.Top);
            tools.Children.Add(new TextBlock { Text="Раздел",VerticalAlignment=VerticalAlignment.Center });
            foreach (var section in _sectionRows) _sections.Items.Add(section.Name);
            _sections.Items.Add("Весь лист");
            tools.Children.Add(_sections);
            dock.Children.Add(tools);
            _sections.SelectionChanged += (_, _) => SetSection(_sections.SelectedIndex);
        }
        _table.SetResourceReference(FrameworkElement.StyleProperty, "DocumentTable");
        _table.BeginningEdit += OnBeginningEdit;
        dock.Children.Add(_table);
        if (HasSections) _sections.SelectedIndex = 0;
        else SetSection(-1);
    }

    private void SetSection(int index)
    {
        if (index >= _sectionRows.Count) index = -1;
        _heading = index < 0 ? 0 : _sectionRows[index].Header;
        var start = _heading == 0 ? 1 : _heading + 1;
        var end = index < 0 || index == _sectionRows.Count - 1
            ? _rowLimit : _sectionRows[index+1].Header - 1;
        var rows = new List<int>();
        for (var row = start; row <= end; row++)
        {
            if (_heading != 0)
            {
                var first = _sheet.Cell(row,1).CachedValue;
                if (first.IsDateTime) continue;
                bool nonempty = false;
                for (var col = 1; col <= _colLimit; col++)
                {
                    var cell = _sheet.Cell(row,col);
                    if (!cell.CachedValue.IsBlank || cell.HasFormula) { nonempty = true; break; }
                }
                if (!nonempty) continue;
            }
            rows.Add(row);
        }
        _columns = Enumerable.Range(1,_colLimit)
            .Where(col => _heading == 0 ||
                !_sheet.Cell(_heading,col).CachedValue.IsBlank ||
                rows.Any(row => !_sheet.Cell(row,col).CachedValue.IsBlank ||
                               _sheet.Cell(row,col).HasFormula)).ToList();
        _table.Columns.Clear();
        for (var col = 0; col < _columns.Count; col++)
        {
            var indexName = "C" + col.ToString(CultureInfo.InvariantCulture);
            var number = _columns[col];
            var label = _heading == 0 ? _sheet.Cell(1,number).Address.ColumnLetter :
                _sheet.Cell(_heading,number).CachedValue.ToString();
            if (string.IsNullOrWhiteSpace(label)) label = _sheet.Cell(1,number).Address.ColumnLetter;
            var column = new DataGridTextColumn
            {
                Header = label,
                Binding = new Binding($"[{indexName}]")
                {
                    Mode=BindingMode.TwoWay, UpdateSourceTrigger=UpdateSourceTrigger.LostFocus
                },
                SortMemberPath=indexName,
                ElementStyle = (Style)Application.Current.FindResource("CompactCostText"),
                EditingElementStyle = (Style)Application.Current.FindResource("CompactCostEditor"),
                HeaderStyle = (Style)Application.Current.FindResource("KsTextHeader"),
                Width = col == 0 ? 205 : 145
            };
            TableColumnMenu.SetStableId(column, "excel-" + number.ToString(CultureInfo.InvariantCulture));
            _table.Columns.Add(column);
        }
        _table.ItemsSource = rows.Select(row => new MetalGridRow(this,row)).ToList();
        var sheetKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(_sheet.Name)))[..16];
        TableColumnMenu.RefreshDynamic(_table, "metal-register-" + sheetKey + "-" +
            _heading.ToString(CultureInfo.InvariantCulture), ownHeaderMenu: true);
    }

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not MetalGridRow row) return;
        var key = e.Column.SortMemberPath;
        if (key.Length == 0 || !int.TryParse(key.AsSpan(1),out var index) ||
            index < 0 || index >= _columns.Count) { e.Cancel = true; return; }
        var excel = _sheet.Cell(row.ExcelRow,_columns[index]);
        e.Cancel = excel.HasFormula || IsMergedFollower(row.ExcelRow,_columns[index]);
    }

    private bool IsMergedFollower(int row,int column)
    {
        foreach (var merged in _sheet.MergedRanges)
        {
            var a = merged.RangeAddress;
            if (row >= a.FirstAddress.RowNumber && row <= a.LastAddress.RowNumber &&
                column >= a.FirstAddress.ColumnNumber && column <= a.LastAddress.ColumnNumber)
                return row != a.FirstAddress.RowNumber || column != a.FirstAddress.ColumnNumber;
        }
        return false;
    }

    internal string Read(int row,string key)
    {
        if (!int.TryParse(key.AsSpan(1),out var col) || col < 0 || col >= _columns.Count) return "";
        var cell = _sheet.Cell(row,_columns[col]);
        if (cell.HasFormula)
        {
            try { return _formulas.Calculate(cell.Address.ToString())
                .ToString("0.##########",CultureInfo.GetCultureInfo("ru-RU")); }
            catch (Exception ex) when (ex is ArithmeticException or InvalidDataException or FormatException)
            {
                return cell.FormulaA1.Contains("#REF!",StringComparison.OrdinalIgnoreCase)
                    ? "#ССЫЛКА!" : "#ОШИБКА!";
            }
        }
        var value = cell.CachedValue;
        if (value.IsBlank) return "";
        if (value.IsDateTime)
            return value.GetDateTime().ToString("dd.MM.yyyy",CultureInfo.GetCultureInfo("ru-RU"));
        if (value.IsNumber)
            return value.GetNumber().ToString("G10",CultureInfo.GetCultureInfo("ru-RU"));
        return value.ToString();
    }

    internal void Write(int row,string key,string text)
    {
        if (!int.TryParse(key.AsSpan(1),out var col) || col < 0 || col >= _columns.Count) return;
        var excelColumn = _columns[col];
        var cell = _sheet.Cell(row,excelColumn);
        if (cell.HasFormula || IsMergedFollower(row,excelColumn))
            throw new InvalidOperationException("Нельзя редактировать формулу или объединённую ячейку.");
        if (string.Equals(Read(row,key),text,StringComparison.Ordinal)) return;
        var value = text.Trim();
        if (value.Length == 0) cell.Clear(XLClearOptions.Contents);
        else if (cell.CachedValue.IsDateTime &&
            DateTime.TryParseExact(value,"dd.MM.yyyy",CultureInfo.GetCultureInfo("ru-RU"),
                DateTimeStyles.None,out var date))
            cell.Value = date;
        else if (decimal.TryParse(value.Replace(',','.'),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,out var number))
            cell.Value = (double)number;
        else cell.Value = value; // Strings beginning with '=' remain text, not executable formulas.
        _formulas.Clear();
        _markDirty();
        _table.Dispatcher.BeginInvoke(() => _table.Items.Refresh(), DispatcherPriority.Background);
    }

    private sealed class MetalGridRow
    {
        private readonly MetalRegisterSheet _page;
        public int ExcelRow { get; }
        public MetalGridRow(MetalRegisterSheet page,int row) { _page=page;ExcelRow=row; }
        public string this[string key]
        {
            get => _page.Read(ExcelRow,key);
            set => _page.Write(ExcelRow,key,value);
        }
    }
}
