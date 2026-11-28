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
    private bool CanUseSnapshot => _attached && !_loadingCosts && !_operationActive && _revision.Length > 0;
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
        using var operation = BeginCostOperation();
        if (operation is null) return;
        SetStatus("Сохранение…");
        try
        {
            if (field == "supplier")
            {
                await Task.Run(() => SqlServerCostSummary.SetSupplier(
                    _connectionString, _contractId, positionId, input, ServerRevision()));
                _revision = "";
                SetStatus("Поставщик сохранён");
                await ReloadAsync();
                return;
            }
            var result = await Task.Run(() => SqlServerCostSummary.SaveSummaryCell(
                _connectionString, _contractId, positionId, contractField, input, ServerRevision()));
            if (!result.Saved)
            {
                SetStatus("Ошибка: " + result.Error);
                return;
            }
            _revision = result.Revision;
            SetStatus(row.IsTransport
                ? "Транспорт сохранён, итоги пересчитаны"
                : "Сохранено. Вернуть — Ctrl+Z в смете");
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
            ResetMaterialProjection();
        };
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

    private sealed record CostLoadSnapshot(CostSummaryData Summary, CostTotalsData Totals,
        List<CostSummaryPreviewRow> Rows, List<SmrExpensePreviewRow> Expenses, string SmrMode, string Revision,
        CostMissingPriceIndex MissingPrices, List<CostSummaryPreviewRow> Materials,
        List<CostSummaryPreviewRow> WorkRows, MaterialOptions MaterialOptions);

    private static CostLoadSnapshot ReadCostSnapshot(string connectionString, int contractId, MaterialOptions materialOptions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var conn = SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult();
        string Revision() => SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var before = Revision();
            token.ThrowIfCancellationRequested();
            StartupTrace.Mark("cost_snapshot_financial_begin");
            var (summary, totals) = SqlServerCostTotals.SummaryAndTotals(conn, contractId, token);
            StartupTrace.Mark("cost_snapshot_financial_end");
            token.ThrowIfCancellationRequested();
            StartupTrace.Mark("cost_snapshot_expenses_begin");
            var expenses = SqlServerSmrExpenses.List(conn, contractId, token).Select(x =>
            {
                token.ThrowIfCancellationRequested();
                return new SmrExpensePreviewRow
                {
                    Id = x.Id,
                    Date = x.Date?.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture) ?? "",
                    Kind = x.Kind, Who = x.Who,
                    Amount = x.Amount.ToString("0.##", CultureInfo.CurrentCulture), Note = x.Note,
                };
            }).ToList();
            StartupTrace.Mark("cost_snapshot_expenses_end");
            token.ThrowIfCancellationRequested();
            using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                "SELECT smr_mode FROM dbo.contracts WHERE id=@id", conn);
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            var mode = Convert.ToString(cmd.ExecuteScalarAsync(token).GetAwaiter().GetResult(), CultureInfo.InvariantCulture) ?? "sum";
            token.ThrowIfCancellationRequested();
            var after = Revision();
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(before) && before == after)
            {
                StartupTrace.Mark("cost_snapshot_format_begin");
                List<CostSummaryPreviewRow> rows;
                using (StartupTrace.Measure("cost_summary_format"))
                    rows = BuildSummaryPreview(summary, token);
                CostMissingPriceIndex missing;
                using (StartupTrace.Measure("cost_missing_index"))
                    missing = new CostMissingPriceIndex(summary.Rows, token);
                List<CostSummaryPreviewRow> workRows;
                using (StartupTrace.Measure("cost_smr_work_projection"))
                    workRows = rows.Where(r => r.Id > 0).ToList();
                StartupTrace.Mark("cost_snapshot_format_end");
                StartupTrace.Mark("cost_snapshot_group_begin");
                List<CostSummaryPreviewRow> materials;
                using (StartupTrace.Measure("cost_material_group_projection"))
                    materials = BuildMaterialRows(summary, rows, materialOptions, token);
                StartupTrace.Mark("cost_snapshot_group_end");
                token.ThrowIfCancellationRequested();
                return new CostLoadSnapshot(summary, totals, rows, expenses, mode, after,
                    missing, materials, workRows, materialOptions);
            }
        }
        throw new InvalidOperationException("Себестоимость изменилась во время чтения. Повторите загрузку.");
    }

    private async Task ReloadAsync()
    {
        if (!_attached) return;
        using var operationTiming = StartupTrace.BeginOperation();
        var generation = ++_loadGeneration;
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
        try
        {
            var materialOptions = CaptureMaterialOptions();
            var data = await Task.Run(() => ReadCostSnapshot(_connectionString, _contractId, materialOptions, token), token);
            if (token.IsCancellationRequested || !_attached || generation != _loadGeneration) return;
            StartupTrace.Mark("cost_snapshot_bind_begin");
            using var bindTiming = StartupTrace.Measure("cost_snapshot_bind");
            _summaryRows = data.Summary.Rows.ToList();
            ResetMaterialProjection();
            _summaryData = data.Summary;
            _allRows = new ObservableCollection<CostSummaryPreviewRow>(data.Rows);
            _missingPrices = data.MissingPrices;
            _filteredRows.Clear();
            ApplyFilter();
            if (data.MaterialOptions == CaptureMaterialOptions())
            {
                ReplaceCostRows(MaterialsGrid, data.Materials);
                _lastMaterialOptions = data.MaterialOptions;
                _lastMaterialSource = data.Materials;
                _lastMaterialCulture = CultureInfo.CurrentCulture;
            }
            else ReloadMaterialView();
            using (StartupTrace.Measure("cost_smr_bind"))
                ApplySmrTab(data.Expenses, data.SmrMode, data.WorkRows);
            using (StartupTrace.Measure("cost_totals_bind"))
                ApplyTotalsTab(data.Totals);
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
            ResetMaterialProjection();
            _totalsData = null;
            _loadError = "Не удалось загрузить себестоимость: " + ex.Message;
            FooterText.Text = _loadError;
            RetryCostButton.Visibility = Visibility.Visible;
        }
        finally
        {
            if (ReferenceEquals(_costReadCancellation, cancellation)) _costReadCancellation = null;
            if (generation == _loadGeneration) _loadingCosts = false;
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
        XlsxExport.Save(Window.GetWindow(this), $"Сводная — {_contractId}", sheet =>
        {
            XlsxExport.Header(sheet, 1, new[] { "Наименование", "Ед.", "Кол-во", "Поставщик",
                "Мат: закуп", "Мат: розница", "Мат: маржа", "Работы: закуп", "Работы: розница",
                "Работы: маржа", "Итого: закуп", "Итого: розница", "Итого: маржа", "СМР: наценка" });
            var row = 2;
            foreach (var rowItem in SummaryGrid.Items.OfType<CostSummaryPreviewRow>())
            {
                sheet.Cell(row, 1).Value = rowItem.Name;
                sheet.Cell(row, 2).Value = rowItem.Unit;
                sheet.Cell(row, 3).Value = rowItem.Qty;
                sheet.Cell(row, 4).Value = rowItem.Supplier;
                sheet.Cell(row, 5).Value = rowItem.MCost;
                sheet.Cell(row, 6).Value = rowItem.MSell;
                sheet.Cell(row, 7).Value = rowItem.MatMargin;
                sheet.Cell(row, 8).Value = rowItem.WCost;
                sheet.Cell(row, 9).Value = rowItem.WSell;
                sheet.Cell(row, 10).Value = rowItem.WorkMargin;
                sheet.Cell(row, 11).Value = rowItem.TotalCost;
                sheet.Cell(row, 12).Value = rowItem.TotalSell;
                sheet.Cell(row, 13).Value = rowItem.TotalMargin;
                sheet.Cell(row, 14).Value = rowItem.MarkupPct;
                row++;
            }
            sheet.SheetView.FreezeRows(1);
        });
    }
    /// <summary>Наценка на СМР: выделенные строки или все (cost_summary_view._apply_markup).</summary>
    private async void Markup_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var selected = SummaryGrid.SelectedItems.OfType<CostSummaryPreviewRow>()
            .Where(r => !r.IsTransport && r.Id > 0).Select(r => (long)r.Id).ToList();
        var ids = selected.Count > 0 ? selected
            : (SummaryGrid.ItemsSource as ObservableCollection<CostSummaryPreviewRow> ?? new())
                .Where(r => !r.IsTransport && r.Id > 0).Select(r => (long)r.Id).ToList();
        var scope = selected.Count > 0 ? "Выбранные позиции" : "Все позиции сметы";
        var dialog = new PromptDialog("Наценка на СМР",
            $"{scope} ({ids.Count}). Цена клиенту за ед. будет пересчитана от закупа СМР.\nНаценка, %",
            "50") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (!decimal.TryParse(dialog.Value.Trim().Replace(',', '.'), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var percent))
        {
            SetStatus("Недопустимое число: " + dialog.Value);
            return;
        }
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
        await ReloadAsync();
    }
}