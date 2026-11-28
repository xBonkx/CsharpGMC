using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Строка выбора «Из справочника»: отметка и количество (CatalogDialog pick=True).</summary>
public sealed class CatalogPickRow : INotifyPropertyChanged
{
    private bool _selected;
    private string _qty = "1";

    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public decimal Price { get; init; }
    public decimal PurchasePrice { get; init; }
    public string PriceText => Price.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
    public string PurchaseText => PurchasePrice.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));

    public bool Selected { get => _selected; set { _selected = value; Changed(); } }
    public string Qty { get => _qty; set { _qty = value; Changed(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Выбранная позиция справочника с количеством.</summary>
public sealed record CatalogPick(long Id, string Name, string Unit, decimal Qty,
    decimal Price, decimal PurchasePrice);

/// <summary>
/// «Из справочника» (Ctrl+K): поиск по номенклатуре, отметки и количество —
/// перенос CatalogDialog(pick=True) из kp_editor.add_from_catalog.
/// </summary>
public sealed class CatalogPickerWindow : Window
{
    private const int PageSize = 200;
    private readonly string _connectionString;
    private readonly TextBox _filter = new() { MinHeight = 32, ToolTip = "Несколько слов в любом порядке" };
    private readonly TextBlock _count = WorkspaceWindowUi.Hint("");
    private readonly DataGrid _items = WorkspaceWindowUi.Table();
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private IReadOnlyList<CatalogPickRow> _rows = Array.Empty<CatalogPickRow>();
    private readonly Dictionary<long, CatalogPickRow> _chosen = new();
    private readonly Button _prev;
    private readonly Button _next;
    private readonly Button _add;
    private int _page;
    private int _request;
    private CancellationTokenSource? _pageCancellation;
    private bool _closed;
    private bool _pageReady;
    private readonly string _expectedRevision;

    public IReadOnlyList<CatalogPick> Picked { get; private set; } = Array.Empty<CatalogPick>();

    public CatalogPickerWindow(string connectionString, string targetName, string expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(expectedRevision))
            throw new ArgumentException("Не задана ревизия сметы.", nameof(expectedRevision));
        _connectionString = connectionString;
        _expectedRevision = expectedRevision;
        Title = "Справочник номенклатуры"; Width = 980; Height = 680;
        MinWidth = 720; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;

        WorkspaceWindowUi.ApplyChrome(this);
        _filter.SetResourceReference(FrameworkElement.StyleProperty, "PythonSearch");
        var root = new DockPanel { Margin = WorkspaceWindowUi.Pad };
        Content = root;

        var heading = WorkspaceWindowUi.Heading($"Добавить из справочника → {targetName}");
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var hint = WorkspaceWindowUi.Hint(
            $"Куда: {targetName}. Отметьте позиции и укажите количество — цена и закуп подставятся из справочника. " +
            "Отметки сохраняются при поиске и листании.");
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var search = ReferenceWindowUi.Search(_filter,"Поиск: несколько слов в любом порядке, например «клапан 500 250»");
        DockPanel.SetDock(search, Dock.Top);search.Margin = new Thickness(0,0,0,8);
        root.Children.Add(search);

        var pager = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(pager, Dock.Top); root.Children.Add(pager);
        var nav = WorkspaceWindowUi.Buttons(
            _prev = WorkspaceWindowUi.Button("Назад", (_, _) => Navigate(-1)),
            _next = WorkspaceWindowUi.Button("Далее", (_, _) => Navigate(1)));
        _prev.IsEnabled = _next.IsEnabled = false;
        DockPanel.SetDock(nav, Dock.Right); pager.Children.Add(nav);
        pager.Children.Add(_count);

        // Массовое выделение затрагивает только загруженную страницу, не все результаты поиска.
        var pageSelection = WorkspaceWindowUi.Buttons(
            WorkspaceWindowUi.Button("Отметить страницу", (_, _) => SelectCurrentPage(true)),
            WorkspaceWindowUi.Button("Снять на странице", (_, _) => SelectCurrentPage(false)));
        pageSelection.HorizontalAlignment = HorizontalAlignment.Left;
        pageSelection.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(pageSelection, Dock.Top);
        root.Children.Add(pageSelection);

        _add = WorkspaceWindowUi.Button("Добавить в КП", (_, _) => Accept());
        WorkspaceWindowUi.StyleButton(_add, primary: true, icon: "IconLabelPlus");
        _add.IsDefault = true;
        _add.IsEnabled = false;
        var footer = WorkspaceWindowUi.Buttons(
            WorkspaceWindowUi.Button("Отмена", (_, _) => DialogResult = false),_add);
        footer.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        _items.IsReadOnly = false;
        _items.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "✓", Width = 37,
            Binding = new Binding(nameof(CatalogPickRow.Selected))
                { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
        });
        _items.Columns.Add(ReadOnly(WorkspaceWindowUi.Column("Наименование", nameof(CatalogPickRow.Name))));
        _items.Columns.Add(ReadOnly(WorkspaceWindowUi.Column("Ед.", nameof(CatalogPickRow.Unit), 70)));
        _items.Columns.Add(new DataGridTextColumn
        {
            Header = "Кол-во", Width = 90,
            Binding = new Binding(nameof(CatalogPickRow.Qty))
                { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
        });
        _items.Columns.Add(ReadOnly(WorkspaceWindowUi.Column("Цена", nameof(CatalogPickRow.PriceText), 110)));
        _items.Columns.Add(ReadOnly(WorkspaceWindowUi.Column("Закуп.", nameof(CatalogPickRow.PurchaseText), 110)));
        foreach (var col in _items.Columns.OfType<DataGridTextColumn>())
            if (col.Header?.ToString() == "Кол-во") WorkspaceWindowUi.StyleColumn(col, TextAlignment.Right);
        TableColumnMenu.SetProtect(_items, "Наименование|Кол-во");
        TableColumnMenu.SetKey(_items, "catalog-picker");
        _items.CanUserReorderColumns = true;
        _items.ItemsSource = _rows;
        root.Children.Add(_items);

        _delay.Tick += async (_, _) => { _delay.Stop(); _page = 0; await ReloadAsync(); };
        _filter.TextChanged += (_, _) =>
        {
            if (_closed) return;
            InvalidatePage();
            _count.Text = "Поиск…";
            _delay.Stop();
            _delay.Start();
        };
        Loaded += async (_, _) => { _filter.Focus(); await ReloadAsync(); };
        Closed += (_, _) =>
        {
            _closed = true;
            _delay.Stop();
            ++_request;
            _pageCancellation?.Cancel();
            _pageReady = false;
            ReleasePage();
            _chosen.Clear();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            DialogResult = false;
            e.Handled = true;
        };
    }

    private static DataGridColumn ReadOnly(DataGridColumn column)
    {
        column.IsReadOnly = true;
        return column;
    }

    private void InvalidatePage()
    {
        ++_request;
        _pageCancellation?.Cancel();
        _pageReady = false;
        _add.IsEnabled = false;
        _prev.IsEnabled = _next.IsEnabled = false;
    }

    private bool CommitPageEdit()
    {
        if (_items.CommitEdit(DataGridEditingUnit.Cell, true) &&
            _items.CommitEdit(DataGridEditingUnit.Row, true)) return true;
        _count.Text = "Завершите редактирование количества и повторите поиск.";
        return false;
    }

    private void ReleasePage()
    {
        foreach (var row in _rows) row.PropertyChanged -= SelectedRowChanged;
        _rows = Array.Empty<CatalogPickRow>();
        _items.ItemsSource = _rows;
    }

    private void Navigate(int step)
    {
        if (_closed || !_pageReady || !CommitPageEdit()) return;
        _page = Math.Max(0, _page + step);
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_closed) return;
        _delay.Stop();
        InvalidatePage();
        if (!CommitPageEdit()) return;
        SavePageState();
        ReleasePage();
        var generation = _request;
        using var cancellation = new CancellationTokenSource();
        _pageCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            _count.Text = "Загрузка…";
            var search = _filter.Text;
            var skip = _page * PageSize;
            // 201st row is a cheap look-ahead; no expensive COUNT over the catalog.
            var snapshot = await Task.Run(() => SqlServerWorkspaceNavigation.CatalogSnapshot(
                _connectionString, search, skip, PageSize + 1, _expectedRevision,
                includeUsageCount: false, token: token), token);
            if (_closed || token.IsCancellationRequested || generation != _request) return;
            var rows = snapshot.Rows;
            var page = new List<CatalogPickRow>(Math.Min(rows.Count, PageSize));
            foreach (var entry in rows.Take(PageSize))
            {
                var row = _chosen.TryGetValue(entry.Id, out var kept) ? kept : new CatalogPickRow
                {
                    Id = entry.Id, Name = entry.Name, Unit = entry.Unit,
                    Price = entry.Price, PurchasePrice = entry.PurchasePrice,
                };
                // Reused selections can return from other pages; avoid duplicate subscriptions.
                row.PropertyChanged -= SelectedRowChanged;
                row.PropertyChanged += SelectedRowChanged;
                page.Add(row);
            }
            _rows = page;
            _items.ItemsSource = _rows;
            _pageReady = true;
            _add.IsEnabled = true;
            _prev.IsEnabled = _page > 0;
            _next.IsEnabled = rows.Count > PageSize;
            RefreshSelectionCount();
        }
        catch (Exception ex)
        {
            if (!_closed && !token.IsCancellationRequested && generation == _request)
            {
                _count.Text = "Поиск не завершён. Измените запрос или откройте справочник заново.";
                WorkspaceWindowUi.Error(this, ex, Title);
            }
        }
        finally
        {
            if (ReferenceEquals(_pageCancellation, cancellation)) _pageCancellation = null;
        }
    }

    private void SelectedRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed || e.PropertyName != nameof(CatalogPickRow.Selected) || sender is not CatalogPickRow row)
            return;
        if (row.Selected) _chosen[row.Id] = row;
        else _chosen.Remove(row.Id);
        if (_pageReady) RefreshSelectionCount();
    }

    private void RefreshSelectionCount() =>
        _count.Text = $"Страница {_page + 1} · {_rows.Count} записей · отмечено: {_chosen.Count}";

    /// <summary>Только текущая страница; выбор на других страницах не сбрасывается.</summary>
    internal static void SetSelectedOnPage(IEnumerable<CatalogPickRow> rows, bool selected)
    {
        foreach (var row in rows) row.Selected = selected;
    }

    private void SelectCurrentPage(bool selected)
    {
        if (_closed || !_pageReady || !CommitPageEdit()) return;
        SetSelectedOnPage(_rows, selected);
        SavePageState();
        RefreshSelectionCount();
    }

    /// <summary>Перед сменой страницы/поиска запоминаем отметки и количество.</summary>
    private void SavePageState()
    {
        foreach (var row in _rows)
        {
            if (row.Selected) _chosen[row.Id] = row;
            else _chosen.Remove(row.Id);
        }
    }

    private void Accept()
    {
        if (_closed || !_pageReady || !CommitPageEdit()) return;
        SavePageState();
        if (_chosen.Count == 0)
        {
            MessageBox.Show(this, "Отметьте хотя бы одну позицию.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var picked = new List<CatalogPick>();
        foreach (var row in _chosen.Values)
        {
            if (!TryQty(row.Qty, out var qty) || qty <= 0)
            {
                MessageBox.Show(this, $"Количество для «{row.Name}» должно быть больше нуля.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            picked.Add(new CatalogPick(row.Id, row.Name, row.Unit, qty, row.Price, row.PurchasePrice));
        }
        Picked = picked;
        DialogResult = true;
    }

    private static bool TryQty(string text, out decimal qty)
    {
        var normalized = text.Trim().Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out qty);
    }
}
