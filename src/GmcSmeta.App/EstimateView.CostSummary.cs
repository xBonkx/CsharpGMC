using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Вкладка «Сводная» КП — перенос python\smeta\ui\cost_summary_view.py: одна таблица для
/// директора — каждая строка КП с материалами (закуп за ед. · розн. за ед. · закуп ·
/// розница · маржа), работами (…) и итого; системы (или поставщики, «одинаковые вместе»)
/// сворачиваются с суммами; фильтр «Без цен ▾» с четырьмя видами пропусков; плитки.
/// Редактируются все ячейки, кроме сумм и итогов: цены за ед., наименование, ед., кол-во —
/// и в свёрнутых группах, и в «одинаковые вместе» (вписанное идёт во все строки группы).
/// Записи: SqlServerCostSummary.SaveSummaryCell / ApplySmrMarkup / TransferSmrToSystems,
/// SqlServerCostTotals.SaveTotalsField (сумма клиенту у транспорта).
/// </summary>
public partial class EstimateView
{
    // ------------------------------------------------------------------ model
    /// <summary>Строка «Сводной»: позиция, свёрнутая группа (система/поставщик),
    /// «одинаковые вместе», extra-строка или ИТОГО (cost_summary_view._row_item / _money_item).</summary>
    public sealed record SummaryRow
    {
        public string Kind { get; init; } = "pos";   // system | pos | extra | total
        public long SystemId { get; init; }
        public long PositionId { get; init; }
        public long[] MemberIds { get; init; } = Array.Empty<long>();
        public bool IsWork { get; init; }
        public bool IsFact { get; init; }
        public bool HasKids { get; init; }
        public bool IsTransport { get; init; }
        public bool IsMerged => Kind == "pos" && MemberIds.Length > 1;
        public bool IsSystemHeader => Kind == "system";
        public bool IsTotalRow => Kind == "total";
        public bool IsPositionRow => Kind == "pos";
        public bool IsIndented { get; init; }
        public string? SupplierGroupKey { get; init; }
        public bool CanCollapseSystem => IsSystemHeader && (SystemId != 0 || SupplierGroupKey is not null);
        public bool IsExtraRow => Kind == "extra";
        /// <summary>▾ / ▸ у шапки системы (сворачивание, как в QTreeWidget эталона).</summary>
        public string CollapseMark { get; init; } = "▾";
        public string MCostTip => IsTransport ? "Двойной щелчок — изменить транспорт вручную" : "";
        public string Name { get; init; } = "";
        public string Unit { get; init; } = "";
        public string QtyText { get; init; } = "";
        public string QtyRaw { get; init; } = "";
        public string CostUnitText { get; init; } = "";
        public string CostUnitRaw { get; init; } = "";
        public string CostUnitTip { get; init; } = "";
        public string PriceUnitText { get; init; } = "";
        public string PriceUnitRaw { get; init; } = "";
        public string PriceUnitTip { get; init; } = "";
        public string WorkCostText { get; init; } = "";
        public string WorkCostRaw { get; init; } = "";
        public string WorkCostTip { get; init; } = "";
        public string WorkSellText { get; init; } = "";
        public string WorkSellRaw { get; init; } = "";
        public string WorkSellTip { get; init; } = "";
        public string MCostText { get; init; } = "";
        public string MSellText { get; init; } = "";
        public string WCostText { get; init; } = "";
        public string WSellText { get; init; } = "";
        public string TotalCostText { get; init; } = "";
        public string TotalSellText { get; init; } = "";
        public string MatMarginText { get; init; } = "";
        public string WorkMarginText { get; init; } = "";
        public string MarkupText { get; init; } = "";
        // Legacy columns display the raw differences, as Python VAL_COLS.
        public decimal MatLegacyMargin { get; init; }
        public decimal WorkLegacyMargin { get; init; }
        public string MatLegacyMarginText => SumText(MatLegacyMargin);
        public string WorkLegacyMarginText => SumText(WorkLegacyMargin);
        public string TotalLegacyMarginText => SumText(MatLegacyMargin + WorkLegacyMargin);
        public bool MatLegacyMarginNeg => MatLegacyMargin < -0.005m;
        public bool WorkLegacyMarginNeg => WorkLegacyMargin < -0.005m;
        public bool TotalLegacyMarginNeg => MatLegacyMargin + WorkLegacyMargin < -0.005m;
        public bool MatMarginNeg { get; init; }
        public bool WorkMarginNeg { get; init; }
        // красные «—» на пропусках (CostMark — всегда; остальные — при включённом фильтре)
        public bool CostMark { get; init; }
        public bool PriceMark { get; init; }
        public bool WorkCostMark { get; init; }
        public bool WorkSellMark { get; init; }
        // флаги пропусков для фильтра
        public bool NoCost { get; init; }
        public bool NoPrice { get; init; }
        public bool NowCost { get; init; }
        public bool NowSell { get; init; }
        public bool IsOdd { get; init; }
    }

    /// <summary>Рек «Сводной» в памяти: верхняя строка КП или слитая «одинаковые вместе».</summary>
    private sealed class SummaryRec
    {
        public List<long> Ids = new();
        public long SystemId;
        public string Name = "";
        public string Unit = "";
        public decimal Qty;
        public decimal MCost, MSell, WCost, WSell;
        public decimal? UCost; public bool UCostDiff;
        public decimal? UPrice; public bool UPriceDiff;
        public decimal? UWcost; public bool UWcostDiff;
        public decimal? UWsell; public bool UWsellDiff;
        public bool NoCost, NoPrice, NowCost, NowSell, Fact, Kids, Work;
        public string Supplier = "";

        public decimal Sell => MSell + WSell;
        public decimal Cost => MCost + WCost;
        // «По марже, % — меньше сверху»: доля маржи в рознице (cost_summary_view._pct)
        public decimal? Pct => Sell != 0m && Cost != 0m ? (Sell - Cost) / Sell * 100m : null;
    }

