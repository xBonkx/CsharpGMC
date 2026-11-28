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
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using Microsoft.Win32;

namespace GmcSmeta.App;

/// <summary>Shared, versioned Excel metal register. Edits preserve the workbook structure and cell styles.</summary>
internal sealed class MetalRegisterWindow : Window
{
    private readonly string _connectionString;
    private SqlServerMetalWarehouse? _warehouse;
    private readonly WrapPanel _header = new() { Margin = new Thickness(0,0,0,9) };
    private MetalRegisterSheet? _activePage;
    private bool _busy, _switchingTabs;
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = new();
    private XLWorkbook? _book;
    private long? _version;
    private bool _dirty;

    public MetalRegisterWindow(string connectionString)
    {
        Title = "Учёт металла";
        Width = 1200; Height = 700; MinWidth = 820; MinHeight = 490;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        DynamicFormLayout.Apply(this);
        _tabs.SetResourceReference(FrameworkElement.StyleProperty,"PythonTabbedPane");
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        _connectionString = connectionString;
        var dock = new DockPanel { Margin = new Thickness(12) };
        Content = dock;
        DockPanel.SetDock(_header,Dock.Top);
        dock.Children.Add(_header);
        void Button(string title, RoutedEventHandler action)
        {
            var button = new Button { Content=title,Padding=new Thickness(14,6,14,6),Margin=new Thickness(0,0,6,6) };
            button.SetResourceReference(FrameworkElement.StyleProperty,"PythonToolbarButton");
            button.Click += action; _header.Children.Add(button);
        }
        Button("Загрузить Excel…",Import_Click);
        Button("Сохранить для всех",Save_Click);
        Button("Выгрузить Excel…",Export_Click);
        Button("Сверка для склада…",Opening_Click);
        Button("Обновить",Refresh_Click);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "UiMutedTextBrush");
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0,8,0,0);
        DockPanel.SetDock(_status,Dock.Bottom);
        dock.Children.Add(_status);
        dock.Children.Add(_tabs);
        _tabs.SelectionChanged += Tabs_SelectionChanged;
        SetBusy(false);
        Closing += (_, e) =>
        {
            if (_busy || !CommitEditor() || !ConfirmDiscard()) e.Cancel = true;
        };
        Closed += (_, _) =>
        {
            _tabs.SelectionChanged -= Tabs_SelectionChanged;
            _tabs.Items.Clear();
            _activePage = null;
            _book?.Dispose();
            _book = null;
        };
        Loaded += InitializeAsync;
    }

    private async void InitializeAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= InitializeAsync;
        SetBusy(true);
        try { _warehouse = await Task.Run(() => new SqlServerMetalWarehouse(_connectionString)); }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
        if (_warehouse is not null) await Reload();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _header.IsEnabled = !busy && _warehouse is not null;
        _tabs.IsEnabled = !busy;
    }

    private bool CommitEditor() => _activePage?.CommitEditor() != false;

    private bool ConfirmDiscard() => !_dirty || MessageBox.Show(this,
        "Отбросить несохранённые изменения?", "Учёт металла",
        MessageBoxButton.YesNo,MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void MarkDirty()
    {
        _dirty = true;
        Title = "Учёт металла *";
        _status.Text = "Есть несохранённые изменения; при конфликте можно выгрузить локальный Excel.";
    }

    private sealed record PreparedBook(XLWorkbook Book, IReadOnlyList<MetalRegisterSheet.Description> Sheets);

    // ClosedXML access is exclusively on the worker before publication and
    // on the dispatcher afterwards. Do not serialize a live grid-bound book
    // concurrently with WPF cell bindings.
    private static PreparedBook Prepare(XLWorkbook book)
    {
        try
        {
            var sheets = book.Worksheets.Select(MetalRegisterSheet.Describe).ToArray();
            if (sheets.Length == 0 || sheets.Any(s => (long)s.RowLimit * s.ColLimit > 250_000))
                throw new InvalidDataException("В книге нет листов либо есть лист больше 250 000 ячеек.");
            return new PreparedBook(book, sheets);
        }
        catch { book.Dispose(); throw; }
    }

    private void Display(PreparedBook? prepared)
    {
        var items = new List<TabItem>();
        MetalRegisterSheet? first = null;
        try
        {
            if (prepared is not null)
            {
                foreach (var sheet in prepared.Sheets)
                    items.Add(new TabItem
                    {
                        Header = sheet.Sheet.Name == "Лист1" && sheet.Sections.Count != 0
                            ? "Объёмы по объектам" : sheet.Sheet.Name,
                        Tag = sheet
                    });
                // Only the first sheet has a grid initially. Build it before
                // destroying the previously displayed workbook.
                first = new MetalRegisterSheet(prepared.Sheets[0], MarkDirty);
                items[0].Content = first;
            }
        }
        catch { prepared?.Book.Dispose(); throw; }

        _switchingTabs = true;
        try
        {
            _tabs.Items.Clear();
            _activePage = null;
            _book?.Dispose();
            _book = prepared?.Book;
            foreach (var item in items) _tabs.Items.Add(item);
            if (items.Count != 0) _tabs.SelectedIndex = 0;
            _activePage = first;
            _dirty = false;
            Title = "Учёт металла";
        }
        finally { _switchingTabs = false; }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_switchingTabs || !ReferenceEquals(e.Source, _tabs) ||
            _tabs.SelectedItem is not TabItem tab ||
            tab.Tag is not MetalRegisterSheet.Description sheet) return;
        var previous = e.RemovedItems.OfType<TabItem>().FirstOrDefault();
        try
        {
            if (_busy || !CommitEditor())
            {
                RestoreTab(previous);
                return;
            }
            if (tab.Content is not MetalRegisterSheet page)
            {
                page = new MetalRegisterSheet(sheet, MarkDirty);
                tab.Content = page;
            }
            _activePage = page;
        }
        catch (Exception ex)
        {
            RestoreTab(previous);
            Error(ex);
        }
    }

    private void RestoreTab(TabItem? previous)
    {
        if (previous is null) return;
        _switchingTabs = true;
        try { _tabs.SelectedItem = previous; }
        finally { _switchingTabs = false; }
    }

    private async Task Reload()
    {
        if (_busy || _warehouse is not { } store || !CommitEditor() || !ConfirmDiscard()) return;
        SetBusy(true);
        try
        {
            _status.Text = "Загрузка общей книги…";
            var data = await Task.Run(() =>
            {
                var shared = store.ReadRegister();
                if (shared is null) return (Book: (PreparedBook?)null, Version: (long?)null);
                var book = Prepare(new XLWorkbook(new MemoryStream(shared.Data, false)));
                return (Book: (PreparedBook?)book, Version: (long?)shared.Revision);
            });
            Display(data.Book);
            _version = data.Version;
            _status.Text = data.Version is null ? "Нет общей Excel-книги." :
                "Версия " + data.Version + " · изменения коллег защищены от перезаписи.";
        }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
    }

    private static PreparedBook OpenLimited(string path)
    {
        if (new FileInfo(path).Length is 0 or > 5_000_000)
            throw new InvalidDataException("Excel-книга должна быть не больше 5 МБ.");
        return Prepare(new XLWorkbook(path));
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _warehouse is null || !CommitEditor() || !ConfirmDiscard()) return;
        var picker = new OpenFileDialog { Title = "Загрузить таблицу металла",Filter = "Excel (*.xlsx)|*.xlsx" };
        if (picker.ShowDialog(this) != true) return;
        if (_book is not null && MessageBox.Show(this,
            "Заменить текущую таблицу выбранным Excel при сохранении для всех?",
            "Учёт металла",MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            var path = picker.FileName;
            var workbook = await Task.Run(() => OpenLimited(path));
            Display(workbook);
            MarkDirty();
        }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _warehouse is not { } store || _book is not { } book ||
            !CommitEditor() || !_dirty) return;
        var version = _version;
        SetBusy(true);
        try
        {
            var bytes = LiveWorkbookSnapshot.Capture(book);
            var mainRevision = await Task.Run(store.Revision);
            var next = await Task.Run(() => store.SaveRegister(bytes,version,mainRevision));
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
        finally { SetBusy(false); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _book is not { } book || !CommitEditor()) return;
        var picker = new SaveFileDialog
        {
            Title = "Выгрузить таблицу металла", FileName = "Учёт металла.xlsx",
            DefaultExt = ".xlsx",Filter = "Excel (*.xlsx)|*.xlsx"
        };
        if (picker.ShowDialog(this) != true) return;
        var path = picker.FileName;
        SetBusy(true);
        try
        {
            // Never bind the live editable workbook to the exported filename.
            // It must survive deletion/locking of that file and later saves.
            var bytes = LiveWorkbookSnapshot.Capture(book);
            await Task.Run(() => ExportFilePublication.WriteBytes(path, bytes));
            _status.Text = "Файл сохранён: " + path;
        }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
    }

    private async void Opening_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _warehouse is not { } store || !CommitEditor()) return;
        var picker = new OpenFileDialog { Title = "Сверка исходных остатков",Filter = "Excel (*.xlsx)|*.xlsx" };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            var path = picker.FileName;
            var preview = await Task.Run(() => MetalOpeningReader.Read(path));
            if (new MetalOpeningPreviewDialog(preview) { Owner = this }.ShowDialog() != true) return;
            if (MessageBox.Show(this, "Перенести остатки в склад один раз?",
                "Первоначальный импорт",MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var revision = await Task.Run(store.Revision);
            await Task.Run(() => store.ImportOpening(
                preview.Records,preview.FileName,preview.Original,revision));
            MessageBox.Show(this,"Начальные остатки и потребности перенесены.","Склад металла");
        }
        catch (Exception ex) { Error(ex); }
        finally { SetBusy(false); }
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
        GridLinesVisibility = DataGridGridLinesVisibility.All, MinRowHeight = 27,
        HeadersVisibility = DataGridHeadersVisibility.All, RowHeaderWidth = 48
    };
    private readonly ComboBox _sections = new() { MinWidth = 230, Margin = new Thickness(6,0,0,4) };
    private readonly List<(int Header,string Name)> _sectionRows = [];
    private readonly int _rowLimit;
    private readonly int _colLimit;
    private IReadOnlyList<int> _columns = [];
    private int _heading;
    private int _sectionIndex = -1;
    private bool _sectionChanging, _refreshPending, _refreshQueued;

    internal sealed record Description(IXLWorksheet Sheet, int RowLimit, int ColLimit,
        IReadOnlyList<(int Header,string Name)> Sections);

    public bool HasSections => _sectionRows.Count != 0;

    public MetalRegisterSheet(IXLWorksheet sheet, Action markDirty) : this(Describe(sheet), markDirty) { }

    internal static Description Describe(IXLWorksheet sheet)
    {
        var rowLimit = sheet.LastRowUsed(XLCellsUsedOptions.Contents)?.RowNumber() ?? 0;
        var colLimit = sheet.LastColumnUsed(XLCellsUsedOptions.Contents)?.ColumnNumber() ?? 0;
        if ((long)rowLimit * colLimit > 250_000)
            throw new InvalidDataException("Лист больше 250 000 ячеек.");
        var sections = new List<(int Header,string Name)>();
        var names = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["металл/объект"] = "Металл в бухтах",
            ["металл в штрипсах"] = "Металл в штрипсах",
            ["металл в листах"] = "Металл в листах",
            ["огнезащита / объект"] = "Огнезащита"
        };
        for (var row = 1; row <= rowLimit; row++)
            if (names.TryGetValue(sheet.Cell(row,1).CachedValue.ToString().Trim(),out var title))
                sections.Add((row,title));
        return new Description(sheet,rowLimit,colLimit,sections);
    }

    internal MetalRegisterSheet(Description description, Action markDirty)
    {
        _sheet = description.Sheet; _markDirty = markDirty;
        _formulas = new MetalFormulaEvaluator(_sheet);
        _rowLimit = description.RowLimit;
        _colLimit = description.ColLimit;
        _sectionRows.AddRange(description.Sections);
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
            _sections.SelectionChanged += (_, _) =>
            {
                if (_sectionChanging) return;
                if (CommitEditor()) SetSection(_sections.SelectedIndex);
                else
                {
                    _sectionChanging = true;
                    try { _sections.SelectedIndex = _sectionIndex < 0 ? _sectionRows.Count : _sectionIndex; }
                    finally { _sectionChanging = false; }
                }
            };
        }
        _table.SetResourceReference(FrameworkElement.StyleProperty, "DocumentTable");
        _table.BeginningEdit += OnBeginningEdit;
        _table.RowEditEnding += (_, _) => QueueRefresh();
        _table.Loaded += (_, _) => QueueRefresh();
        ScrollViewer.SetIsDeferredScrollingEnabled(_table, false);
        VirtualizingPanel.SetVirtualizationMode(_table, VirtualizationMode.Recycling);
        _table.LoadingRow += (_, e) =>
        {
            e.Row.Header = e.Row.Item is MetalGridRow row ? row.ExcelRow : null;
        };
        dock.Children.Add(_table);
        if (HasSections) _sections.SelectedIndex = 0;
        else SetSection(-1);
    }

    internal bool CommitEditor() =>
        _table.CommitEdit(DataGridEditingUnit.Cell, true) &&
        _table.CommitEdit(DataGridEditingUnit.Row, true);

    private void SetSection(int index)
    {
        if (index >= _sectionRows.Count) index = -1;
        _sectionIndex = index;
        _heading = index < 0 ? 0 : _sectionRows[index].Header;
        var start = _heading == 0 ? 1 : _heading + 1;
        var end = index < 0 || index == _sectionRows.Count - 1
            ? _rowLimit : _sectionRows[index+1].Header - 1;
        var rows = new List<int>();
        var usedColumns = new bool[_colLimit + 1];
        if (_heading != 0)
            for (var col = 1; col <= _colLimit; col++)
                usedColumns[col] = !_sheet.Cell(_heading,col).CachedValue.IsBlank;
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
                    if (cell.CachedValue.IsBlank && !cell.HasFormula) continue;
                    nonempty = usedColumns[col] = true;
                }
                if (!nonempty) continue;
            }
            rows.Add(row);
        }
        _columns = Enumerable.Range(1,_colLimit)
            .Where(col => _heading == 0 || usedColumns[col]).ToList();
        _table.ColumnHeaderHeight = _heading == 0 ? 32 : 72;
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
                ElementStyle = DisplayStyle(indexName),
                CellStyle = CellStyle(indexName),
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
        if (_table.Items.Count > 0 && _table.Columns.Count > 0)
            _table.ScrollIntoView(_table.Items[0],_table.Columns[0]);
    }

    private static Style DisplayStyle(string key)
    {
        var style = new Style(typeof(TextBlock),(Style)Application.Current.FindResource("CompactCostText"));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty,
            new Binding { Converter = new SheetCellPresentation(key,"alignment") }));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
            new Binding { Converter = new SheetCellPresentation(key,"tooltip") }));
        return style;
    }

    private static Style CellStyle(string key)
    {
        var style = new Style(typeof(DataGridCell),(Style)Application.Current.FindResource("CompactCostCell"));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            new Binding { Converter = new SheetCellPresentation(key,"background") }));
        return style; // inherited selection/edit triggers take priority over the background setter
    }

    private sealed class SheetCellPresentation(string key,string kind) : IValueConverter
    {
        private static readonly Brush FormulaBrush = MakeFormulaBrush();
        private static Brush MakeFormulaBrush()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0xE8,0xEF,0xF9));
            brush.Freeze();
            return brush;
        }

        public object Convert(object value,Type targetType,object parameter,CultureInfo culture)
        {
            if (value is not MetalGridRow row ||
                !int.TryParse(key.AsSpan(1),out var col) || col < 0 || col >= row.Page._columns.Count)
                return DependencyProperty.UnsetValue;
            var cell = row.Page._sheet.Cell(row.ExcelRow,row.Page._columns[col]);
            return kind switch
            {
                "background" => cell.HasFormula ? FormulaBrush : Brushes.Transparent,
                "alignment" => cell.HasFormula || cell.CachedValue.IsNumber ? TextAlignment.Right : TextAlignment.Left,
                "tooltip" => cell.HasFormula ? "=" + cell.FormulaA1.TrimStart('=') : row[key],
                _ => DependencyProperty.UnsetValue
            };
        }
        public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture) =>
            Binding.DoNothing;
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
        _refreshPending = true;
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (!_refreshPending || _refreshQueued) return;
        _refreshQueued = true;
        _table.Dispatcher.BeginInvoke(new Action(() =>
        {
            _refreshQueued = false;
            if (!_refreshPending) return;
            if (!_table.IsLoaded) return;
            if (_table.Items is IEditableCollectionView { IsEditingItem: true }) return;
            _refreshPending = false;
            _table.Items.Refresh();
        }), DispatcherPriority.Background);
    }

    private sealed class MetalGridRow
    {
        private readonly MetalRegisterSheet _page;
        public int ExcelRow { get; }
        internal MetalRegisterSheet Page => _page;
        public MetalGridRow(MetalRegisterSheet page,int row) { _page=page;ExcelRow=row; }
        public string this[string key]
        {
            get => _page.Read(ExcelRow,key);
            set => _page.Write(ExcelRow,key,value);
        }
    }
}
