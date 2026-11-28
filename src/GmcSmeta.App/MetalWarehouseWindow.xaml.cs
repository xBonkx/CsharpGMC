using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

public sealed class MetalAreaConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long n ? MetalWarehouseRules.SqmText(n) : value?.ToString() ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class MetalPaidTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "Оплачено" : "Ожидается";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public partial class MetalWarehouseWindow : Window
{
    private readonly string _connection;
    private SqlServerMetalWarehouse? _store;
    private MetalSnapshot? _snapshot;
    private MetalStockSearch? _stockSearch;
    private int _page;
    private bool _busy;
    private int _reloadVersion;
    private int _journalVersion;
    private CancellationTokenSource? _journalRead;
    private CancellationTokenSource? _snapshotRead;
    private bool _closed;
    private bool _journalLoading, _journalDirty = true, _journalHasNext, _publishingStock;
    private string _journalSearch = "";
    private int _publishedJournalPage;
    private ILookup<long, MetalPlaceView>? _placesByMaterial;
    private ILookup<long, MetalObjectView>? _objectsByMaterial;
    private bool _writeUncertain;
    private bool CanWrite => IsLoaded && !_busy && !_writeUncertain && _snapshot is not null && _store is not null;
    private const int JournalSize = 200;
    private static readonly Brush MetalDeficitBrush = FrozenBrush(0xB4,0x23,0x18);
    private static readonly Brush MetalNormalBrush = FrozenBrush(0x0F,0x17,0x2A);

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r,g,b));
        brush.Freeze();
        return brush;
    }

    public MetalWarehouseWindow(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        _connection = connectionString;
        InitializeComponent();
        Loaded += async (_, _) => await Reload();
        Tabs.SelectionChanged += Tabs_SelectionChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _snapshotRead?.Cancel();
            InvalidateJournal();
        };
        UpdateJournalControls();
        Closing += (_, args) =>
        {
            if (_busy && (_snapshotRead is null || _store is null))
            {
                args.Cancel = true;
                StatusText.Text = "Дождитесь завершения операции или загрузки.";
            }
        };
    }

    private async Task Reload()
    {
        if (_closed || _busy) return;
        _busy = true;
        var version = ++_reloadVersion;
        using var request = new CancellationTokenSource();
        _snapshotRead = request;
        var token = request.Token;
        InvalidateJournal();
        _snapshot = null; // An unsuccessful read must not leave a writable stale dashboard.
        _stockSearch = null;
        _placesByMaterial = null;
        _objectsByMaterial = null;
        _journalDirty = true;
        UpdateJournalControls();
        StatusText.Text = "Обновление склада…";
        try
        {
            // The constructor can initialize schema; do not allow closing until it returns.
            var store = _store ?? await Task.Run(() => new SqlServerMetalWarehouse(_connection));
            if (_closed || token.IsCancellationRequested) return;
            _store = store;
            var data = await Task.Run(() =>
            {
                var snapshot = store.ReadSnapshot(token);
                token.ThrowIfCancellationRequested();
                return (Store: store, Snapshot: snapshot,
                    Places: snapshot.Places.Where(x => x.Area != 0 || x.Pieces != 0).ToLookup(x => x.MaterialId),
                    Objects: snapshot.Objects.ToLookup(x => x.MaterialId),
                    Search: new MetalStockSearch(snapshot.Stock));
            }, token);
            if (_closed || token.IsCancellationRequested || version != _reloadVersion) return;
            _store = data.Store;
            _snapshot = data.Snapshot;
            _placesByMaterial = data.Places;
            _objectsByMaterial = data.Objects;
            _stockSearch = data.Search;
            OpeningButton.Visibility = !_snapshot.Initialized && _snapshot.Stock.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
            ObjectsGrid.ItemsSource = _snapshot.Objects;
            LotsGrid.ItemsSource = _snapshot.Lots;
            FilterStock();
            StatusText.Text = _writeUncertain
                ? "Запись не подтверждена. Проверьте журнал перед повторным открытием склада."
                : $"Материалов: {_snapshot.Stock.Count} · Объектов с потребностью: {_snapshot.Objects.Count} " +
                  $"· Партии: {_snapshot.Lots.Count} · база только GmcSmetaPilot_*";
        }
        catch (Exception ex)
        {
            if (_closed || token.IsCancellationRequested) return;
            _snapshot = null;
            _stockSearch = null;
            _placesByMaterial = null;
            _objectsByMaterial = null;
            StockGrid.ItemsSource = null;
            ObjectsGrid.ItemsSource = null;
            LotsGrid.ItemsSource = null;
            JournalGrid.ItemsSource = null;
            OpeningButton.Visibility = Visibility.Collapsed;
            foreach (var field in new[] { TotalOwned, TotalOurs, TotalSupplier, TotalNeed, TotalDeficit })
                field.Text = "—";
            TotalDeficit.Foreground = MetalNormalBrush;
            FillSelectedMaterial();
            StatusText.Text = "Ошибка чтения склада: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Склад металла", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_snapshotRead, request)) _snapshotRead = null;
            _busy = false;
            if (!_closed)
            {
                UpdateJournalControls();
                if (_snapshot is not null && JournalVisible) _ = LoadJournal();
            }
        }
    }

    private void StockFilter_Changed(object sender, RoutedEventArgs e) => FilterStock();

    private void FilterStock()
    {
        if (_snapshot is null || _stockSearch is not { } search || StockCategory is null || StockSearch is null || StockGrid is null)
            return;
        var category = (StockCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var selected = (StockGrid.SelectedItem as MetalStockView)?.MaterialId;
        var filtered = search.Filter(category, StockSearch.Text);
        var rows = filtered.Rows;
        _publishingStock = true;
        try
        {
            StockGrid.ItemsSource = rows;
            StockGrid.SelectedItem = rows.FirstOrDefault(x => x.MaterialId == selected) ?? rows.FirstOrDefault();
        }
        finally { _publishingStock = false; }
        var totals = new[] { filtered.Owned, filtered.Ours, filtered.Supplier, filtered.Need, filtered.Deficit };
        var fields = new[] { TotalOwned, TotalOurs, TotalSupplier, TotalNeed, TotalDeficit };
        for (var i = 0; i < 5; i++)
            fields[i].Text = MetalWarehouseRules.SqmText(totals[i]) + " м²";
        TotalDeficit.Foreground = totals[4] > 0 ? MetalDeficitBrush : MetalNormalBrush;
        FillSelectedMaterial();
    }

    private void StockGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_publishingStock) FillSelectedMaterial();
    }

    private void FillSelectedMaterial()
    {
        if (PlaceGrid is null || MaterialObjectsGrid is null || DetailTitle is null)
            return;
        if (_snapshot is null || StockGrid.SelectedItem is not MetalStockView selected)
        {
            DetailTitle.Text = "Выберите материал";
            PlaceGrid.ItemsSource = null;
            MaterialObjectsGrid.ItemsSource = null;
            return;
        }
        DetailTitle.Text = selected.Category + " · " + selected.Material;
        PlaceGrid.ItemsSource = _placesByMaterial?[selected.MaterialId];
        MaterialObjectsGrid.ItemsSource = _objectsByMaterial?[selected.MaterialId];
    }

    private bool JournalVisible => ReferenceEquals(Tabs.SelectedItem, JournalTab);

    private void InvalidateJournal()
    {
        ++_journalVersion;
        _journalDirty = true;
        _journalRead?.Cancel();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, Tabs) || _closed) return;
        if (!JournalVisible)
        {
            if (_journalLoading) InvalidateJournal();
            return;
        }
        if (_journalDirty) _ = LoadJournal();
    }

    private void UpdateJournalControls()
    {
        var ready = !_busy && !_journalLoading && _snapshot is not null;
        JournalSearchButton.IsEnabled = ready;
        JournalPreviousButton.IsEnabled = ready && !_journalDirty && _publishedJournalPage > 0;
        JournalNextButton.IsEnabled = ready && !_journalDirty && _journalHasNext;
    }

    private async Task LoadJournal()
    {
        if (_closed || !IsLoaded || !JournalVisible || _busy || _journalLoading || !_journalDirty ||
            _snapshot is null || _store is not { } store) return;
        _journalLoading = true;
        _journalDirty = false;
        UpdateJournalControls();
        var page = _page;
        var search = _journalSearch;
        var version = ++_journalVersion;
        var snapshot = _snapshot;
        using var request = new CancellationTokenSource();
        _journalRead = request;
        var token = request.Token;
        JournalPage.Text = "Загрузка журнала…";
        try
        {
            var rows = await Task.Run(() => store.ReadJournalPage(page, JournalSize, search, cancellationToken: token), token);
            if (_closed || !IsLoaded || !JournalVisible || token.IsCancellationRequested ||
                version != _journalVersion || !ReferenceEquals(snapshot, _snapshot)) return;
            JournalGrid.ItemsSource = rows.Rows;
            _publishedJournalPage = page;
            _journalHasNext = rows.HasNext;
            JournalPage.Text = $"Страница {page + 1} · {rows.Rows.Count} операций";
        }
        catch (Exception ex)
        {
            if (_closed || !IsLoaded || !JournalVisible || token.IsCancellationRequested ||
                version != _journalVersion || !ReferenceEquals(snapshot, _snapshot)) return;
            JournalGrid.ItemsSource = null;
            _journalHasNext = false;
            _journalDirty = true;
            JournalPage.Text = "Ошибка чтения журнала: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_journalRead, request)) _journalRead = null;
            _journalLoading = false;
            if (!_closed) UpdateJournalControls();
            // A reload invalidated this page while its query was running.
            // Start the replacement only when the new snapshot is ready.
            if (!_closed && version != _journalVersion && _journalDirty && JournalVisible)
                _ = LoadJournal();
        }
    }

    private async Task Apply(MetalSnapshot snapshot, Action action)
    {
        if (!CanWrite || !ReferenceEquals(snapshot, _snapshot))
        {
            StatusText.Text = "Склад изменился или запись не подтверждена. Проверьте данные.";
            return;
        }
        _busy = true;
        InvalidateJournal();
        UpdateJournalControls();
        StatusText.Text = "Складская запись…";
        try
        {
            await Task.Run(action);
            StatusText.Text = "Операция проведена.";
        }
        catch (DBConcurrencyException ex)
        {
            StatusText.Text = "Конфликт: данные изменились другим сотрудником.";
            MessageBox.Show(this, ex.Message + "\nОбновите склад и повторите действие.",
                "Конфликт изменений", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _writeUncertain = true;
            StatusText.Text = "Запись не подтверждена. Проверьте журнал перед повторением.";
            MessageBox.Show(this, ex.Message +
                "\nЕсли соединение оборвалось, проверьте журнал до повторной операции.",
                "Склад металла", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; }
        await Reload();
    }

    private async Task RunOperation(string preset)
    {
        if (!CanWrite || _snapshot is not { } snapshot || _store is not { } store) return;
        var selected = StockGrid.SelectedItem as MetalStockView;
        try
        {
            IReadOnlyList<(long Id, string Name)> sites;
            _busy = true;
            UpdateJournalControls();
            try { sites = await Task.Run(store.Objects); }
            finally { EndReadBusy(); }
            if (!CanWrite || !ReferenceEquals(snapshot, _snapshot)) return;
            var dialog = new MetalOperationDialog(snapshot.Stock, sites, selected, preset) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is not { } input) return;
            await Apply(snapshot, () => store.Post(input, snapshot.Revision));
        }
        catch (Exception ex)
        {
            if (IsLoaded) MessageBox.Show(this, ex.Message, "Склад металла", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Operation_Click(object sender, RoutedEventArgs e) => await RunOperation("");
    private async void QuickOperation_Click(object sender, RoutedEventArgs e) =>
        await RunOperation((sender as Button)?.Tag?.ToString() ?? "");

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!CanWrite || _snapshot is not { } snapshot || _store is not { } store) return;
        var picker = new OpenFileDialog { Title = "Сверить исходные остатки", Filter = "Excel (*.xlsx)|*.xlsx" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            _busy = true;
            UpdateJournalControls();
            var preview = await Task.Run(() => MetalOpeningReader.Read(picker.FileName));
            EndReadBusy();
            if (!CanWrite || !ReferenceEquals(snapshot, _snapshot)) return;
            if (new MetalOpeningPreviewDialog(preview) { Owner = this }.ShowDialog() != true) return;
            if (MessageBox.Show(this, "Перенести начальные остатки и потребности? " +
                "Операцию можно выполнить только один раз.",
                "Начальный импорт", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            await Apply(snapshot, () => store.ImportOpening(preview.Records, preview.FileName,
                preview.Original, snapshot.Revision));
        }
        catch (Exception ex)
        {
            EndReadBusy();
            MessageBox.Show(this, ex.Message, "Первоначальный импорт", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        if (!CanWrite) return;
        new MetalRegisterWindow(_connection) { Owner = this }.ShowDialog();
        _ = Reload();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _snapshot is not { } snapshot || _store is not { } store) return;
        var picker = new SaveFileDialog
        {
            Title = "Выгрузить складской отчёт", FileName = "Склад металла.xlsx",
            DefaultExt = ".xlsx", Filter = "Excel (*.xlsx)|*.xlsx"
        };
        if (picker.ShowDialog(this) != true) return;
        if (!ReferenceEquals(snapshot, _snapshot) || _busy) return;
        var path = picker.FileName;
        _busy = true;
        UpdateJournalControls();
        try
        {
            StatusText.Text = "Формирование Excel…";
            await Task.Run(() => MetalWarehouseReport.Export(store, snapshot, path));
            StatusText.Text = "Отчёт сохранён: " + picker.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Экспорт склада", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { EndReadBusy(); }
    }

    private void EndReadBusy()
    {
        _busy = false;
        UpdateJournalControls();
        if (_snapshot is not null && JournalVisible && _journalDirty) _ = LoadJournal();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Reload();
    private async void JournalSearch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _journalLoading) return;
        _page = 0;
        _journalSearch = HistorySearch.Text;
        _journalDirty = true;
        await LoadJournal();
    }
    private async void JournalPrevious_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _journalLoading || _journalDirty || _publishedJournalPage == 0) return;
        _page = _publishedJournalPage - 1;
        _journalDirty = true;
        await LoadJournal();
    }
    private async void JournalNext_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _journalLoading || _journalDirty || !_journalHasNext) return;
        _page = checked(_publishedJournalPage + 1);
        _journalDirty = true;
        await LoadJournal();
    }

    private async void LotAdd_Click(object sender, RoutedEventArgs e)
    {
        if (!CanWrite || _snapshot is not { } snapshot || _store is not { } store) return;
        var dialog = new MetalLotDialog(snapshot.Stock) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } lot) return;
        await Apply(snapshot, () => store.AddLot("Бухты", lot.Material, lot.Supplier,
            lot.Invoice, lot.Area, lot.Pieces, snapshot.Revision));
    }

    private async void LotPay_Click(object sender, RoutedEventArgs e)
    {
        if (!CanWrite || _snapshot is not { } snapshot || _store is not { } store ||
            LotsGrid.SelectedItem is not MetalLotView lot) return;
        if (MessageBox.Show(this, "Подтвердить оплату всей партии?", "Оплата",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Apply(snapshot, () => store.UpdateLot(lot.Id, snapshot.Revision));
    }

    private async void LotReceive_Click(object sender, RoutedEventArgs e)
    {
        if (!CanWrite || _snapshot is not { } snapshot || _store is not { } store ||
            LotsGrid.SelectedItem is not MetalLotView lot) return;
        var dialog = new MetalReceiveDialog(lot) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } received) return;
        await Apply(snapshot, () => store.UpdateLot(lot.Id, snapshot.Revision,
            received.Area, received.Pieces, received.Place));
    }
}
