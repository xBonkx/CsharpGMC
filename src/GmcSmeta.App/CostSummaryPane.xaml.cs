using System.Threading;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Строка сводной таблицы себестоимости (cost_summary_view, вариант «Как в КП»).</summary>
public sealed class CostSummaryPreviewRow
{
    public long Id { get; init; }
    public bool IsTransport { get; init; }
    public bool IsWork { get; init; }
    public bool IsGroup { get; init; }
    public IReadOnlyList<long> MemberIds { get; init; } = Array.Empty<long>();
    public string MatMarkupPct { get; init; } = "";
    public string CostNote { get; init; } = "";
    public string UCost { get; set; } = "";
    public string UPrice { get; set; } = "";
    public string OriginalWorkUnitPrice { get; init; } = "";
    // Keep the shared Materials row unchanged; Summary displays the original KP price.
    public string SummaryUnitPrice
    {
        get => IsWork ? OriginalWorkUnitPrice : UPrice;
        set { if (!IsWork) UPrice = value; }
    }
    public string UWcost { get; set; } = "";
    public string UWsell { get; set; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Qty { get; set; } = "";
    public string Supplier { get; set; } = "";
    public string MCost { get; init; } = "";
    public string MSell { get; init; } = "";
    public string MatMargin { get; init; } = "";
    public string WCost { get; init; } = "";
    public string WSell { get; init; } = "";
    public string WorkMargin { get; init; } = "";
    public string TotalCost { get; init; } = "";
    public string TotalSell { get; init; } = "";
    public string TotalMargin { get; init; } = "";
    public string MarkupPct { get; init; } = "";
    public string RowBackground { get; init; } = "White";
}

/// <summary>
/// «Себестоимость → Сводная» (cost_summary_view, режим «Как в КП»): по каждой верхней
/// строке — закуп материалов (факт/план, закуп комплектующих сложен в строку), розница,
/// цены работ и маржа; вне КП и прочие затраты — отдельными строками. Read-only:
/// правка закупов и поставщиков — следующий блок (save_cell).
/// </summary>
public partial class CostSummaryPane : UserControl
{
    private string _connectionString = "";
    private int _contractId;
    private readonly bool _readOnly;
    private bool _loadingCosts;
    private CostLoadSnapshot? _cachedCostSnapshot;
    private int _publishedCostTab = -1;
    private bool _restoringCostTab;
    private bool _operationActive;
    private volatile bool _writeAttempted;
    private string _operationRevision = "";
    private string? _loadError;
    private long _loadGeneration;
    private CancellationTokenSource? _costReadCancellation;
    private volatile bool _attached;
    private long _attachmentGeneration;
    private long _operationAttachment;
    private bool _needsReloadAfterAttach = true;
    private bool CanUseSnapshot => _attached && !_loadingCosts && !_operationActive && _revision.Length > 0 &&
        _publishedCostTab == CostTabs.SelectedIndex && (CostTabs.SelectedIndex != 0 || !MaterialSearchPending);
    private ObservableCollection<CostSummaryPreviewRow> _allRows = new();
    private List<CostSummaryRow> _summaryRows = new();
    private CostMissingPriceIndex _missingPrices = new(Array.Empty<CostSummaryRow>());
    private readonly Dictionary<CostMissingPrice, ObservableCollection<CostSummaryPreviewRow>> _filteredRows = new();

    private static string? Field(DataGridColumn column) => column.Header?.ToString() switch
    {
        "Поставщик" => "supplier",
        "Кол-во" => "qty",
        "Мат: закуп за ед." => "purchase_price",
        "Мат: розн. за ед." => "price",
        "Работы: закуп за ед." => "work_cost",
        "Работы: розн. за ед." => "work_sell",
        _ => null,
    };