    // ------------------------------------------------------------------ state
    private CostSummaryData? _costSummaryData;
    private IReadOnlyList<CostSummaryRow> SummaryRecs => _costSummaryData?.Rows ?? Array.Empty<CostSummaryRow>();
    private CostMissingPriceIndex _summaryMissingIndex = new(Array.Empty<CostSummaryRow>());
    private CostSummarySearchIndex? _summarySearchIndex;
    private Dictionary<long, string> _summaryLabels = new();
    private string _summaryMode = "kp";
    private string _summarySort = "kp";
    private bool _summaryMissingOn;
    private readonly HashSet<long> _collapsedSummarySystems = new();
    private readonly HashSet<string> _collapsedSummarySuppliers = new(StringComparer.Ordinal);
    private readonly HashSet<long> _summarySelectedSystems = new();
    private List<(long Id, string Label)> _summarySystemOptions = new();
    private DispatcherTimer? _summarySearchDebounce;
    private bool _summarySearchDirty;
    private bool _summaryBusy;
    private bool _summaryLoading;
    private int _summaryLoadVersion;
    private CancellationTokenSource? _summaryLoadCancellation;
    private string _summaryRevision = "";
    private bool SummarySnapshotReady => !_detached && !_summaryLoading && !_summaryBusy && !_summarySearchDirty &&
        !_serverConflict && Volatile.Read(ref _pendingWrites) == 0 &&
        !string.IsNullOrWhiteSpace(_summaryRevision) && _summaryRevision == _serverRevision;
    private List<MenuItem> _summaryMissingItems = new();

    private static readonly (string Key, string Text)[] SummaryMissingKinds =
    {
        ("noprice", "Материалы: нет цены клиенту"),
        ("nocost", "Материалы: нет закупа"),
        ("nowsell", "Работы: нет розницы"),
        ("nowcost", "Работы: нет закупа"),
    };

    // все четыре вида пропусков отмечены по умолчанию (cost_summary_view MISSING)
    private readonly HashSet<string> _summaryMissingChecked =
        new(SummaryMissingKinds.Select(k => k.Key));

    private HashSet<string> SummaryMissingSelected() => _summaryMissingChecked;

    // ------------------------------------------------------------------ load
    private void CancelSummaryRead()
    {
        // Only read requests are cancelled here. The write queue and its
        // mandatory estimate readback have a separate lifetime.
        _summarySearchDebounce?.Stop();
        _summaryLoadVersion++;
        var cancellation = _summaryLoadCancellation;
        _summaryLoadCancellation = null;
        _summaryLoading = false;
        cancellation?.Cancel();
    }

    private void InvalidateCostSummary()
    {
        _summaryExcelCancellation?.Cancel();
        CancelSummaryRead();
        _summaryRevision = "";
        _costSummaryData = null;
        _summaryMissingIndex = new(Array.Empty<CostSummaryRow>());
        _summarySearchIndex = null;
        if (SummaryGrid is { } grid)
        {
            // Reload after a write clears the source before the asynchronous read.
            // Capture once, before that clear, not from an already empty grid.
            if (grid.ItemsSource is not null) _summaryPendingView = CaptureSummaryView(grid);
            grid.ItemsSource = null;
        }
    }

