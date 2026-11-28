using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class CostSummaryPane
{
    private CostSummaryData? _summaryData;
    private readonly CostSummaryGroupingCache _materialGroups = new();
    private MaterialOptions? _lastMaterialOptions;
    private object? _lastMaterialSource;
    private CultureInfo? _lastMaterialCulture;
    private System.Windows.Threading.DispatcherTimer? _materialSearchDebounce;
    private bool _materialSearchDirty;
    private bool MaterialSearchPending => _materialSearchDirty;

    private void ResetMaterialProjection()
    {
        _materialSearchDebounce?.Stop();
        _materialGroups.Clear();
        _lastMaterialOptions = null;
        _lastMaterialSource = null;
        _lastMaterialCulture = null;
    }

    private static string ViewTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private void MaterialsView_Changed(object sender, RoutedEventArgs e) => ReloadMaterialView();

    private void MaterialsSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_attached || _summaryData is null) return;
        _materialSearchDirty = true;
        if (CostTabs.SelectedIndex != 0) return;
        if (_materialSearchDebounce is null)
        {
            _materialSearchDebounce = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _materialSearchDebounce.Tick += (_, _) =>
            {
                if (!_attached || CostTabs.SelectedIndex != 0) { _materialSearchDebounce.Stop(); return; }
                if (_operationActive || _loadingCosts || MaterialsGrid.Items is
                    System.ComponentModel.IEditableCollectionView { IsEditingItem: true }) return;
                ReloadMaterialView();
            };
        }
        _materialSearchDebounce.Stop();
        _materialSearchDebounce.Start();
    }

    /// <summary>
    /// Empty systems, supplier sections, equal-material groups and Python ordering.
    /// Header rows are not written to SQL: their MemberIds point to original positions.
    /// </summary>
    private void ReloadMaterialView()
    {
        _materialSearchDebounce?.Stop();
        if (_summaryData is null || MaterialsGrid is null ||
            CostViewMode is null || CostSortMode is null || !_attached || CostTabs.SelectedIndex != 0)
            return;
        _materialSearchDirty = false;
        var options = CaptureMaterialOptions();
        if (_lastMaterialOptions == options && Equals(_lastMaterialCulture, CultureInfo.CurrentCulture) &&
            _lastMaterialSource is not null && ReferenceEquals(MaterialsGrid.ItemsSource, _lastMaterialSource)) return;
        using var operationTiming = StartupTrace.BeginOperation();
        using var planTiming = StartupTrace.Measure("cost_material_filter_group_plan");
        var groups = _materialGroups.Get(_summaryData, options.Mode, options.Sort);
        planTiming?.Dispose();
        using var filterTiming = StartupTrace.Measure("cost_material_filter_projection");
        var result = BuildMaterialRows(_summaryData, _allRows, options, CancellationToken.None, groups);
        filterTiming?.Dispose();
        using var bindTiming = StartupTrace.Measure("cost_material_filter_bind");
        ReplaceCostRows(MaterialsGrid, result);
        _lastMaterialOptions = options;
        _lastMaterialSource = result;
        _lastMaterialCulture = CultureInfo.CurrentCulture;
    }

    private sealed record MaterialOptions(string Mode, string Sort, string Search, bool NoCost, bool NoPrice);

    private MaterialOptions CaptureMaterialOptions() => new(
        ViewTag(CostViewMode, "kp"), ViewTag(CostSortMode, "kp"),
        MaterialSearch?.Text?.Trim() ?? "", MaterialMissingCost?.IsChecked == true,
        MaterialMissingPrice?.IsChecked == true);

    // Plain DTOs only: may run on a worker for a fresh SQL snapshot.
    private static List<CostSummaryPreviewRow> BuildMaterialRows(CostSummaryData data,
        IEnumerable<CostSummaryPreviewRow> allRows, MaterialOptions options, CancellationToken token,
        IReadOnlyList<CostSummaryGrouping.Group>? preparedGroups = null)
    {
        token.ThrowIfCancellationRequested();
        var groups = preparedGroups ?? CostSummaryGrouping.Build(data, options.Mode, options.Sort);
        token.ThrowIfCancellationRequested();
        var all = allRows.Where(x => x.Id > 0).ToDictionary(x => x.Id);
        var result = new List<CostSummaryPreviewRow>();
        var (search, noCost, noPrice) = (options.Search, options.NoCost, options.NoPrice);
        var anyFilter = noCost || noPrice || search.Length != 0;
        string MoneyValue(decimal value) => value == 0m ? "—" : Money.Format(value);
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            var members = group.Rows
                .Where(r => (!noCost && !noPrice) ||
                    (noCost && r.NoCost) || (noPrice && r.NoPrice))
                .Where(r => search.Length == 0 ||
                    r.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    r.Supplier.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                    group.Label.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            if (members.Count == 0 && anyFilter)
                continue;
            if (members.Count == 0 && !group.IsSystem)
                continue;
            var sumCost = members.Sum(r => r.MCost);
            var sumSell = members.Sum(r => r.MSell);
            result.Add(new CostSummaryPreviewRow
            {
                Id = -100,
                IsGroup = true,
                MemberIds = members.Select(r => r.Id).ToArray(),
                Name = group.Label + (group.IsEmpty ? "  (пустая система)" :
                    "  ·  " + members.Count.ToString(CultureInfo.CurrentCulture) + " поз."),
                MCost = MoneyValue(sumCost),
                MSell = MoneyValue(sumSell),
                MatMargin = MoneyValue(sumSell - sumCost),
                RowBackground = "#EAF2FD",
            });
            foreach (var row in members)
            {
                token.ThrowIfCancellationRequested();
                if (all.TryGetValue(row.Id, out var preview)) result.Add(preview);
            }
        }
        if (!anyFilter)
            foreach (var extra in allRows.Where(r => r.IsTransport))
                result.Add(extra);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private IReadOnlyList<long> SelectedMaterials()
    {
        var selected = MaterialsGrid.SelectedItems.OfType<CostSummaryPreviewRow>()
            .SelectMany(x => x.MemberIds).Where(id => id > 0).Distinct().ToArray();
        if (selected.Length > 0) return selected;
        return Array.Empty<long>();
    }

    private async void MaterialsMarkup_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var ids = SelectedMaterials();
        if (ids.Count == 0)
        {
            SetStatus("Выделите позиции или заголовок системы для наценки.");
            return;
        }
        var d = new PromptDialog("Массовая наценка материалов",
            $"Выбрано {ids.Count} строк. Укажите коэффициент пересчёта (например 1,44):",
            "1,44") { Owner = Window.GetWindow(this) };
        if (d.ShowDialog() != true) return;
        if (!decimal.TryParse(d.Value.Replace(',', '.'), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var factor) || factor <= 0m)
        {
            SetStatus("Неверный коэффициент.");
            return;
        }
        var choice = MessageBox.Show(Window.GetWindow(this),
            "Да — рассчитать розницу из закупа.\nНет — рассчитать закуп из розницы.\n" +
            "Отмена — ничего не менять.",
            "Направление пересчёта", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;
        var fromCost = choice == MessageBoxResult.Yes;
        var onlyEmptyAnswer = MessageBox.Show(Window.GetWindow(this),
            "Применить только к позициям с незаполненной целевой ценой?\n" +
            "Да — только пустые; нет — все выбранные.",
            "Только пустые", MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question, MessageBoxResult.No);
        if (onlyEmptyAnswer == MessageBoxResult.Cancel) return;
        var onlyEmpty = onlyEmptyAnswer == MessageBoxResult.Yes;
        // Python cost_view.MarkupDialog defaults to whole rubles for retail.
        // Purchase price calculation remains kopeck-precise in the SQL API.
        var wholeRubles = false;
        if (fromCost)
        {
            var wholeAnswer = MessageBox.Show(Window.GetWindow(this),
                "Округлять розничную цену до целых рублей?\n" +
                "Да — как по умолчанию в Python; нет — до копеек.",
                "Округление цены", MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question, MessageBoxResult.Yes);
            if (wholeAnswer == MessageBoxResult.Cancel) return;
            wholeRubles = wholeAnswer == MessageBoxResult.Yes;
        }
        try
        {
            SetStatus("Массовая переоценка материалов…");
            var result = await Task.Run(() => SqlServerCostOperations.MarkupMaterials(
                _connectionString, _contractId, ids, factor, fromCost,
                onlyEmpty: onlyEmpty, wholeRubles: wholeRubles, ServerRevision()));
            _revision = result.Revision;
            await ReloadAsync();
            SetStatus($"Изменено {result.Changed}, пропущено {result.Skipped} строк. Цены обновлены.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка переоценки: " + ex.Message);
            await ReloadAsync();
        }
    }

    private async void CatalogFill_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        if (MessageBox.Show(Window.GetWindow(this),
            "Заполнить пустые закупочные цены последними ценами из справочника?\n" +
            "Цены с фактом по счёту не изменятся.",
            "Закуп из справочника", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            var result = await Task.Run(() => SqlServerCostOperations.FillPurchaseFromCatalog(
                _connectionString, _contractId, ServerRevision()));
            _revision = result.Revision;
            await ReloadAsync();
            SetStatus($"Закуп из справочника: заполнено {result.Changed} позиций.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка справочника: " + ex.Message);
            await ReloadAsync();
        }
    }

    private async void MaterialColor_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var ids = SelectedMaterials();
        if (ids.Count == 0)
        {
            SetStatus("Для цвета выделите строки или всю систему.");
            return;
        }
        var d = new PromptDialog("Цвет строк",
            "Введите цвет #RRGGBB (например #FFF3B0) или оставьте пустым для снятия заливки:",
            "#FFF3B0") { Owner = Window.GetWindow(this) };
        if (d.ShowDialog() != true) return;
        try
        {
            var color = d.Value.Trim();
            var result = await Task.Run(() => SqlServerCostOperations.SetCostFormatting(
                _connectionString, _contractId, ids, "cost_color", color, ServerRevision()));
            _revision = result.Revision;
            await ReloadAsync();
            SetStatus($"Цвет обновлён: {result.Changed} строк.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка цвета: " + ex.Message);
        }
    }

    private async void Transport_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        try
        {
            var current = await Task.Run(() => SqlServerCostOperations.ReadTransport(
                _connectionString, _contractId));
            var dialog = new CostTransportDialog(current, await Task.Run(() =>
                SqlServerCostOperations.SupplierCosts(_connectionString, _contractId)), _totalsData?.Locked == true)
            {
                Owner = Window.GetWindow(this)
            };
            if (dialog.ShowDialog() != true) return;
            var cost = dialog.OurCost;
            var amount = dialog.CustomerAmount;
            var note = dialog.CalculationNote;
            var result = await Task.Run(() => SqlServerCostOperations.SaveTransport(
                _connectionString, _contractId, cost, amount, note, ServerRevision()));
            if (!result.Saved)
            {
                SetStatus("Транспорт: " + result.Error);
                return;
            }
            _revision = result.Revision;
            await ReloadAsync();
            SetStatus("Транспорт сохранён. Розница и закуп учитываются по одной строке.");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка транспорта: " + ex.Message);
            await ReloadAsync();
        }
    }
}
