using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

internal static class WorkspaceWindowUi
{
    public static readonly Thickness Pad = new(20, 16, 20, 16);

    public static void ApplyChrome(Window window)
    {
        window.SetResourceReference(Control.FontFamilyProperty, "UiFontFamily");
        window.SetResourceReference(Control.FontSizeProperty, "UiBodySize");
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        System.Windows.Media.TextOptions.SetTextFormattingMode(window, System.Windows.Media.TextFormattingMode.Display);
        System.Windows.Media.TextOptions.SetTextRenderingMode(window, System.Windows.Media.TextRenderingMode.ClearType);
    }

    public static TextBlock Heading(string title)
    {
        var heading = new TextBlock
        {
            Text = title
        };
        heading.SetResourceReference(FrameworkElement.StyleProperty, "UiHeadingText");
        heading.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormGroupHeadingGap");
        heading.SetResourceReference(TextBlock.FontSizeProperty, "UiH2Size");
        return heading;
    }

    public static void StyleTable(DataGrid grid)
    {
        grid.SetResourceReference(FrameworkElement.StyleProperty, "DocumentTable");
    }

    public static void StyleColumn(DataGridTextColumn column, TextAlignment alignment)
    {
        var element = alignment == TextAlignment.Right ? "CompactCostMoney" :
            alignment == TextAlignment.Center ? "CompactCostCenter" : "CompactCostText";
        column.ElementStyle = (Style)Application.Current.FindResource(element);
        column.EditingElementStyle = (Style)Application.Current.FindResource(
            alignment == TextAlignment.Right ? "CompactCostMoneyEditor" :
            alignment == TextAlignment.Center ? "CompactCostCenterEditor" : "CompactCostEditor");
        column.HeaderStyle = (Style)Application.Current.FindResource(
            alignment == TextAlignment.Right ? "CompactCostMoneyHeader" :
            alignment == TextAlignment.Center ? "CompactCostHeader" : "CompactCostTextHeader");
        if (alignment != TextAlignment.Left && column.Binding is Binding binding &&
            binding.Path?.Path is { Length: > 0 } path)
            TableColumnMenu.SetFitDisplayPath(column, path);
    }

    public static void StyleButton(Button button, bool primary = false, string? icon = null)
    {
        button.SetResourceReference(FrameworkElement.StyleProperty,
            primary ? "PythonToolbarPrimary" : "PythonToolbarButton");
        if (icon is not null) button.SetResourceReference(ContentControl.ContentTemplateProperty, icon);
    }

    public static TextBlock Hint(string text)
    {
        var hint = new TextBlock { Text = text };
        hint.SetResourceReference(FrameworkElement.StyleProperty, "UiSecondaryText");
        hint.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogIntroGap");
        return hint;
    }

