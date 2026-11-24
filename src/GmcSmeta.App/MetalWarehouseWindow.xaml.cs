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

public partial class MetalWarehouseWindow : Window
{
    private readonly string _connection;
    private SqlServerMetalWarehouse? _store;
    private MetalSnapshot? _snapshot;
    private int _page;
    private bool _busy;
    private int _reloadVersion;
    private int _journalVersion;
    private bool _writeUncertain;
    private bool CanWrite => IsLoaded && !_busy && !_writeUncertain && _snapshot is not null && _store is not null;
    private const int JournalSize = 200;

    public MetalWarehouseWindow(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        _connection = connectionString;
        InitializeComponent();
        Loaded += async (_, _) => await Reload();
        Closing += (_, args) =>
        {
            if (_busy)
            {
                args.Cancel = true;
                StatusText.Text = "Дождитесь завершения операции или загрузки.";
            }
        };
    }

    private async Task Reload()
    {
        if (_busy) return;
        _busy = true;
        var version = ++_reloadVersion;
        ++_journalVersion;
        _snapshot = null; // An unsuccessful read must not leave a writable stale dashboard.
        StatusText.Text = "Обновление склада…";
        try
        {
            var data = await Task.Run(() =>
            {
                var store = _store ?? new SqlServerMetalWarehouse(_connection);
                return (Store: store, Snapshot: store.ReadSnapshot());
            });
            if (version != _reloadVersion) return;
            _store = data.Store;
            _snapshot = data.Snapshot;
            OpeningButton.Visibility = !_snapshot.Initialized && _snapshot.Stock.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;
            ObjectsGrid.ItemsSource = _snapshot.Objects;
            LotsGrid.ItemsSource = _snapshot.Lots;
            FilterStock();
            await LoadJournal();
            StatusText.Text = _writeUncertain
                ? "Запись не подтверждена. Проверьте журнал перед повторным открытием склада."
                : $"Материалов: {_snapshot.Stock.Count} · Объектов с потребностью: {_snapshot.Objects.Count} " +
                  $"· Партии: {_snapshot.Lots.Count} · база только GmcSmetaPilot_*";
        }
        catch (Exception ex)
        {
            _snapshot = null;
            StatusText.Text = "Ошибка чтения склада: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Склад металла", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _busy = false; }
    }

    private void StockFilter_Changed(object sender, RoutedEventArgs e) => FilterStock();

    private void FilterStock()
    {
        if (_snapshot is null || StockCategory is null || StockSearch is null || StockGrid is null)
            return;
        var category = (StockCategory.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var query = StockSearch.Text.Trim().Replace(',', '.');
        var selected = (StockGrid.SelectedItem as MetalStockView)?.MaterialId;
        var rows = _snapshot.Stock.Where(s =>
            (category.Length == 0 || category == s.Category) &&
            (s.Category + " " + s.Material).Replace(',', '.')
                .Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        StockGrid.ItemsSource = rows;
        StockGrid.SelectedItem = rows.FirstOrDefault(x => x.MaterialId == selected) ?? rows.FirstOrDefault();
        var totals = new[]
        {
            rows.Sum(x => x.Ours + x.Supplier),
            rows.Sum(x => x.Ours),
            rows.Sum(x => x.Supplier),
            rows.Sum(x => x.RemainingDemand),
            rows.Sum(x => x.Deficit)
        };
        var fields = new[] { TotalOwned, TotalOurs, TotalSupplier, TotalNeed, TotalDeficit };
        for (var i = 0; i < 5; i++)
            fields[i].Text = MetalWarehouseRules.SqmText(totals[i]) + " м²";
        FillSelectedMaterial();
    }

    private void StockGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => FillSelectedMaterial();

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
        PlaceGrid.ItemsSource = _snapshot.Places.Where(x =>
            x.MaterialId == selected.MaterialId && (x.Area != 0 || x.Pieces != 0)).ToList();
        MaterialObjectsGrid.ItemsSource = _snapshot.Objects.Where(x =>
            x.MaterialId == selected.MaterialId).ToList();
    }

    private async Task LoadJournal()
    {
        if (_store is not { } store) return;
        var page = _page;
        var search = HistorySearch.Text;
        var version = ++_journalVersion;
        try
        {
            var rows = await Task.Run(() => store.ReadJournal(page, JournalSize, search));
            if (!IsLoaded || version != _journalVersion) return;
            JournalGrid.ItemsSource = rows;
            JournalPage.Text = $"Страница {page + 1} · {rows.Count} операций";
        }
        catch (Exception ex)
        {
            if (!IsLoaded || version != _journalVersion) return;
            JournalGrid.ItemsSource = null;
            JournalPage.Text = "Ошибка чтения журнала: " + ex.Message;
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
            var sites = await Task.Run(store.Objects);
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
            var preview = await Task.Run(() => MetalOpeningReader.Read(picker.FileName));
            if (new MetalOpeningPreviewDialog(preview) { Owner = this }.ShowDialog() != true) return;
            if (MessageBox.Show(this, "Перенести начальные остатки и потребности? " +
                "Операцию можно выполнить только один раз.",
                "Начальный импорт", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await Apply(snapshot, () => store.ImportOpening(preview.Records, preview.FileName,
                preview.Original, snapshot.Revision));
        }
        catch (Exception ex)
        {
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
        if (_snapshot is null || _store is null) return;
        var picker = new SaveFileDialog
        {
            Title = "Выгрузить складской отчёт", FileName = "Склад металла.xlsx",
            DefaultExt = ".xlsx", Filter = "Excel (*.xlsx)|*.xlsx"
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var snapshot = _snapshot;
            var store = _store;
            StatusText.Text = "Формирование Excel…";
            await Task.Run(() => MetalWarehouseReport.Export(store, snapshot, picker.FileName));
            StatusText.Text = "Отчёт сохранён: " + picker.FileName;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Экспорт склада", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Reload();
    private async void JournalSearch_Click(object sender, RoutedEventArgs e)
    {
        _page = 0; await LoadJournal();
    }
    private async void JournalPrevious_Click(object sender, RoutedEventArgs e)
    {
        _page = Math.Max(0,_page - 1); await LoadJournal();
    }
    private async void JournalNext_Click(object sender, RoutedEventArgs e)
    {
        if (JournalGrid.Items.Count < JournalSize) return;
        _page++; await LoadJournal();
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
            MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
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