    private void SummaryGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_readOnly || !CanUseSnapshot) { e.Cancel = true; return; }
        if (e.Row.Item is not CostSummaryPreviewRow row) { e.Cancel = true; return; }
        var field = Field(e.Column);
        if (field is null || row.IsGroup) { e.Cancel = true; return; }
        if (row.IsTransport)
        {
            if (field != "purchase_price") e.Cancel = true;   // у транспорта правится только наш расход
            return;
        }
        if (row.Id <= 0 && field == "supplier") { e.Cancel = true; return; }
        if (row.Id <= 0) { e.Cancel = true; return; }
        if (field == "price" && row.IsWork)
        {
            MessageBox.Show(Window.GetWindow(this), "Это строки работ — их цену клиенту правят во вкладке «Смета».",
                "Сводная", MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
        }
    }

    private async void SummaryGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (_readOnly || !CanUseSnapshot || e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not CostSummaryPreviewRow row) return;
        var field = Field(e.Column);
        if (field is null || e.EditingElement is not TextBox editor) return;
        var input = editor.Text.Trim();
        var positionId = row.IsTransport ? 0L : row.Id;
        var contractField = row.IsTransport && field == "purchase_price" ? "transport_cost" : field;
        var baseline = _cachedCostSnapshot is { } confirmed && confirmed.Revision == _revision &&
            ReferenceEquals(confirmed.Summary, _summaryData) ? confirmed : null;
        using var operation = BeginCostOperation();
        if (operation is null) return;
        SetStatus("Сохранение…");
        try
        {
            string committed;
            if (field == "supplier")
                committed = await Task.Run(() => SqlServerCostSummary.SetSupplierWithRevision(
                    _connectionString, _contractId, positionId, input, ServerRevision()));
            else
            {
                var result = await Task.Run(() => SqlServerCostSummary.SaveSummaryCell(
                    _connectionString, _contractId, positionId, contractField, input, ServerRevision()));
                if (!result.Saved)
                {
                    SetStatus("Ошибка: " + result.Error);
                    return;
                }
                committed = result.Revision;
            }
            _revision = committed;
            SetStatus(field == "supplier" ? "Поставщик сохранён" : row.IsTransport
                ? "Транспорт сохранён, итоги пересчитаны"
                : "Сохранено. Вернуть — Ctrl+Z в смете");
            var delta = baseline is not null && positionId > 0 &&
                SqlServerCostSummary.SupportsCostRowDelta(contractField)
                ? new CostDeltaRequest(baseline, positionId, contractField, committed) : null;
            // A rejected narrow read falls back to the full confirmed snapshot.
            await ReloadAsync(delta: delta);
            return;
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
        await ReloadAsync();
    }

    private void SetStatus(string message) => FooterText.Text = _loadError is null
        ? message : message + " · " + _loadError;

    public CostSummaryPane(string connectionString, int contractId, string title, bool readOnly = false)
    {
        _readOnly = readOnly;
        InitializeComponent();
        MarkupButton.IsEnabled = !readOnly;
        CaptionText.Text = $"Сводная: {title}" + (readOnly ? " · только просмотр" : "");
        SummaryGrid.IsReadOnly = readOnly;
        MaterialsGrid.IsReadOnly = readOnly;
        SmrWorkGrid.IsReadOnly = readOnly;
        SmrExpenseGrid.IsReadOnly = readOnly;
        TotalsGrid.IsReadOnly = readOnly;
        _connectionString = connectionString;
        _contractId = contractId;
        CostTabs.SelectionChanged += CostTabs_SelectionChanged;
        Loaded += (_, _) =>
        {
            _attached = true;
            if (_needsReloadAfterAttach && !_operationActive) _ = ReloadAsync();
        };
        Unloaded += (_, _) =>
        {
            _attached = false;
            Interlocked.Increment(ref _attachmentGeneration);
            ++_loadGeneration;
            _costReadCancellation?.Cancel();
            _revision = "";
            _loadingCosts = false;
            _needsReloadAfterAttach = true;
            _filteredRows.Clear();
            _cachedCostSnapshot = null;
            _publishedCostTab = -1;
            ResetMaterialProjection();
        };
    }