    public static Button Button(string label, RoutedEventHandler action)
    {
        var button = new Button { Content = label, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0) };
        StyleButton(button, icon: label switch
        {
            "Назад" => "IconLabelArrowBack",
            "Далее" => "IconLabelArrowForward",
            "Отметить страницу" => "IconLabelCheck",
            "Удалить" => "IconLabelTrash",
            "Обновить" => "IconLabelRefresh",
            "Добавить" or "Новый тег" => "IconLabelPlus",
            "Переименовать / объединить" => "IconLabelEdit",
            _ => null
        });
        button.Click += action;
        return button;
    }

    public static DataGrid Table(bool multiple = false)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            CanUserDeleteRows = false, CanUserReorderColumns = false,
            SelectionMode = multiple ? DataGridSelectionMode.Extended : DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow,
            MinRowHeight = 30, AlternationCount = 2, HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        StyleTable(grid);
        return grid;
    }

    public static DataGridTextColumn Column(string label, string name, double width = 0)
    {
        var column = new DataGridTextColumn { Header = label, Binding = new Binding(name),
            Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star) };
        StyleColumn(column, name switch
        {
            "PriceText" or "PurchaseText" or "QtyText" or "Uses" => TextAlignment.Right,
            "Unit" or "Date" => TextAlignment.Center,
            _ => TextAlignment.Left
        });
        return column;
    }

    public static StackPanel Buttons(params UIElement[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    public static void Error(Window owner, Exception ex, string title) =>
        MessageBox.Show(owner, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>Ctrl+P — cancellable, stale-safe search across objects, documents and positions.</summary>
public sealed class GlobalSearchWindow : Window
{
    private readonly string _connectionString;
    private readonly TextBox _input = new() { MinHeight = 34,
        ToolTip = "Объект, юридический договор, приложение, ДС, КП или позиция: несколько слов в любом порядке" };
    private readonly TextBlock _status = WorkspaceWindowUi.Hint("Начните печатать — поиск по всей базе");
    private readonly ListBox _results = new();
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private CancellationTokenSource? _searchCancellation;
    private int _request;

    public SqlServerWorkspaceNavigation.SearchHit? SelectedResult { get; private set; }

    public GlobalSearchWindow(string connectionString)
    {
        _connectionString = connectionString;
        Title = "Найти везде"; Width = 720; Height = 520;
        MinWidth = 520; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WorkspaceWindowUi.ApplyChrome(this);
        _input.SetResourceReference(FrameworkElement.StyleProperty, "PythonSearch");
        var panel = new DockPanel();
        panel.SetResourceReference(FrameworkElement.MarginProperty, "LayoutCompareDialogMargin");
        Content = panel;
        var search = ReferenceWindowUi.Search(_input,"Объект, договор, приложение, ДС, КП или позиция: «Махалина», «Ballu BSO», «№ 5»…");
        DockPanel.SetDock(search, Dock.Top);
        panel.Children.Add(search);
        var resultStyle = new Style(typeof(ListBoxItem));
        resultStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        var resultText = new FrameworkElementFactory(typeof(TextBlock));
        resultText.SetBinding(TextBlock.TextProperty, new Binding("Display"));
        resultText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        _results.ItemTemplate = new DataTemplate { VisualTree = resultText };
        ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled);
        resultStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,6,8,6)));
        resultStyle.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,new Binding("Display")));
        _results.ItemContainerStyle = resultStyle;
        _status.Margin = new Thickness(0, 8, 0, 8);
        DockPanel.SetDock(_status, Dock.Top);
        panel.Children.Add(_status);
        var help = WorkspaceWindowUi.Hint("↑ ↓ — выбрать, Enter — открыть, Esc — закрыть");
        help.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(help, Dock.Bottom);
        panel.Children.Add(help);
        panel.Children.Add(_results);

        _delay.Tick += async (_, _) => { _delay.Stop(); await SearchAsync(); };
        _input.TextChanged += (_, _) =>
        {
            ++_request;
            _searchCancellation?.Cancel();
            _results.ItemsSource = null;
            _status.Text = "Поиск…";
            _delay.Stop();
            _delay.Start();
        };
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (_results.Items.Count > 0)
                {
                    var step = e.Key == Key.Down ? 1 : -1;
                    _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + step, 0, _results.Items.Count - 1);
                    _results.ScrollIntoView(_results.SelectedItem);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
        };
        _results.MouseDoubleClick += (_, _) => OpenSelected();
        _results.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
        Loaded += async (_, _) => { _input.Focus(); await SearchAsync(); };
        Closed += (_, _) =>
        {
            _delay.Stop();
            ++_request;
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
        };
    }

    private async Task SearchAsync()
    {
        var request = ++_request;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var ct = _searchCancellation.Token;
        var query = _input.Text;
        _status.Text = "Поиск в SQL Server…";
        try
        {
            var hits = await SqlServerWorkspaceNavigation.SearchAsync(_connectionString, query, ct);
            if (request != _request || ct.IsCancellationRequested) return;
            _results.ItemsSource = hits;
            var view = CollectionViewSource.GetDefaultView(_results.ItemsSource);
            view.GroupDescriptions.Add(new PropertyGroupDescription("KindTitle"));
            if (hits.Count > 0) _results.SelectedIndex = 0;
            _status.Text = string.IsNullOrWhiteSpace(query)
                ? "Начните печатать — показаны активные объекты"
                : hits.Count == 0 ? "Ничего не найдено"
                : $"Найдено: {hits.Count}" + (hits.Count >= 60 ? " · возможны ещё результаты" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (request == _request) _status.Text = "Ошибка поиска: " + ex.Message;
        }
    }

    private void OpenSelected()
    {
        if (_results.SelectedItem is not SqlServerWorkspaceNavigation.SearchHit hit) return;
        SelectedResult = hit;
        DialogResult = true;
    }
}