    private async Task LoadCostSummaryAsync(bool force = false)
    {
        if (_estimate is null || _detached) return;
        // Only a visible tab may create its visual tree. Cache checks and
        // invalidation use SummaryView/SummaryGrid without constructing it.
        if (SummaryView is null)
        {
            if (_visibleEstimateMode != "summary" || SummaryPanel is null) return;
            EnsureSummaryView(SummaryPanel, this);
        }
        if (_summaryLoading) return;
        if (!force && (HasGridEdit(SummaryGrid) || _summaryBusy)) return;
        var expected = _serverRevision;
        var ownRevision = _costSummaryData is not null ? _summaryRevision : "";
        var cachedSummary = !force && TotalsSnapshotReady ? _totalsSourceSummary : null;
        var cachedRevision = cachedSummary is null ? "" : _totalsRevision;
        _summaryLoading = true;
        _summaryRevision = "";
        var version = ++_summaryLoadVersion;
        using var cancellation = new CancellationTokenSource();
        _summaryLoadCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var snapshot = await Task.Run(async () =>
            {
                using var conn = await SqlServerDatabase.OpenAsync(_sqlConnectionString, token).ConfigureAwait(false);
                var before = await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(before)) throw new InvalidOperationException("SQL Server не подтвердил ревизию.");
                if (before != expected || CostTabRevisionPolicy.CanReuse(expected, ownRevision, before, force))
                    return (Data: (CostSummaryData?)null, Revision: before, Missing: (CostMissingPriceIndex?)null,
                        Labels: (Dictionary<long, string>?)null, Search: (CostSummarySearchIndex?)null);
                var data = CostFinancialSnapshot.CanReuse(cachedSummary, cachedRevision, before)
                    ? cachedSummary! : SqlServerCostSummary.Summary(conn, _contractId, token);
                if (string.IsNullOrWhiteSpace(before) ||
                    before != await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false))
                    throw new InvalidOperationException("Данные изменились во время чтения сводной. Повтори обновление.");
                token.ThrowIfCancellationRequested();
                using var prepare = StartupTrace.Measure("embedded_summary_prepare");
                var labels = SummarySystemSelection.Labels(data.Systems.Select(s => (s.Id, s.Label)), data.SystemParents);
                var missing = new CostMissingPriceIndex(data.Rows, token);
                token.ThrowIfCancellationRequested();
                var search = new CostSummarySearchIndex(data.Rows, labels);
                token.ThrowIfCancellationRequested();
                return (Data: (CostSummaryData?)data, Revision: before, Missing: (CostMissingPriceIndex?)missing,
                    Labels: (Dictionary<long, string>?)labels, Search: (CostSummarySearchIndex?)search);
            }, token);
            if (token.IsCancellationRequested || version != _summaryLoadVersion || _detached ||
                !ConfirmCostTabRevision(expected, snapshot.Revision)) return;
            if (snapshot.Data is null)
            {
                _summaryRevision = snapshot.Revision;
                if (_summarySearchDirty) SummaryRebuild();
                StartupTrace.Mark("embedded_summary_revision_hit");
                return;
            }
            using var bind = StartupTrace.Measure("embedded_summary_bind");
            _costSummaryData = snapshot.Data;
            _summaryMissingIndex = snapshot.Missing!;
            _summaryLabels = snapshot.Labels!;
            _summarySearchIndex = snapshot.Search!;
            if (SummaryTransferSmrButton is not null)
                SummaryTransferSmrButton.IsEnabled = !_estimate.Locked;
            if (SummaryExcelButton is not null) SummaryExcelButton.IsEnabled = _summaryExcelCancellation is null;
            SummaryFillSystemFilter();
            SummaryRebuild();
            _summaryRevision = snapshot.Revision;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // SqlClient can report cancellation as SqlException as well as OCE.
        }
        catch (Exception ex)
        {
            if (version == _summaryLoadVersion)
                SetStatus("Сводная: ошибка загрузки — " + ex.Message, true);
        }
        finally
        {
            if (ReferenceEquals(_summaryLoadCancellation, cancellation))
            {
                _summaryLoadCancellation = null;
                _summaryLoading = false;
            }
        }
    }

    private void SummaryFillSystemFilter()
    {
        if (_costSummaryData is not { } data) return;
        var systemsWithRows = SummaryRecs.Select(r => r.SystemId).ToHashSet();
        _summarySystemOptions = data.Systems
            .Where(s => s.Label != "Без системы" || systemsWithRows.Contains(s.Id))
            .Select(s => (s.Id, _summaryLabels.GetValueOrDefault(s.Id, s.Label))).ToList();
        _summarySelectedSystems.IntersectWith(_summarySystemOptions.Select(s => s.Id));
        SummarySyncSystemFilter();
    }

    private void SummarySyncSystemFilter()
    {
        if (SummarySystemFilter is null || SummarySystemFilterText is null) return;
        var names = _summarySystemOptions.Where(s => _summarySelectedSystems.Contains(s.Id))
            .Select(s => s.Label).ToArray();
        SummarySystemFilterText.Text = names.Length switch
        {
            0 => "Все системы", <= 2 => string.Join(", ", names), _ => $"Систем: {names.Length}"
        };
        SummarySystemFilter.Tag = names.Length > 0 ? "True" : "False";
        SummarySystemFilter.ToolTip = names.Length == 0
            ? "Показать одну или несколько систем вместе с подразделами"
            : string.Join("\n", names);
    }

    internal void SummarySystemFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _costSummaryData is not { } data || _detached) return;
        var options = _summarySystemOptions;
        var popup = new SummarySystemPickerPopup(button, options, data.SystemParents,
            _summarySelectedSystems, selected =>
            {
                // A refreshed or detached editor must not accept stale popup input.
                if (_detached || !ReferenceEquals(options, _summarySystemOptions) || _costSummaryData is null)
                    return false;
                if (_summarySelectedSystems.SetEquals(selected)) return true;
                _summarySelectedSystems.Clear();
                _summarySelectedSystems.UnionWith(selected);
                SummarySyncSystemFilter();
                SummaryRebuild();
                return true;
            });
        popup.Open();
    }

    // ------------------------------------------------------------------ build
    private void SummaryRebuild()
    {
        _summarySearchDebounce?.Stop();
        var data = _costSummaryData;
        if (data is null || SummaryGrid is null) return;
        _summarySearchDirty = false;
        using var timing = StartupTrace.Measure("embedded_summary_filter_group_format");
        var recs = SummaryRecs;
        var chosen = SummaryMissingSelected();
        var missingMask = chosen.Aggregate(CostMissingPrice.None, (mask, key) => mask | SummaryFlag(key));
        // пометки «—» на пропусках: nocost — всегда, остальные — при включённом фильтре
        var markKinds = _summaryMissingOn ? chosen : new HashSet<string>();

        SummaryUpdateMissingCounts(missingMask);

        // фильтр (show_data): системы, «без цен», поиск
        var systemIds = SummarySystemSelection.Expand(_summarySelectedSystems, data.SystemParents);
        var rows = (_summarySearchIndex ??= new CostSummarySearchIndex(recs, _summaryLabels))
            .Filter(SummarySearch?.Text, systemIds, _summaryMissingOn, missingMask)
            .ToList();
        bool filtered = rows.Count != recs.Count;

        var built = new List<SummaryRow>();
        decimal totMCost = 0m, totMSell = 0m, totWCost = 0m, totWSell = 0m;
        int stripe = 0;

        if (_summaryMode == "same")
        {
            // «Одинаковые вместе»: одна строка на (наименование + ед.), суммы количеств
            var merged = new List<SummaryRec>();
            var map = new Dictionary<(string, string), SummaryRec>();
            foreach (var r in rows)
            {
                var key = (r.Name.Trim().ToLowerInvariant(), r.Unit.Trim().ToLowerInvariant());
                if (!map.TryGetValue(key, out var m))
                {
                    m = new SummaryRec
                    {
                        SystemId = r.SystemId, Name = r.Name, Unit = r.Unit, Qty = 0m,
                        UCost = r.UCost, UPrice = r.UPrice, UWcost = r.UWcost, UWsell = r.UWsell,
                        Work = r.Work,
                    };
                    map[key] = m;
                    merged.Add(m);
                }
                MergeInto(m, r);
            }
            var list = SortRecs(merged);
            foreach (var item in list)
            {
                built.Add(SummaryRowFromRec(item, markKinds, stripe++ % 2 == 1));
                totMCost += item.MCost; totMSell += item.MSell;
                totWCost += item.WCost; totWSell += item.WSell;
            }
        }
        else
        {
            // группы: по системам или по поставщикам; свёрнутая группа тоже редактируется
            var records = rows.Select(ToRec);
            var groups = new List<(string Title, long SystemId, string? SupplierKey, List<SummaryRec> Items)>();
            if (_summaryMode == "sup")
            {
                foreach (var g in records.GroupBy(r => r.Supplier).OrderBy(g => g.Key.Length == 0 ? 1 : 0)
                         .ThenBy(g => g.Key.Length == 0 ? "" : g.Key.ToLowerInvariant(), StringComparer.Ordinal))
                    groups.Add((g.Key.Length == 0 ? "Поставщик не указан" : $"Поставщик: {g.Key}",
                        0, g.Key, g.ToList()));
            }
            else
            {
                var bySystem = records.ToLookup(r => r.SystemId);
                foreach (var (id, label, _) in data.Systems)
                {
                    var items = bySystem[id].ToList();
                    if (items.Count > 0)
                        groups.Add((_summaryLabels.GetValueOrDefault(id, label), id, null, items));
                }
            }
            foreach (var (title, sysId, supplierKey, items) in groups)
            {
                var sorted = SortRecs(items);
                var amounts = AccumulateSummaryGroup(sorted);
                built.Add(SummaryRowFromGroup(sysId, supplierKey, title, sorted, amounts));
                // Сворачивание скрывает только детали, не исключает систему из итогов.
                totMCost += amounts.MaterialCost; totMSell += amounts.MaterialSell;
                totWCost += amounts.WorkCost; totWSell += amounts.WorkSell;
                if (SummaryGroupCollapsed(sysId, supplierKey))
                    continue;
                foreach (var r in sorted)
                    built.Add(SummaryRowFromRec(r, markKinds, stripe++ % 2 == 1, indented: true));
            }
        }

        // extra: прочие затраты на монтаж / куплено вне КП / транспорт — только без фильтра
        if (!filtered)
        {
            var zeroMargin = SummaryMoneyDisplay.Margin(0m, 0m).Text;
            foreach (var (name, cost, sell, kind) in data.Extra)
            {
                var work = kind == "works";
                if (work) { totWCost += cost; totWSell += sell; }
                else { totMCost += cost; totMSell += sell; }

                var costText = SumText(cost);
                var sellText = SumText(sell);
                var margin = SummaryMoneyDisplay.Margin(cost, sell);
                built.Add(new SummaryRow
                {
                    Kind = "extra", Name = name, IsTransport = kind == "trans",
                    IsOdd = stripe++ % 2 == 1,
                    MCostText = work ? "" : costText, MSellText = work ? "" : sellText,
                    WCostText = work ? costText : "", WSellText = work ? sellText : "",
                    TotalCostText = costText, TotalSellText = sellText,
                    MatLegacyMargin = work ? 0m : sell - cost,
                    WorkLegacyMargin = work ? sell - cost : 0m,
                    MatMarginText = work ? zeroMargin : margin.Text,
                    WorkMarginText = work ? margin.Text : zeroMargin,
                    MatMarginNeg = !work && margin.Negative,
                    WorkMarginNeg = work && margin.Negative,
                });
            }
        }

        // ИТОГО
        var matMargin = SummaryMoneyDisplay.Margin(totMCost, totMSell);
        var workMargin = SummaryMoneyDisplay.Margin(totWCost, totWSell);
        built.Add(new SummaryRow
        {
            Kind = "total",
            Name = "ИТОГО" + (filtered ? " (по фильтру)" : ""),
            MCostText = SumText(totMCost), MSellText = SumText(totMSell),
            WCostText = SumText(totWCost), WSellText = SumText(totWSell),
            TotalCostText = SumText(totMCost + totWCost), TotalSellText = SumText(totMSell + totWSell),
            MatLegacyMargin = totMSell - totMCost, WorkLegacyMargin = totWSell - totWCost,
            MatMarginText = matMargin.Text,
            WorkMarginText = workMargin.Text,
            MatMarginNeg = matMargin.Negative,
            WorkMarginNeg = workMargin.Negative,
        });

        SummaryReplaceRows(built);
        SummarySyncViewButtons();
        SummaryFillTiles(totMCost, totMSell, totWCost, totWSell, data, filtered,
            nNocost: _summaryMissingIndex.CountAny(CostMissingPrice.MaterialCost), shownRows: rows.Count, totalRows: recs.Count);
    }

    private static string? SummarySelectionKey(SummaryRow row) =>
        SummarySystemSelection.RowKey(row.Kind, row.SystemId, row.SupplierGroupKey, row.MemberIds);

    private sealed record SummaryViewState(HashSet<string> Selected, string? CurrentKey,
        DataGridColumn? Column, double Vertical, double Horizontal,
        SummaryViewportAnchor.Row? CurrentAnchor, SummaryViewportAnchor.Row? TopAnchor);
    private SummaryViewState? _summaryPendingView;

    private static ScrollViewer? SummaryScroll(DataGrid grid) =>
        grid.Template?.FindName("DG_ScrollViewer", grid) as ScrollViewer;

    private static ScrollContentPresenter? SummaryViewport(ScrollViewer? scroll) =>
        scroll?.Template?.FindName("PART_ScrollContentPresenter", scroll) as ScrollContentPresenter;

    // Walk only realized visual rows; do not enumerate/generate offscreen containers.
    private static IEnumerable<DataGridRow> SummaryRealizedRows(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is DataGridRow row) yield return row;
            else if (child is not DataGrid)
                foreach (var nested in SummaryRealizedRows(child)) yield return nested;
        }
    }

    private static double? SummaryRowY(DataGridRow row, ScrollContentPresenter viewport)
    {
        if (!row.IsVisible || row.ActualHeight <= 0 || !viewport.IsAncestorOf(row)) return null;
        var y = row.TranslatePoint(new Point(0, 0), viewport).Y;
        return double.IsFinite(y) ? y : null;
    }

    private static SummaryViewState CaptureSummaryView(DataGrid grid)
    {
        var selected = grid.SelectedItems.OfType<SummaryRow>().Select(SummarySelectionKey)
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var current = grid.CurrentCell;
        var currentKey = current.Item is SummaryRow row ? SummarySelectionKey(row) : null;
        var scroll = SummaryScroll(grid);
        SummaryViewportAnchor.Row? currentAnchor = null, topAnchor = null;
        if (grid.IsVisible && scroll is not null &&
            (!scroll.CanContentScroll || VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Pixel) &&
            SummaryViewport(scroll) is { } viewport)
        {
            foreach (var container in SummaryRealizedRows(viewport))
            {
                if (container.Item is not SummaryRow item || SummarySelectionKey(item) is not { } key ||
                    SummaryRowY(container, viewport) is not double y ||
                    y + container.ActualHeight <= 0 || y >= viewport.ActualHeight) continue;
                if (topAnchor is null || y < topAnchor.Value.Y)
                    topAnchor = new(key, y);
                // Like Python view_anchor: prefer the current row when its top is visible.
                if (key == currentKey && y >= 0) currentAnchor = new(key, y);
            }
        }
        return new(selected, currentKey, current.Column, scroll?.VerticalOffset ?? 0,
            scroll?.HorizontalOffset ?? 0, currentAnchor, topAnchor);
    }

    private void SummaryReplaceRows(List<SummaryRow> built)
    {
        var grid = SummaryGrid;
        // Never skip an authoritative replacement or interfere with an active editor.
        if (SummarySnapshotReady && _summaryPendingView is null &&
            grid.ItemsSource is ObservableCollection<SummaryRow> current &&
            grid.Items is System.ComponentModel.IEditableCollectionView { IsEditingItem: false, IsAddingNew: false } &&
            SummaryRowSequence.Same(current, built))
            return;

        var state = grid.ItemsSource is not null ? CaptureSummaryView(grid) : _summaryPendingView;
        _summaryPendingView = null;
        var source = new ObservableCollection<SummaryRow>(built);
        grid.ItemsSource = source;
        TableColumnMenu.RequestSummaryFit(grid);
        if (state is null) return;

        // Use exact stable keys; duplicate/ambiguous keys must not carry selection.
        var byKey = SummaryRowSequence.RestoreCandidates(built, state.Selected, state.CurrentKey,
            state.CurrentAnchor?.Key, state.TopAnchor?.Key);
        foreach (var (key, item) in byKey)
            if (state.Selected.Contains(key)) grid.SelectedItems.Add(item);
        if (state.CurrentKey is { } currentKey && byKey.TryGetValue(currentKey, out var restoredCurrent) &&
            state.Column is { Visibility: Visibility.Visible } column && grid.Columns.Contains(column))
            grid.CurrentCell = new DataGridCellInfo(restoredCurrent, column);

        var anchor = SummaryViewportAnchor.Choose(state.CurrentAnchor, state.TopAnchor,
            byKey.Keys.ToHashSet(StringComparer.Ordinal));

        bool IsCurrent() => !_detached && grid.IsVisible && ReferenceEquals(grid.ItemsSource, source);
        void Restore(bool realize)
        {
            if (!IsCurrent() || SummaryScroll(grid) is not { } scroll) return;
            scroll.ScrollToHorizontalOffset(state.Horizontal);
            if (anchor is not { } target ||
                scroll.CanContentScroll && VirtualizingPanel.GetScrollUnit(grid) != ScrollUnit.Pixel ||
                SummaryViewport(scroll) is not { } viewport)
            {
                scroll.ScrollToVerticalOffset(state.Vertical);
                return;
            }
            var item = byKey[target.Key];
            if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow container &&
                ReferenceEquals(container.Item, item) && SummaryRowY(container, viewport) is double y)
            {
                scroll.ScrollToVerticalOffset(SummaryViewportAnchor.Offset(scroll.VerticalOffset, y,
                    target.Y, scroll.ScrollableHeight));
                // A second bounded pass compensates for virtualization/remeasurement.
                if (realize) grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    new Action(() => Restore(false)));
            }
            else if (realize)
            {
                grid.ScrollIntoView(item);
                grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Restore(false)));
            }
            else scroll.ScrollToVerticalOffset(state.Vertical);
        }

        if (grid.IsVisible)
            grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Restore(true)));
    }

    private static void MergeInto(SummaryRec m, CostSummaryRow r)
    {
        m.Ids.Add(r.Id);
        m.Qty += r.Qty;
        m.MCost += r.MCost; m.MSell += r.MSell;
        m.WCost += r.WCost; m.WSell += r.WSell;
        if (m.UCost != r.UCost) { m.UCost = null; m.UCostDiff = true; }
        if (m.UPrice != r.UPrice) { m.UPrice = null; m.UPriceDiff = true; }
        if (m.UWcost != r.UWcost) { m.UWcost = null; m.UWcostDiff = true; }
        if (m.UWsell != r.UWsell) { m.UWsell = null; m.UWsellDiff = true; }
        m.NoCost |= r.NoCost; m.NoPrice |= r.NoPrice;
        m.NowCost |= r.NowCost; m.NowSell |= r.NowSell;
        m.Fact |= r.Fact; m.Kids |= r.Kids;
        // The merged presentation has no supplier column or supplier sort.
        // Search has already matched the original rows, including suppliers.
    }

    private SummaryRec ToRec(CostSummaryRow r) => new()
    {
        Ids = new List<long> { r.Id },
        SystemId = r.SystemId,
        Name = r.Name,
        Unit = r.Unit,
        Qty = r.Qty,
        MCost = r.MCost, MSell = r.MSell, WCost = r.WCost, WSell = r.WSell,
        UCost = r.UCost, UPrice = r.UPrice, UWcost = r.UWcost, UWsell = r.UWsell,
        NoCost = r.NoCost, NoPrice = r.NoPrice, NowCost = r.NowCost, NowSell = r.NowSell,
        Fact = r.Fact, Kids = r.Kids, Work = r.Work,
        Supplier = r.Supplier,
    };

    private static CostMissingPrice SummaryFlag(string key) => key switch
    {
        "noprice" => CostMissingPrice.MaterialPrice,
        "nocost" => CostMissingPrice.MaterialCost,
        "nowsell" => CostMissingPrice.WorkSell,
        "nowcost" => CostMissingPrice.WorkCost,
        _ => CostMissingPrice.None,
    };

    private List<SummaryRec> SortRecs(List<SummaryRec> list)
    {
        if (_summarySort == "kp") return list; // already in document order, callers only read
        IEnumerable<SummaryRec> sorted = _summarySort switch
        {
            "name" => list.OrderBy(r => r.Name.ToLowerInvariant(), StringComparer.Ordinal),
            "sell" => list.OrderByDescending(r => r.Sell),
            "cost" => list.OrderByDescending(r => r.Cost),
            "margin" => list.OrderByDescending(r => r.Sell - r.Cost),
            "pct" => list.OrderBy(r => r.Pct ?? 1_000_000_000m),
            _ => list,
        };
        return sorted.ToList();
    }

    // ------------------------------------------------------------------ tiles
    private void SummaryFillTiles(decimal mCost, decimal mSell, decimal wCost, decimal wSell,
        CostSummaryData data, bool filtered, int nNocost, int shownRows, int totalRows)
    {
        var smrContract = data.SmrContract;
        SummaryTileMatValue.Text = Money.Format(mSell);
        SummaryTileMatDetail.Text = "Закупка: " + Money.Format(mCost);
        SummaryTileMat.ToolTip = $"Маржа на материалах: {Money.Format(mSell - mCost)}";
        SummaryTileWorkValue.Text = Money.Format(wSell);
        SummaryTileWorkDetail.Text = $"Закупка: {Money.Format(wCost)}\nСМР по смете: {Money.Format(smrContract)}";
        SummaryTileWork.ToolTip = $"Маржа на работах: {Money.Format(wSell - wCost)}\n" +
            $"СМР по договору (платит заказчик): {Money.Format(smrContract)}";
        SummaryTileCostValue.Text = Money.Format(mCost + wCost);
        SummaryTileSellValue.Text = Money.Format(mSell + wSell);
        SummaryTileMatMarginValue.Text = Money.Format(mSell - mCost);
        SummaryTileWorkMarginValue.Text = Money.Format(wSell - wCost);

        // примечание (cost_summary_view.show_data): что считать неполным
        var notes = new List<string>();
        if (!filtered)
        {
            if (nNocost > 0)
                notes.Insert(0, $"закуп материалов не указан у {nNocost} поз.");
            notes.AddRange(data.Notes);
            if (smrContract != 0m && Math.Abs(wSell - smrContract) >= 1m)
                notes.Add($"розница работ по позициям {Money.Format(wSell)}, а СМР по договору " +
                          $"{Money.Format(smrContract)}");
        }
        var what = filtered
            ? $"Показано по фильтру: {shownRows} поз. из {totalRows}; транспорт и прочие затраты не входят. "
            : "";
        var text = (what + (notes.Count > 0 ? "⚠ Неполно: " + string.Join("; ", notes) : "")).Trim();
        SummaryNote.Text = text;
        SummaryNote.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Refresh only the filter caption and the currently open menu. No grid rebuild.
    private void SummaryUpdateMissingCounts(CostMissingPrice missingMask)
    {
        var chosen = SummaryMissingSelected();
        // счётчики пропусков в меню (cost_summary_view._count_missing)
        foreach (var item in _summaryMissingItems)
        {
            var key = (string)item.Tag!;
            var count = _summaryMissingIndex.CountAny(SummaryFlag(key));
            item.Header = $"{SummaryMissingKinds.First(k => k.Key == key).Text}: {count}";
        }
        int missingCount = _summaryMissingIndex.CountAny(missingMask);
        if (SummaryMissingText is not null)
            SummaryMissingText.Text = chosen.Count == 1 && chosen.Contains("nocost")
                ? $"Без закупочных цен: {missingCount}"
                : $"Без цен: {missingCount}";
        // Python keeps the split filter available even at zero: the user must
        // still be able to select a different kind of missing price.
    }

    private void SummarySyncViewButtons()
    {
        if (SummaryResetViewButton is not null)
            SummaryResetViewButton.Visibility =
                _summaryMode != "kp" || _summarySort != "kp" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ toolbar events
    internal void SummarySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_costSummaryData is null || _detached) return;
        _summarySearchDirty = true;
        if (_visibleEstimateMode != "summary") return;
        if (_summarySearchDebounce is null)
        {
            _summarySearchDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _summarySearchDebounce.Tick += (_, _) =>
            {
                if (_detached || _visibleEstimateMode != "summary") { _summarySearchDebounce.Stop(); return; }
                if (_summaryLoading || _summaryBusy || HasGridEdit(SummaryGrid)) return;
                SummaryRebuild();
            };
        }
        _summarySearchDebounce.Stop();
        _summarySearchDebounce.Start();
    }

    internal void ClearSummarySearch_Click(object sender, RoutedEventArgs e)
    {
        SummarySearch.Clear();
        SummarySearch.Focus();
    }

    internal void SummaryMissing_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = SummaryMissingToggle?.IsChecked == true;
        if (_summaryMissingOn == enabled) return;
        _summaryMissingOn = enabled;
        SummaryRebuild();
    }

    internal void SummaryMissingMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SummaryMissingChrome is null) return;
        var menu = new ContextMenu { PlacementTarget = SummaryMissingChrome, Placement = PlacementMode.Bottom };
        var menuItems = new List<MenuItem>();
        _summaryMissingItems = menuItems;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_summaryMissingItems, menuItems))
                _summaryMissingItems = new List<MenuItem>();
        };
        foreach (var (key, text) in SummaryMissingKinds)
        {
            var item = new MenuItem
            {
                Header = $"{text}: {_summaryMissingIndex.CountAny(SummaryFlag(key))}", IsCheckable = true, StaysOpenOnClick = true, Tag = key,
                IsChecked = _summaryMissingChecked.Contains(key),
            };
            item.Click += (_, _) =>
            {
                var changed = item.IsChecked
                    ? _summaryMissingChecked.Add(key) : _summaryMissingChecked.Remove(key);
                if (!changed) return;
                if (_summaryMissingOn) SummaryRebuild();
                else SummaryUpdateMissingCounts(_summaryMissingChecked.Aggregate(
                    CostMissingPrice.None, (mask, kind) => mask | SummaryFlag(kind)));
            };
            menu.Items.Add(item);
            menuItems.Add(item);
        }
        menu.IsOpen = true;
    }

    internal void SummaryGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Как группировать", IsEnabled = false });
        var modes = new (string Key, string Title)[]
        {
            ("kp", "Как в КП (по системам)"),
            ("same", "Одинаковые вместе"),
            ("sup", "По поставщику"),
        };
        foreach (var (key, title) in modes)
        {
            var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = _summaryMode == key };
            item.Click += (_, _) =>
            {
                if (_summaryMode == key) return;
                _summaryMode = key;
                SummaryRebuild();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    internal void SummarySort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Как сортировать", IsEnabled = false });
        var sorts = new (string Key, string Title)[]
        {
            ("kp", "Как в КП"),
            ("name", "По названию"),
            ("sell", "По рознице — дорогие сверху"),
            ("cost", "По закупу — дорогие сверху"),
            ("margin", "По марже — больше сверху"),
            ("pct", "По марже, % — меньше сверху"),
        };
        foreach (var (key, title) in sorts)
        {
            var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = _summarySort == key };
            item.Click += (_, _) =>
            {
                if (_summarySort == key) return;
                _summarySort = key;
                SummaryRebuild();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    internal void SummaryResetView_Click(object sender, RoutedEventArgs e)
    {
        if (_summaryMode == "kp" && _summarySort == "kp") return;
        _summaryMode = "kp";
        _summarySort = "kp";
        SummaryRebuild();
    }

    // ------------------------------------------------------------------ collapse
    private bool SummaryGroupCollapsed(long systemId, string? supplierKey) =>
        supplierKey is not null ? _collapsedSummarySuppliers.Contains(supplierKey) :
        systemId != 0 && _collapsedSummarySystems.Contains(systemId);

    internal void SummaryToggleSystem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SummaryRow row } || !row.CanCollapseSystem) return;
        if (row.SupplierGroupKey is { } supplierKey)
        {
            if (!_collapsedSummarySuppliers.Add(supplierKey)) _collapsedSummarySuppliers.Remove(supplierKey);
        }
        else if (!_collapsedSummarySystems.Add(row.SystemId)) _collapsedSummarySystems.Remove(row.SystemId);
        SummaryRebuild();
        e.Handled = true;
    }

    // ------------------------------------------------------------------ editing
    /// <summary>Имя колонки «Сводной» — из SortMemberPath (шапка — визуальный объект).</summary>
    private static string SummaryColumnKey(DataGridColumn column) => column.SortMemberPath ?? "";

    internal void SummaryGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (!SummarySnapshotReady || e.Row.Item is not SummaryRow row)
        {
            e.Cancel = true;
            if (!SummarySnapshotReady) SetStatus("Обнови сводную перед редактированием.", true);
            return;
        }
        var column = SummaryColumnKey(e.Column);
        var locked = _estimate?.Locked == true;

        if (row.Kind == "total")
        {
            e.Cancel = true;
            return;
        }
        if (row.Kind == "extra")
        {
            // транспорт: только закуп/клиент (в блоках «Материалы» и «Итого»)
            if (!row.IsTransport)
            {
                e.Cancel = true;
                return;
            }
            if (column is "m_msell" or "t_sell" && locked)
            {
                SetStatus("Смета подписана — цена клиенту заблокирована", true);
                e.Cancel = true;
                return;
            }
            if (column is "m_mcost" or "m_msell" or "t_cost" or "t_sell") return;
            e.Cancel = true;
            return;
        }
        if (row.Kind == "system")
        {
            // свёрнутая группа: правятся только цены за ед. (войдут во все строки)
            if (column is "m_ucost" or "m_uprice" or "w_ucost" or "w_usell") return;
            e.Cancel = true;
            return;
        }
        // позиция / «одинаковые вместе»
        switch (column)
        {
            case "name":
            case "unit":
                return;
            case "qty":
                if (row.MemberIds.Length > 1)
                {
                    SetStatus("Здесь сумма одинаковых строк — количество правьте в обычном виде («Как в КП»)", true);
                    e.Cancel = true;
                    return;
                }
                if (locked)
                {
                    SetStatus("Смета подписана и заблокирована: цену, количество и название здесь не поменять. " +
                              "Снять блокировку — во вкладке «Смета»", true);
                    e.Cancel = true;
                }
                return;
            case "m_ucost":
            case "w_ucost":
                return;
            case "m_uprice":
            case "w_usell":
                if (row.IsWork && column == "m_uprice")
                {
                    SetStatus("Строка работ («Монтаж …»): её цену клиенту правят во вкладке «Смета»", true);
                    e.Cancel = true;
                    return;
                }
                if (locked)
                {
                    SetStatus("Смета подписана и заблокирована: цену, количество и название здесь не поменять. " +
                              "Снять блокировку — во вкладке «Смета»", true);
                    e.Cancel = true;
                }
                return;
            default:
                e.Cancel = true;
                return;
        }
    }

    internal void SummaryGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (sender is DataGrid grid) GridEditInteraction.FocusTextEditor(grid, e);
    }

    internal void SummaryGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (!SummarySnapshotReady) { e.Cancel = true; return; }
        if (e.Row.Item is not SummaryRow row) return;
        var editor = MaterialsEditorTextBox(e.EditingElement);
        if (editor is null) return;
        var column = SummaryColumnKey(e.Column);
        var text = editor.Text;
        if (text == (column switch
        {
            "name" => row.Name, "unit" => row.Unit, "qty" => row.QtyRaw,
            "m_ucost" => row.CostUnitRaw, "m_uprice" => row.PriceUnitRaw,
            "m_mcost" => row.MCostText, "m_msell" => row.MSellText,
            "w_ucost" => row.WorkCostRaw, "w_usell" => row.WorkSellRaw,
            "t_cost" => row.TotalCostText, "t_sell" => row.TotalSellText,
            _ => null
        })) return;

        // Валидация до записи (cost_summary_view._save / save_cell)
        switch (column)
        {
            case "name":
                if (text.Trim().Length == 0)
                {
                    SetStatus("Наименование не может быть пустым", true);
                    e.Cancel = true;
                    return;
                }
                break;
            case "unit":
                break;
            case "qty":
                if (text.Trim().Length == 0 || NumberText.Parse(text) is null)
                {
                    SetStatus(text.Trim().Length == 0
                        ? "Количество не может быть пустым"
                        : "Недопустимое число: " + text.Trim(), true);
                    e.Cancel = true;
                    return;
                }
                break;
            case "m_ucost":
            case "m_uprice":
            case "w_ucost":
            case "w_usell":
            case "m_mcost":
            case "m_msell":
            case "t_cost":
            case "t_sell":
                if (text.Trim().Length > 0 && NumberText.Parse(text) is null)
                {
                    SetStatus("Недопустимое число: " + text.Trim(), true);
                    e.Cancel = true;
                    return;
                }
                break;
            default:
                e.Cancel = true;
                return;
        }

        var ids = row.MemberIds.Length > 0 ? row.MemberIds.ToList() : new List<long>();
        if (ids.Count == 0 && row.PositionId > 0) ids = new List<long> { row.PositionId };
        // строка «Транспорт» правит реквизиты договора — идентификаторы позиций не нужны
        if (ids.Count == 0 && !row.IsTransport)
        {
            e.Cancel = true;
            return;
        }
        _ = SummaryCommitAsync(column, ids, text, _summaryRevision);
    }

    // All writes use the revision of the displayed snapshot, never a newly read
    // revision. Share the estimate's write queue and close/readback guard.
    private async Task RunSummaryWriteAsync(string revision,
        Func<string, Task<(string Revision, string Message)>> write)
    {
        if (!SummarySnapshotReady || revision != _summaryRevision)
        {
            SetStatus("Сводная устарела. Обнови смету перед записью.", true);
            return;
        }
        _summaryBusy = true;
        _summaryRevision = "";
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || _serverRevision != revision)
                throw new InvalidOperationException("Сводная изменилась до записи.");
            var result = await write(revision);
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
            SetStatus(result.Message + " Выполняется сверка…", false);
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Сводная: запись не подтверждена — " + ex.Message;
            SetStatus(_saveError, true);
        }
        finally
        {
            _writeLock.Release();
            _summaryBusy = false;
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    private static string RequireSummarySaved(SqlServerDatabase.SaveResult result)
    {
        if (!result.Saved) throw new InvalidOperationException(result.Error ?? "Не сохранено");
        return result.Revision;
    }

    private Task SummaryCommitAsync(string column, List<long> ids, string text, string revision) =>
        RunSummaryWriteAsync(revision, async expected =>
        {
            var field = column switch
            {
                "name" => "name", "unit" => "unit", "qty" => "qty",
                "m_ucost" => "purchase_price", "m_uprice" => "price",
                "w_ucost" => "work_cost", "w_usell" => "work_sell",
                "m_mcost" or "t_cost" => "transport_cost",
                "m_msell" or "t_sell" => "transport_amount",
                _ => throw new InvalidOperationException("Неизвестное поле сводной.")
            };
            if (field == "transport_amount")
            {
                var value = text.Trim().Length == 0 ? 0m :
                    NumberText.Parse(text) ?? throw new InvalidOperationException("Недопустимое число.");
                var result = await Task.Run(() => SqlServerCostTotals.SaveTotalsField(
                    _sqlConnectionString, _contractId, field, value, expected));
                return (RequireSummarySaved(result), "Транспорт записан.");
            }
            if (field == "transport_cost")
            {
                var result = await Task.Run(() => SqlServerCostSummary.SaveSummaryCell(
                    _sqlConnectionString, _contractId, 0, field, text.Trim(), expected));
                return (RequireSummarySaved(result), "Транспорт записан.");
            }

            var edits = ids.Select(id => (id, text.Trim())).ToArray();
            var saved = await Task.Run(() => SqlServerCostSummary.SaveSummaryCells(
                _sqlConnectionString, _contractId, edits, field, expected));
            return (RequireSummarySaved(saved), $"Записано: {ids.Count} поз.");
        });

    // ------------------------------------------------------------------ markup / СМР в смету
    internal async void SummaryMarkupAll_Click(object sender, RoutedEventArgs e)
    {
        var ids = SummaryRecs.Select(r => r.Id).ToList();
        await SummaryMarkupAsync(ids, allRows: true);
    }

    internal void SummaryGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!SummarySnapshotReady ||
            ContextMenuTarget.SelectRow<SummaryRow>(SummaryGrid, e) is not { Kind: "pos" })
        {
            e.Handled = true;
            return;
        }
        var ids = SummarySelectedIds();
        if (ids.Count == 0)
        {
            e.Handled = true;
            return;
        }
        var contractId = _contractId;
        var revision = _summaryRevision;
        var menu = new ContextMenu { PlacementTarget = SummaryGrid,
            Placement = e.CursorLeft < 0 && e.CursorTop < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint };
        // The Python action has no icon.
        var item = new MenuItem { Header = "Наценка на СМР…", IsEnabled = _estimate?.Locked == false };
        item.Click += (_, _) =>
        {
            if (SummarySnapshotReady && _estimate?.Locked == false &&
                _contractId == contractId && _summaryRevision == revision &&
                new HashSet<long>(ids).SetEquals(SummarySelectedIds()))
                _ = SummaryMarkupAsync(ids, allRows: false);
            else SetStatus("Выбор или данные изменились. Откройте меню снова.", true);
        };
        menu.Items.Add(item);
        e.Handled = true;
        menu.IsOpen = true;
    }

    private List<long> SummarySelectedIds()
    {
        var ids = new List<long>();
        var seen = new HashSet<long>();
        foreach (var row in SummaryGrid.SelectedItems.OfType<SummaryRow>())
        {
            if (row.Kind != "pos") continue;
            foreach (var id in row.MemberIds)
                if (seen.Add(id)) ids.Add(id);
        }
        return ids;
    }

    private async Task SummaryMarkupAsync(List<long> ids, bool allRows)
    {
        if (!SummarySnapshotReady) return;
        if (ids.Count == 0)
        {
            SetStatus("В смете нет позиций для наценки", true);
            return;
        }
        if (_estimate?.Locked != false)
        {
            SetStatus("Смета подписана и заблокирована.", true);
            return;
        }
        var revision = _summaryRevision;
        var scope = allRows ? "Все позиции сметы" : "Выбранные позиции";
        var dialog = new SummaryMarkupDialog($"{scope} ({ids.Count}). Цена клиенту за ед. будет пересчитана от закупа СМР.")
        { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        if (!SummarySnapshotReady || _estimate?.Locked != false || revision != _summaryRevision ||
            !new HashSet<long>(ids).SetEquals(allRows ? SummaryRecs.Select(r => r.Id) : SummarySelectedIds()))
        {
            SetStatus("Выбор или данные изменились. Откройте диалог снова.", true);
            return;
        }
        var percent = dialog.Percent;
        await RunSummaryWriteAsync(revision, async expected =>
        {
            var result = await Task.Run(() => SqlServerCostSummary.ApplySmrMarkupWithRevision(
                _sqlConnectionString, _contractId, ids, percent, expected));
            return (result.Revision, $"Наценка СМР: изменено {result.Changed}, пропущено {result.Skipped}.");
        });
    }

    internal async void SummaryTransferSmr_Click(object sender, RoutedEventArgs e)
    {
        if (!SummarySnapshotReady || _estimate?.Locked != false) return;
        var revision = _summaryRevision;
        var answer = MessageBox.Show(Window.GetWindow(this),
            "Смета перейдёт в режим «По системам». Розница СМР по всем позициям, " +
            "включая скрытые фильтром, заменит суммы СМР соответствующих систем. " +
            "Системы без рассчитанных цен сохранят свои суммы.",
            "Перенести СМР в смету?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes || revision != _summaryRevision) return;
        await RunSummaryWriteAsync(revision, async expected =>
        {
            var result = await Task.Run(() => SqlServerCostSummary.TransferSmrToSystemsWithRevision(
                _sqlConnectionString, _contractId, expected));
            return (result.Revision, $"СМР перенесены в смету: {result.Systems} систем.");
        });
    }


}

/// <summary>Диалог наценки на СМР (cost_summary_view._prompt_markup → QInputDialog.getDouble).</summary>
public sealed class SummaryMarkupDialog : Window
{
    private readonly TextBox _percent;

    public decimal Percent { get; private set; } = 50m;

    public SummaryMarkupDialog(string prompt)
    {
        Title = "Наценка на СМР";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brushes.White;
        DynamicFormLayout.Apply(this);
        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = prompt + "\nНаценка, %",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        _percent = new TextBox
        {
            Text = "50", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Right,
        };
        DynamicFormLayout.Field(_percent);
        System.Windows.Automation.AutomationProperties.SetName(_percent, "Наценка, %");
        root.Children.Add(_percent);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = new Button { Content = "Отмена", IsCancel = true };
        cancel.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogActionGap");
        var ok = new Button
        {
            Content = "Применить", IsDefault = true,
        };
        ok.Click += (_, _) => Apply();
        row.Children.Add(cancel);
        row.Children.Add(ok);
        DynamicFormLayout.StyleActions(ok, cancel);
        Content = DynamicFormLayout.WithFooter(root, row);
        Loaded += (_, _) => { _percent.Focus(); _percent.SelectAll(); };
    }

    private void Apply()
    {
        var value = NumberText.Parse(_percent.Text);
        if (value is not decimal v || v < -100m || v > 10000m)
        {
            MessageBox.Show(this, "Наценка должна быть числом от −100 до 10000 %.", "Наценка на СМР",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Percent = v;
        DialogResult = true;
    }
}