    private void CostTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, CostTabs) || !_attached || _restoringCostTab) return;
        if (_loadingCosts)
        {
            _materialSearchDebounce?.Stop();
            return; // the worker checks the selected tab again before publishing
        }
        if (_operationActive)
        {
            _materialSearchDebounce?.Stop();
            _needsReloadAfterAttach = true; // mandatory readback or operation disposal loads the selected tab
            return;
        }
        if (_publishedCostTab >= 0 && new[] { SummaryGrid, MaterialsGrid, SmrWorkGrid, SmrExpenseGrid, TotalsGrid }
            .Any(grid => grid.Items is System.ComponentModel.IEditableCollectionView { IsEditingItem: true }))
        {
            _restoringCostTab = true;
            try { CostTabs.SelectedIndex = _publishedCostTab; }
            finally { _restoringCostTab = false; }
            SetStatus("Завершите редактирование ячейки перед сменой вкладки.");
            return;
        }
        _materialSearchDebounce?.Stop(); // retain dirty search for the next activation
        _ = ReloadAsync(allowReuse: true);
    }

    public void RefreshFromSqlServer()
    {
        if (_attached && !_operationActive && !_loadingCosts) _ = ReloadAsync();
    }

    private void RetryCost_Click(object sender, RoutedEventArgs e) => RefreshFromSqlServer();

    private string _revision = "";

    // A write must use the revision of the displayed snapshot, never a fresh
    // revision fetched from SQL on behalf of stale rows.
    private string ServerRevision()
    {
        if (!_attached || _operationAttachment != Interlocked.Read(ref _attachmentGeneration) ||
            !_operationActive || string.IsNullOrEmpty(_operationRevision))
            throw new InvalidOperationException("Сначала обновите себестоимость.");
        _writeAttempted = true;
        return _operationRevision;
    }

    private CostOperation? BeginCostOperation()
    {
        if (_readOnly || !CanUseSnapshot) return null;
        _operationActive = true;
        _cachedCostSnapshot = null; // mutable preview rows must never survive a write attempt as a clean cache
        _operationRevision = _revision;
        _operationAttachment = Interlocked.Read(ref _attachmentGeneration);
        _writeAttempted = false;
        return new CostOperation(this, _loadGeneration);
    }

    private sealed class CostOperation(CostSummaryPane owner, long generation) : IDisposable
    {
        public void Dispose()
        {
            owner._operationActive = false;
            owner._operationRevision = "";
            if (owner._attached && (owner._needsReloadAfterAttach ||
                owner._writeAttempted && generation == owner._loadGeneration))
                _ = owner.ReloadAsync(); // includes failed/unknown COMMIT; never replay the write
        }
    }

    private static string Num(decimal? v) => v is null ? "" : v.Value.ToString("0.######", CultureInfo.CurrentCulture);

    /// <summary>Фильтр «Без закупочных цен» (cost_summary_view.MISSING): строки с выбранными
    /// пропущенными ценами; в кнопке — счётчики каждого вида.</summary>
    private void ApplyFilter()
    {
        if (FilterNoPrice is null || FilterNoCost is null || FilterNowSell is null ||
            FilterNowCost is null || FilterCountText is null || SummaryGrid is null) return;
        if (_attached && CostTabs.SelectedIndex != 2) return;
        using var timing = StartupTrace.Measure("cost_summary_filter_bind");
        var selected = (FilterNoPrice.IsChecked == true ? CostMissingPrice.MaterialPrice : CostMissingPrice.None)
            | (FilterNoCost.IsChecked == true ? CostMissingPrice.MaterialCost : CostMissingPrice.None)
            | (FilterNowSell.IsChecked == true ? CostMissingPrice.WorkSell : CostMissingPrice.None)
            | (FilterNowCost.IsChecked == true ? CostMissingPrice.WorkCost : CostMissingPrice.None);
        FilterCountText.Text = selected == CostMissingPrice.None ? "" :
            $"Без цен: {_missingPrices.Count(selected)}";

        // At most 15 non-empty combinations per snapshot; reuse preview row objects.
        ObservableCollection<CostSummaryPreviewRow> visible;
        if (selected == CostMissingPrice.None)
            visible = _allRows;
        else if (_filteredRows.TryGetValue(selected, out var cached))
            visible = cached;
        else
        {
            visible = new ObservableCollection<CostSummaryPreviewRow>(
                _allRows.Where(row => _missingPrices.Includes(row.Id, selected)));
            _filteredRows.Add(selected, visible);
        }
        if (!ReferenceEquals(SummaryGrid.ItemsSource, visible))
            ReplaceCostRows(SummaryGrid, visible);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private sealed record CostLoadSnapshot(CostSummaryData Summary, CostTotalsData? Totals,
        List<CostSummaryPreviewRow> Rows, List<SmrExpensePreviewRow>? Expenses, string SmrMode, string Revision,
        CostMissingPriceIndex MissingPrices, List<CostSummaryPreviewRow>? Materials,
        List<CostSummaryPreviewRow>? WorkRows, MaterialOptions MaterialOptions, string Culture);

    private sealed record CostDeltaRequest(CostLoadSnapshot Baseline, long PositionId, string Field, string Revision);

    private static CostLoadSnapshot ReadCostSnapshot(string connectionString, int contractId,
        MaterialOptions materialOptions, int activeTab, CostLoadSnapshot? cached, CostDeltaRequest? delta,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var conn = SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult();
        string Revision() => SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var before = Revision();
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(before)) throw new InvalidOperationException("SQL Server не подтвердил ревизию.");
            var same = cached is not null && cached.Revision == before &&
                cached.Culture == CultureInfo.CurrentCulture.Name ? cached : null;
            CostSummaryData summary;
            CostTotalsData? totals = same?.Totals;
            using (StartupTrace.Measure("cost_snapshot_financial"))
            {
                var patched = delta is not null && delta.Revision == before &&
                    delta.Baseline.Culture == CultureInfo.CurrentCulture.Name
                    ? SqlServerCostSummary.ReadChangedCostRow(conn, contractId, delta.Baseline.Summary,
                        delta.PositionId, delta.Field, delta.Revision, token) : null;
                if (patched is not null)
                {
                    summary = patched;
                    StartupTrace.Mark("cost_targeted_row_readback_hit");
                }
                else if (same is not null) summary = same.Summary;
                else if (activeTab == 3)
                    (summary, totals) = SqlServerCostTotals.SummaryAndTotals(conn, contractId, token);
                else summary = SqlServerCostSummary.Summary(conn, contractId, token);
                if (activeTab == 3 && totals is null)
                    totals = SqlServerCostTotals.Totals(conn, contractId, summary, token);
            }
            token.ThrowIfCancellationRequested();
            var expenses = same?.Expenses;
            var mode = same?.SmrMode ?? "sum";
            if (activeTab == 1 && expenses is null)
            {
                using var expenseTiming = StartupTrace.Measure("cost_snapshot_expenses");
                expenses = SqlServerSmrExpenses.List(conn, contractId, token).Select(x =>
                {
                    token.ThrowIfCancellationRequested();
                    return new SmrExpensePreviewRow
                    {
                        Id = x.Id, Date = x.Date?.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture) ?? "",
                        Kind = x.Kind, Who = x.Who,
                        Amount = x.Amount.ToString("0.##", CultureInfo.CurrentCulture), Note = x.Note,
                    };
                }).ToList();
                using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT smr_mode FROM dbo.contracts WHERE id=@id", conn);
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
                mode = Convert.ToString(cmd.ExecuteScalarAsync(token).GetAwaiter().GetResult(), CultureInfo.InvariantCulture) ?? "sum";
            }
            token.ThrowIfCancellationRequested();
            var after = Revision();
            token.ThrowIfCancellationRequested();
            if (before == after)
            {
                List<CostSummaryPreviewRow> rows;
                using (StartupTrace.Measure("cost_summary_format"))
                    rows = same?.Rows ?? BuildSummaryPreview(summary, token);
                CostMissingPriceIndex missing;
                using (StartupTrace.Measure("cost_missing_index"))
                    missing = same?.MissingPrices ?? new CostMissingPriceIndex(summary.Rows, token);
                var workRows = same?.WorkRows;
                if (activeTab == 1 && workRows is null)
                    using (StartupTrace.Measure("cost_smr_work_projection"))
                        workRows = rows.Where(r => r.Id > 0).ToList();
                var materials = same is not null && same.MaterialOptions == materialOptions ? same.Materials : null;
                if (activeTab == 0 && materials is null)
                    using (StartupTrace.Measure("cost_material_group_projection"))
                        materials = BuildMaterialRows(summary, rows, materialOptions, token);
                token.ThrowIfCancellationRequested();
                return new CostLoadSnapshot(summary, totals, rows, expenses, mode, after,
                    missing, materials, workRows, materialOptions, CultureInfo.CurrentCulture.Name);
            }
        }
        throw new InvalidOperationException("Себестоимость изменилась во время чтения. Повторите загрузку.");
    }

    private async Task ReloadAsync(bool allowReuse = false, CostDeltaRequest? delta = null)
    {
        if (!_attached) return;
        using var operationTiming = StartupTrace.BeginOperation();
        var generation = ++_loadGeneration;
        var cached = allowReuse && !_operationActive && _revision.Length > 0 ? _cachedCostSnapshot : null;
        _cachedCostSnapshot = null;
        _costReadCancellation?.Cancel();
        _loadingCosts = true;
        _revision = "";
        _loadError = null;
        RetryCostButton.Visibility = Visibility.Collapsed;
        SetStatus("Загрузка себестоимости из SQL Server…");
        // Let DataGrid finish CellEditEnding before disabling the editor.
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        if (!_attached || generation != _loadGeneration) return;
        CostTabs.IsEnabled = false;
        using var cancellation = new CancellationTokenSource();
        _costReadCancellation = cancellation;
        var token = cancellation.Token;
        var activeTab = Math.Clamp(CostTabs.SelectedIndex, 0, 3);
        var selectedChanged = false;
        try
        {
            var materialOptions = CaptureMaterialOptions();
            var data = await Task.Run(() => ReadCostSnapshot(_connectionString, _contractId, materialOptions,
                activeTab, cached, delta, token), token);
            if (token.IsCancellationRequested || !_attached || generation != _loadGeneration) return;
            if (CostTabs.SelectedIndex != activeTab)
            {
                selectedChanged = true;
                return;
            }
            StartupTrace.Mark("cost_snapshot_bind_begin");
            using var bindTiming = StartupTrace.Measure("cost_snapshot_bind");
            var sameBase = ReferenceEquals(cached?.Summary, data.Summary);
            if (!sameBase)
            {
                _summaryRows = data.Summary.Rows.ToList();
                ResetMaterialProjection();
                _summaryData = data.Summary;
                _allRows = new ObservableCollection<CostSummaryPreviewRow>(data.Rows);
                _missingPrices = data.MissingPrices;
                _filteredRows.Clear();
                _totalsData = null;
            }
            // Only the active projection is published. Hidden rows remain
            // identity anchors, not editable data, until verified on activation.
            switch (activeTab)
            {
                case 0:
                    if (data.Materials is not null && data.MaterialOptions == CaptureMaterialOptions())
                    {
                        ReplaceCostRows(MaterialsGrid, data.Materials);
                        _lastMaterialOptions = data.MaterialOptions;
                        _lastMaterialSource = data.Materials;
                        _lastMaterialCulture = CultureInfo.CurrentCulture;
                        _materialSearchDirty = false;
                    }
                    else ReloadMaterialView();
                    break;
                case 1:
                    if (!sameBase || !ReferenceEquals(cached?.Expenses, data.Expenses) || SmrExpenseGrid.ItemsSource is null)
                        using (StartupTrace.Measure("cost_smr_bind"))
                            ApplySmrTab(data.Expenses!, data.SmrMode, data.WorkRows!);
                    break;
                case 2:
                    ApplyFilter();
                    break;
                case 3:
                    if (!sameBase || !ReferenceEquals(_totalsData, data.Totals))
                        using (StartupTrace.Measure("cost_totals_bind"))
                            ApplyTotalsTab(data.Totals!);
                    break;
            }
            _cachedCostSnapshot = data;
            _publishedCostTab = activeTab;
            _revision = data.Revision;
            _needsReloadAfterAttach = false;
            var summary = data.Summary;
            SetStatus("СМР документа: " + Money.Format(summary.SmrContract) +
                "   ·   Итого закуп: " + Money.Format(summary.TotalCost) +
                "   ·   Итого розница: " + Money.Format(summary.TotalSell) +
                "   ·   Маржа: " + Money.Format(summary.TotalSell - summary.TotalCost) +
                (summary.Notes.Count > 0 ? "   ·   " + string.Join("; ", summary.Notes) : ""));
            CostTabs.IsEnabled = true;
            StartupTrace.Mark("cost_snapshot_bind_end");
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || !_attached || generation != _loadGeneration) return;
            _revision = "";
            _summaryData = null;
            _cachedCostSnapshot = null;
            _publishedCostTab = -1;
            ResetMaterialProjection();
            _totalsData = null;
            _loadError = "Не удалось загрузить себестоимость: " + ex.Message;
            FooterText.Text = _loadError;
            RetryCostButton.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_costReadCancellation, cancellation)) _costReadCancellation = null;
            if (generation == _loadGeneration)
            {
                _loadingCosts = false;
                if (selectedChanged && _attached) _ = ReloadAsync();
            }
        }
    }

    internal static List<CostSummaryPreviewRow> BuildSummaryPreview(CostSummaryData data) =>
        BuildSummaryPreview(data, CancellationToken.None);

    private static List<CostSummaryPreviewRow> BuildSummaryPreview(CostSummaryData data, CancellationToken token)
    {
        var items = new List<CostSummaryPreviewRow>();
        string M(decimal v) => v == 0m ? "—" : Money.Format(v);
        string Q(decimal v) => v == decimal.Truncate(v)
            ? v.ToString("0", CultureInfo.CurrentCulture)
            : v.ToString("0.###", CultureInfo.CurrentCulture);
        string Pct(decimal? v) => v is null ? "" : $"{v.Value.ToString("0.0", CultureInfo.CurrentCulture)}%";

        var systemLabels = data.Systems.ToDictionary(s => s.Id, s => s.Label);
        foreach (var row in data.Rows)
        {
            token.ThrowIfCancellationRequested();
            var systemLabel = systemLabels.GetValueOrDefault(row.SystemId, "");
            items.Add(new CostSummaryPreviewRow
            {
                Id = row.Id,
                MemberIds = new[] { row.Id },
                IsWork = row.Work,
                CostNote = row.CostNote,
                RowBackground = row.CostColor.Length == 7 && row.CostColor[0] == '#'
                    ? row.CostColor : "White",
                MatMarkupPct = row.UCost is > 0m
                    ? Pct((row.UPrice - row.UCost.Value) / row.UCost.Value * 100m)
                    : "",
                Name = $"[{systemLabel}] {row.Name}",
                Unit = row.Unit,
                Qty = Q(row.Qty),
                Supplier = row.Supplier,
                UCost = Num(row.UCost),
                UPrice = row.Work ? "" : Num(row.UPrice),
                OriginalWorkUnitPrice = row.Work ? Num(row.UPrice) : "",
                UWcost = Num(row.UWcost),
                UWsell = Num(row.UWsell),
                MCost = M(row.MCost),
                MSell = M(row.MSell),
                MatMargin = M(row.MatMargin),
                WCost = M(row.WCost),
                WSell = M(row.WSell),
                WorkMargin = M(row.WorkMargin),
                TotalCost = M(row.TotalCost),
                TotalSell = M(row.TotalSell),
                TotalMargin = M(row.TotalMargin),
                MarkupPct = Pct(row.MarkupPct),
            });
        }
        foreach (var (name, cost, sell, kind) in data.Extra)
        {
            token.ThrowIfCancellationRequested();
            bool works = string.Equals(kind, "works", StringComparison.OrdinalIgnoreCase);
            bool transport = string.Equals(kind, "trans", StringComparison.OrdinalIgnoreCase);
            items.Add(new CostSummaryPreviewRow
            {
                Id = transport ? -1 : 0,
                IsTransport = transport,
                Name = name,
                UCost = transport ? Num(cost) : "",
                MCost = works ? "—" : M(cost),
                MSell = works ? "—" : M(sell),
                MatMargin = works ? "—" : M(sell - cost),
                WCost = works ? M(cost) : "—",
                WSell = works ? M(sell) : "—",
                WorkMargin = works ? M(sell - cost) : "—",
                TotalCost = M(cost),
                TotalSell = M(sell),
                TotalMargin = M(sell - cost),
                RowBackground = "#FFF8EF",
            });
        }
        return items;
    }


    /// <summary>Экспорт сводной — xlsx.</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseSnapshot) return;
        // Freeze the displayed strings before the modal save dialog pumps the UI.
        var snapshot = SummaryGrid.Items.OfType<CostSummaryPreviewRow>().Select(r => new[]
        {
            r.Name, r.Unit, r.Qty, r.Supplier, r.MCost, r.MSell, r.MatMargin,
            r.WCost, r.WSell, r.WorkMargin, r.TotalCost, r.TotalSell, r.TotalMargin, r.MarkupPct
        }).ToArray();
        XlsxExport.Save(Window.GetWindow(this), $"Сводная — {_contractId}", sheet =>
        {
            XlsxExport.Header(sheet, 1, new[] { "Наименование", "Ед.", "Кол-во", "Поставщик",
                "Мат: закуп", "Мат: розница", "Мат: маржа", "Работы: закуп", "Работы: розница",
                "Работы: маржа", "Итого: закуп", "Итого: розница", "Итого: маржа", "СМР: наценка" });
            var row = 2;
            foreach (var values in snapshot)
            {
                for (var column = 0; column < values.Length; column++)
                    sheet.Cell(row, column + 1).Value = values[column];
                row++;
            }
            sheet.SheetView.FreezeRows(1);
        });
    }
    /// <summary>Наценка на СМР: выделенные строки или все (cost_summary_view._apply_markup).</summary>
    private async void Markup_Click(object sender, RoutedEventArgs e)
    {
        if (_readOnly || !CanUseSnapshot || CostTabs.SelectedIndex != 2) return;
        var revision = _revision;
        var attachment = Interlocked.Read(ref _attachmentGeneration);
        var selected = SummaryGrid.SelectedItems.OfType<CostSummaryPreviewRow>().ToArray();
        // An explicit selection containing only transport/extra/group rows is
        // not permission to change every position in the estimate.
        var allRows = SummaryGrid.SelectedItems.Count == 0;
        var ids = MarkupIds(allRows ? _allRows : selected);
        if (ids.Count == 0)
        {
            SetStatus("Нет позиций сметы для наценки.");
            return;
        }
        var scope = allRows ? "Все позиции сметы, включая скрытые фильтром" : "Выбранные позиции";
        var dialog = new SummaryMarkupDialog(
            $"{scope} ({ids.Count}). Цена клиенту за ед. будет пересчитана от закупа СМР.")
            { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (_readOnly || !CanUseSnapshot || CostTabs.SelectedIndex != 2 ||
            revision != _revision || attachment != Interlocked.Read(ref _attachmentGeneration) ||
            allRows != (SummaryGrid.SelectedItems.Count == 0) ||
            !new HashSet<long>(ids).SetEquals(MarkupIds(allRows ? _allRows :
                SummaryGrid.SelectedItems.OfType<CostSummaryPreviewRow>())))
        {
            SetStatus("Выбор или данные изменились. Откройте диалог снова.");
            return;
        }
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var percent = dialog.Percent;
        SetStatus("Пересчёт…");
        try
        {
            var (changed, skipped) = await Task.Run(() => SqlServerCostSummary.ApplySmrMarkup(
                _connectionString, _contractId, ids, percent, ServerRevision()));
            if (changed == 0)
            {
                SetStatus("У выбранных позиций не указан закуп СМР");
                return;
            }
            _revision = "";
            var message = $"Цена клиенту по СМР пересчитана у {changed} поз.";
            if (skipped > 0) message += $" · без закупа пропущено {skipped}";
            SetStatus(message + ". Вернуть — Ctrl+Z");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
        // CostOperation.Dispose requests the guarded readback, including unknown COMMIT.
    }

    private static List<long> MarkupIds(IEnumerable<CostSummaryPreviewRow> rows) =>
        rows.Where(r => !r.IsTransport && !r.IsGroup && r.Id > 0)
            .Select(r => r.Id).Distinct().ToList();
}