/// <summary>Catalog manage mode: bounded page reads and price history, using existing SQL write service.</summary>
public sealed class CatalogWindow : Window
{
    private readonly string _connectionString;
    private readonly TextBox _filter = new() { MinHeight = 32,
        ToolTip = "Несколько слов в любом порядке" };
    private readonly TextBlock _count = WorkspaceWindowUi.Hint("");
    private readonly DataGrid _items = WorkspaceWindowUi.Table();
    private readonly DataGrid _history = WorkspaceWindowUi.Table();
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly Button _prev;
    private readonly Button _next;
    private int _page;
    private int _request;
    private int _historyRequest;
    private CancellationTokenSource? _pageCancellation;
    private CancellationTokenSource? _historyCancellation;
    private bool _closed;
    private bool _writing;
    private string _revision = "";

    private bool CanWrite => !_closed && !_writing && !_delay.IsEnabled &&
        !string.IsNullOrWhiteSpace(_revision);

    private void InvalidatePage()
    {
        ++_request;
        ++_historyRequest;
        _pageCancellation?.Cancel();
        _historyCancellation?.Cancel();
        _revision = "";
        _history.ItemsSource = null;
        _prev.IsEnabled = _next.IsEnabled = false;
    }

    public CatalogWindow(string connectionString)
    {
        _connectionString = connectionString;
        Title = "Справочник номенклатуры"; Width = 1180; Height = 680;
        MinWidth = 780; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WorkspaceWindowUi.ApplyChrome(this);
        _filter.SetResourceReference(FrameworkElement.StyleProperty, "PythonSearch");
        var root = new DockPanel { Margin = WorkspaceWindowUi.Pad };
        Content = root;
        var heading = WorkspaceWindowUi.Heading("Справочник");
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var hint = WorkspaceWindowUi.Hint("Наполняется из КП. Цена — последняя записанная; история содержит источник и дату.");
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var search = ReferenceWindowUi.Search(_filter,"Поиск: несколько слов в любом порядке, например «клапан 500 250»");
        DockPanel.SetDock(search, Dock.Top); search.Margin = new Thickness(0,0,0,8);
        root.Children.Add(search);

        var pager = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(pager, Dock.Top); root.Children.Add(pager);
        var nav = WorkspaceWindowUi.Buttons(
            _prev = WorkspaceWindowUi.Button("Назад", (_, _) => { _page = Math.Max(0, _page - 1); _ = ReloadAsync(); }),
            _next = WorkspaceWindowUi.Button("Далее", (_, _) => { _page++; _ = ReloadAsync(); }));
        DockPanel.SetDock(nav, Dock.Right); pager.Children.Add(nav);
        pager.Children.Add(_count);

        var footer = WorkspaceWindowUi.Buttons(
            WorkspaceWindowUi.Button("Обновить", async (_, _) => await ReloadAsync()),
            WorkspaceWindowUi.Button("Закрыть", (_, _) => Close()));
        footer.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var sections = new Grid();
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight=90 });
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(6) });
        sections.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight=120 });
        _items.Columns.Add(WorkspaceWindowUi.Column("Наименование", "Name"));
        _items.Columns.Add(WorkspaceWindowUi.Column("Ед.", "Unit", 70));
        _items.Columns.Add(WorkspaceWindowUi.Column("Цена", "PriceText", 110));
        _items.Columns.Add(WorkspaceWindowUi.Column("Закуп.", "PurchaseText", 110));
        _items.Columns.Add(WorkspaceWindowUi.Column("Исп.", "Uses", 70));
        sections.Children.Add(_items); Grid.SetRow(_items, 0);
        var splitter=ReferenceWindowUi.HistorySplitter();
        sections.Children.Add(splitter); Grid.SetRow(splitter,1);
        var historyPanel=new DockPanel{Margin=new Thickness(0,6,0,0)};
        var historyBar=new DockPanel{Margin=new Thickness(0,0,0,6)};
        var historyActions=WorkspaceWindowUi.Buttons(
            WorkspaceWindowUi.Button("Переименовать / объединить", async (_,_)=>await RenameAsync()),
            WorkspaceWindowUi.Button("Удалить", async (_,_)=>await DeleteAsync()));
        DockPanel.SetDock(historyActions,Dock.Right);historyBar.Children.Add(historyActions);
        historyBar.Children.Add(WorkspaceWindowUi.Heading("История цен"));
        DockPanel.SetDock(historyBar,Dock.Top);historyPanel.Children.Add(historyBar);
        _history.Columns.Add(WorkspaceWindowUi.Column("Объект", "Object"));
        _history.Columns.Add(WorkspaceWindowUi.Column("Документ", "Contract"));
        _history.Columns.Add(WorkspaceWindowUi.Column("Дата", "Date", 110));
        _history.Columns.Add(WorkspaceWindowUi.Column("Система", "System"));
        _history.Columns.Add(WorkspaceWindowUi.Column("Кол-во", "QtyText", 90));
        _history.Columns.Add(WorkspaceWindowUi.Column("Цена", "PriceText", 110));
        _history.Columns.Add(WorkspaceWindowUi.Column("Закуп.", "PurchaseText", 110));
        TableColumnMenu.SetKey(_items, "catalog-manage");
        _items.CanUserReorderColumns = true;
        TableColumnMenu.SetKey(_history, "catalog-price-history");
        _history.CanUserReorderColumns = true;
        historyPanel.Children.Add(_history);sections.Children.Add(historyPanel); Grid.SetRow(historyPanel, 2);
        root.Children.Add(sections);

        _delay.Tick += async (_, _) => { _delay.Stop(); _page = 0; await ReloadAsync(); };
        _filter.TextChanged += (_, _) =>
        {
            if (_closed) return;
            InvalidatePage();
            _page = 0;
            _delay.Stop();
            _delay.Start();
        };
        _items.SelectionChanged += async (_, _) => await LoadHistoryAsync();
        Loaded += async (_, _) => { _filter.Focus(); await ReloadAsync(); };
        Closing += (_, args) =>
        {
            if (!_writing) return;
            args.Cancel = true;
            _count.Text = "Дождитесь завершения записи.";
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _delay.Stop();
            ++_request;
            ++_historyRequest;
            _pageCancellation?.Cancel();
            _historyCancellation?.Cancel();
            _revision = "";
        };
    }

    private SqlServerWorkspaceNavigation.CatalogEntry? Selected =>
        _items.SelectedItem as SqlServerWorkspaceNavigation.CatalogEntry;

    private async Task ReloadAsync()
    {
        if (_closed || _writing) return;
        _delay.Stop();
        InvalidatePage();
        var generation = _request;
        using var cancellation = new CancellationTokenSource();
        _pageCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            _count.Text = "Загрузка…";
            var search = _filter.Text;
            var skip = _page * 200;
            // 201st row is a cheap look-ahead; no expensive COUNT over a 50k-item catalog.
            var result = await Task.Run(() => SqlServerWorkspaceNavigation.CatalogSnapshot(
                _connectionString, search, skip, 201, token: token), token);
            if (_closed || token.IsCancellationRequested || generation != _request) return;
            var rows = result.Rows;
            _revision = result.Revision;
            _items.ItemsSource = rows.Take(200).ToArray();
            _prev.IsEnabled = _page > 0;
            _next.IsEnabled = rows.Count > 200;
            _count.Text = $"Страница {_page + 1} · {Math.Min(200, rows.Count)} записей";
        }
        catch (Exception ex)
        {
            if (_closed || token.IsCancellationRequested || generation != _request) return;
            InvalidatePage();
            _items.ItemsSource = null;
            _count.Text = "Не удалось загрузить справочник. Нажмите «Обновить».";
            WorkspaceWindowUi.Error(this, ex, "Справочник");
        }
        finally
        {
            if (ReferenceEquals(_pageCancellation, cancellation)) _pageCancellation = null;
        }
    }

    private async Task LoadHistoryAsync()
    {
        var request = ++_historyRequest;
        _historyCancellation?.Cancel();
        _history.ItemsSource = null;
        if (_closed || _writing || string.IsNullOrWhiteSpace(_revision) || Selected is not { } entry) return;
        var id = entry.Id;
        var revision = _revision;
        using var cancellation = new CancellationTokenSource();
        _historyCancellation = cancellation;
        var token = cancellation.Token;
        bool IsCurrent() => !_closed && !_writing && !token.IsCancellationRequested &&
            request == _historyRequest && revision == _revision && Selected?.Id == id;
        try
        {
            var rows = await Task.Run(() => SqlServerWorkspaceNavigation.PriceHistory(_connectionString, id, token), token);
            if (IsCurrent()) _history.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            if (IsCurrent()) WorkspaceWindowUi.Error(this, ex, "История цен");
        }
        finally
        {
            if (ReferenceEquals(_historyCancellation, cancellation)) _historyCancellation = null;
        }
    }

    private async Task RenameAsync()
    {
        if (!CanWrite || Selected is not { } item) return;
        var revision = _revision;
        var generation = _request;
        var name = new PromptDialog("Переименовать", "Наименование", item.Name) { Owner = this };
        if (name.ShowDialog() != true || name.Value.Length == 0) return;
        var unit = new PromptDialog("Единица измерения", "Ед. изм.", item.Unit) { Owner = this };
        if (unit.ShowDialog() != true) return;
        if (name.Value == item.Name && unit.Value == item.Unit) return;
        if (MessageBox.Show(this,
            "Если позиция с таким названием и единицей уже есть, записи будут объединены. Продолжить?",
            "Справочник", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var newName = name.Value;
        var newUnit = unit.Value;
        await RunCatalogWriteAsync(item, revision, generation, "Переименование", () =>
            SqlServerCatalog.UpdateCatalogItem(_connectionString, item.Id, newName, newUnit, revision));
    }

    private async Task DeleteAsync()
    {
        if (!CanWrite || Selected is not { } item) return;
        var revision = _revision;
        var generation = _request;
        if (MessageBox.Show(this,
            $"Удалить «{item.Name}» из каталога? Привязки у {item.Uses} существующих позиций будут сняты, сами позиции останутся.",
            "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunCatalogWriteAsync(item, revision, generation, "Удаление", () =>
            SqlServerCatalog.DeleteCatalogItem(_connectionString, item.Id, revision));
    }

    private async Task RunCatalogWriteAsync(SqlServerWorkspaceNavigation.CatalogEntry item,
        string revision, int generation, string caption, Action action)
    {
        if (!CanWrite || generation != _request || revision != _revision ||
            !ReferenceEquals(Selected, item))
        {
            if (!_closed) _count.Text = "Справочник изменился. Выберите позицию заново.";
            return;
        }
        _writing = true;
        InvalidatePage();
        _count.Text = "Сохранение…";
        try { await Task.Run(action); }
        catch (Exception ex)
        {
            if (!_closed) WorkspaceWindowUi.Error(this, ex, caption);
        }
        finally
        {
            _writing = false;
            if (!_closed) await ReloadAsync(); // reconcile even an uncertain write; never replay it
        }
    }
}
