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
        public bool CanCollapseSystem => IsSystemHeader && SystemId != 0;
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
    private List<CostSummaryRow> _summaryRecs = new();
    private Dictionary<long, string> _summaryLabels = new();
    private string _summaryMode = "kp";
    private string _summarySort = "kp";
    private bool _summaryMissingOn;
    private readonly HashSet<long> _collapsedSummarySystems = new();
    private bool _summaryBusy;
    private bool _summaryLoading;
    private int _summaryLoadVersion;
    private string _summaryRevision = "";
    private bool SummarySnapshotReady => !_detached && !_summaryLoading && !_summaryBusy &&
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
    private void InvalidateCostSummary()
    {
        _summaryLoadVersion++;
        _summaryRevision = "";
        _costSummaryData = null;
        _summaryRecs = new();
        _summaryLoading = false;
        if (SummaryGrid is not null) SummaryGrid.ItemsSource = null;
    }

    private async Task LoadCostSummaryAsync(bool force = false)
    {
        if (_estimate is null || SummaryGrid is null) return;
        if (_summaryLoading) return;
        if (!force && _costSummaryData is not null && !string.IsNullOrWhiteSpace(_summaryRevision) &&
            _summaryRevision == _serverRevision)
        {
            SummaryRebuild();
            return;
        }
        _summaryLoading = true;
        _summaryRevision = "";
        var version = ++_summaryLoadVersion;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.Open(_sqlConnectionString);
                var before = SqlServerDatabase.Revision(conn);
                var data = SqlServerCostSummary.Summary(conn, _contractId);
                if (string.IsNullOrWhiteSpace(before) || before != SqlServerDatabase.Revision(conn))
                    throw new InvalidOperationException("Данные изменились во время чтения сводной. Повтори обновление.");
                return (Data: data, Revision: before);
            });
            if (version != _summaryLoadVersion || _detached) return;
            _costSummaryData = snapshot.Data;
            _summaryRecs = snapshot.Data.Rows.ToList();
            if (SummaryTransferSmrButton is not null)
                SummaryTransferSmrButton.IsEnabled = !_estimate.Locked;
            if (SummaryExcelButton is not null) SummaryExcelButton.IsEnabled = true;
            SummaryComputeLabels();
            SummaryFillSystemFilter();
            SummaryRebuild();
            _summaryRevision = snapshot.Revision;
        }
        catch (Exception ex)
        {
            if (version == _summaryLoadVersion)
                SetStatus("Сводная: ошибка загрузки — " + ex.Message, true);
        }
        finally
        {
            if (version == _summaryLoadVersion) _summaryLoading = false;
        }
    }

    private sealed record SummarySystemOption(long? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private void SummaryFillSystemFilter()
    {
        if (SummarySystemFilter is null) return;
        var selected = SummarySystemFilter.SelectedItem as SummarySystemOption;
        var options = new List<SummarySystemOption> { new(null, "Все системы") };
        foreach (var (id, name, _) in _costSummaryData!.Systems)
            options.Add(new SummarySystemOption(id, _summaryLabels.GetValueOrDefault(id, name)));
        SummarySystemFilter.ItemsSource = options;
        SummarySystemFilter.SelectedItem =
            options.FirstOrDefault(o => o.Id == selected?.Id) ?? options[0];
    }

    /// <summary>Подписи систем: подраздел — «Родитель › подраздел» (System.label эталона);
    /// подсистемы в эталоне идут сразу за родителем.</summary>
    private void SummaryComputeLabels()
    {
        var systems = _costSummaryData!.Systems;
        _summaryLabels = new Dictionary<long, string>();
        // в SQL-выборке нет parent_id: составные подписи строим по порядку,
        // как их отдаёт Core (имя системы целиком, для подразделов — имя как есть)
        foreach (var (id, name, _) in systems)
            _summaryLabels[id] = name;
    }

    // ------------------------------------------------------------------ build
    private void SummaryRebuild()
    {
        var data = _costSummaryData;
        if (data is null || SummaryGrid is null) return;
        var recs = _summaryRecs;
        var chosen = SummaryMissingSelected();
        // пометки «—» на пропусках: nocost — всегда, остальные — при включённом фильтре
        var markKinds = _summaryMissingOn ? chosen : new HashSet<string>();

        // счётчики пропусков в меню (cost_summary_view._count_missing)
        foreach (var item in _summaryMissingItems)
        {
            var key = (string)item.Tag!;
            var count = recs.Count(r => SummaryFlag(r, key));
            item.Header = $"{SummaryMissingKinds.First(k => k.Key == key).Text}: {count}";
        }
        int missingCount = recs.Count(r => chosen.Any(k => SummaryFlag(r, k)));
        if (SummaryMissingText is not null)
            SummaryMissingText.Text = chosen.Count == 1 && chosen.Contains("nocost")
                ? $"Без закупочных цен: {missingCount}"
                : $"Без цен: {missingCount}";
        // Python keeps the split filter available even at zero: the user must
        // still be able to select a different kind of missing price.

        // фильтр (show_data): системы, «без цен», поиск
        var words = (SummarySearch?.Text ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant()).ToArray();
        long? systemId = (SummarySystemFilter?.SelectedItem as SummarySystemOption)?.Id;
        var rows = new List<SummaryRec>();
        foreach (var r in recs)
        {
            if (systemId is long sid && r.SystemId != sid) continue;
            if (_summaryMissingOn && !chosen.Any(k => SummaryFlag(r, k))) continue;
            if (words.Length > 0)
            {
                var hay = $"{r.Name} {_summaryLabels.GetValueOrDefault(r.SystemId, "")} {r.Supplier}".ToLowerInvariant();
                if (!words.All(w => hay.Contains(w))) continue;
            }
            rows.Add(ToRec(r));
        }
        bool filtered = rows.Count != recs.Count;

        var built = new List<SummaryRow>();
        decimal totMCost = 0m, totMSell = 0m, totWCost = 0m, totWSell = 0m;
        int stripe = 0;

        void AddStripe(SummaryRow row)
        {
            built.Add(row with { IsOdd = stripe % 2 == 1 });
            stripe++;
        }

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
                        Work = r.Work, Supplier = "",
                    };
                    map[key] = m;
                    merged.Add(m);
                }
                MergeInto(m, r);
            }
            var list = SortRecs(merged);
            foreach (var m in list)
                AddStripe(SummaryRowFromRec(m, kinds: markKinds));
            totMCost = list.Sum(x => x.MCost); totMSell = list.Sum(x => x.MSell);
            totWCost = list.Sum(x => x.WCost); totWSell = list.Sum(x => x.WSell);
        }
        else
        {
            // группы: по системам или по поставщикам; свёрнутая группа тоже редактируется
            var groups = new List<(string Title, long SystemId, List<SummaryRec> Items)>();
            if (_summaryMode == "sup")
            {
                foreach (var g in rows.GroupBy(r => r.Supplier).OrderBy(g => g.Key.Length == 0 ? 1 : 0)
                         .ThenBy(g => g.Key.Length == 0 ? "" : g.Key.ToLowerInvariant(), StringComparer.Ordinal))
                    groups.Add((g.Key.Length == 0 ? "Поставщик не указан" : $"Поставщик: {g.Key}",
                        0, g.ToList()));
            }
            else
            {
                foreach (var (id, label, _) in data.Systems)
                {
                    var items = rows.Where(r => r.SystemId == id).ToList();
                    if (items.Count > 0)
                        groups.Add((_summaryLabels.GetValueOrDefault(id, label), id, items));
                }
            }
            foreach (var (title, sysId, items) in groups)
            {
                var sorted = SortRecs(items);
                built.Add(SummaryRowFromGroup(sysId, title, sorted, chosen));
                // Сворачивание скрывает только детали, не исключает систему из итогов.
                totMCost += sorted.Sum(x => x.MCost); totMSell += sorted.Sum(x => x.MSell);
                totWCost += sorted.Sum(x => x.WCost); totWSell += sorted.Sum(x => x.WSell);
                if (sysId != 0 && _collapsedSummarySystems.Contains(sysId))
                    continue;
                foreach (var r in sorted)
                    AddStripe(SummaryRowFromRec(r, kinds: markKinds) with { IsIndented = true });
            }
        }

        // extra: прочие затраты на монтаж / куплено вне КП / транспорт — только без фильтра
        if (!filtered)
        {
            foreach (var (name, cost, sell, kind) in data.Extra)
            {
                if (kind == "works")
                {
                    totWCost += cost; totWSell += sell;
                    AddStripe(new SummaryRow
                    {
                        Kind = "extra", Name = name,
                        WCostText = SumText(cost), WSellText = SumText(sell),
                        TotalCostText = SumText(cost), TotalSellText = SumText(sell),
                    });
                }
                else if (kind == "trans")
                {
                    // транспорт: закуп и розница на стороне материалов, правится вручную
                    totMCost += cost; totMSell += sell;
                    AddStripe(new SummaryRow
                    {
                        Kind = "extra", IsTransport = true, Name = name,
                        MCostText = SumText(cost), MSellText = SumText(sell),
                        TotalCostText = SumText(cost), TotalSellText = SumText(sell),
                    });
                }
                else
                {
                    totMCost += cost; totMSell += sell;
                    AddStripe(new SummaryRow
                    {
                        Kind = "extra", Name = name,
                        MCostText = SumText(cost), MSellText = SumText(sell),
                        TotalCostText = SumText(cost), TotalSellText = SumText(sell),
                    });
                }
            }
        }

        // ИТОГО
        built.Add(new SummaryRow
        {
            Kind = "total",
            Name = "ИТОГО" + (filtered ? " (по фильтру)" : ""),
            MCostText = SumText(totMCost), MSellText = SumText(totMSell),
            WCostText = SumText(totWCost), WSellText = SumText(totWSell),
            TotalCostText = SumText(totMCost + totWCost), TotalSellText = SumText(totMSell + totWSell),
            MatMarginText = MarginText(totMCost, totMSell),
            WorkMarginText = MarginText(totWCost, totWSell),
            MatMarginNeg = MarginNeg(totMCost, totMSell),
            WorkMarginNeg = MarginNeg(totWCost, totWSell),
        });

        SummaryGrid.ItemsSource = new ObservableCollection<SummaryRow>(built);
        SummarySyncViewButtons();
        SummaryFillTiles(totMCost, totMSell, totWCost, totWSell, data, filtered,
            nNocost: recs.Count(r => r.NoCost), shownRows: rows.Count, totalRows: recs.Count);
    }

    private void MergeInto(SummaryRec m, SummaryRec r)
    {
        m.Ids.AddRange(r.Ids);
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
        if (r.Supplier.Length > 0)
        {
            var sups = m.Supplier.Length == 0
                ? new HashSet<string>() : new HashSet<string>(m.Supplier.Split(", "));
            sups.Add(r.Supplier);
            m.Supplier = string.Join(", ", sups.OrderBy(s => s, StringComparer.Ordinal));
        }
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

    private static bool SummaryFlag(CostSummaryRow r, string key) => key switch
    {
        "noprice" => r.NoPrice,
        "nocost" => r.NoCost,
        "nowsell" => r.NowSell,
        "nowcost" => r.NowCost,
        _ => false,
    };

    private List<SummaryRec> SortRecs(List<SummaryRec> list)
    {
        if (_summarySort == "kp") return list.ToList();
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

    // ------------------------------------------------------------------ row factory
    private static string SumText(decimal v) => Math.Abs(v) >= 0.005m ? Money.Format(v) : "";

    /// <summary>Маржа блока: «—», когда нет ни закупа, ни розницы (cost_summary_view._money_item).</summary>
    private static string MarginText(decimal cost, decimal sell) =>
        cost != 0m || sell != 0m ? Money.Format(Money.Round2(sell - cost)) : "—";

    private static bool MarginNeg(decimal cost, decimal sell) =>
        (cost != 0m || sell != 0m) && sell - cost < -0.005m;

    private static string UnitText(decimal? value, bool diff, bool isPrice) =>
        diff || value is null ? "" : (value.Value != 0m || !isPrice ? Money.Format(value.Value) : "");

    private static string UnitRaw(decimal? value, bool diff, bool isPrice) =>
        diff || value is null ? "" : (value.Value != 0m || !isPrice ? NumberText.QtyText(value.Value) : "");

    private SummaryRow SummaryRowFromRec(SummaryRec r, HashSet<string> kinds)
    {
        var markup = "";
        if (r.UWcost is { } wc && wc != 0m && r.UWsell is { } ws)
            markup = ((ws - wc) / wc * 100m).ToString("0.00", Ru).Replace('.', ',');
        var costTip = r.Fact
            ? "Закуп — факт по счёту (из закупок). Вписанное здесь станет плановым закупом"
            : r.Kids ? "Закуп строки + её комплектующие (в «Материалы: закуп»)" : "";
        var costText = r.NoCost ? "—" : UnitText(r.UCost, r.UCostDiff, isPrice: false);
        var priceText = UnitText(r.UPrice, r.UPriceDiff, isPrice: true);
        if (kinds.Contains("noprice") && r.NoPrice && priceText.Length == 0) priceText = "—";
        var workCostText = UnitText(r.UWcost, r.UWcostDiff, isPrice: false);
        if (kinds.Contains("nowcost") && r.NowCost && workCostText.Length == 0) workCostText = "—";
        var workSellText = UnitText(r.UWsell, r.UWsellDiff, isPrice: true);
        if (kinds.Contains("nowsell") && r.NowSell && workSellText.Length == 0) workSellText = "—";
        return new SummaryRow
        {
            Kind = "pos",
            SystemId = r.SystemId,
            PositionId = r.Ids.Count == 1 ? r.Ids[0] : 0,
            MemberIds = r.Ids.ToArray(),
            IsWork = r.Work,
            IsFact = r.Fact,
            HasKids = r.Kids,
            Name = r.Name,
            Unit = r.Unit,
            QtyText = NumberText.QtyText(r.Qty),
            QtyRaw = r.Ids.Count == 1 ? NumberText.QtyText(r.Qty) : "",
            CostUnitText = costText,
            CostUnitRaw = UnitRaw(r.UCost, r.UCostDiff, isPrice: false),
            CostUnitTip = costTip,
            PriceUnitText = priceText,
            PriceUnitRaw = UnitRaw(r.UPrice, r.UPriceDiff, isPrice: true),
            PriceUnitTip = r.Work ? "Строка работ: цена клиенту — это цена работы, её правят во вкладке «Смета»" : "",
            WorkCostText = workCostText,
            WorkCostRaw = UnitRaw(r.UWcost, r.UWcostDiff, isPrice: false),
            WorkSellText = workSellText,
            WorkSellRaw = UnitRaw(r.UWsell, r.UWsellDiff, isPrice: true),
            MCostText = SumText(r.MCost), MSellText = SumText(r.MSell),
            WCostText = SumText(r.WCost), WSellText = SumText(r.WSell),
            TotalCostText = SumText(r.Cost), TotalSellText = SumText(r.Sell),
            MatMarginText = MarginText(r.MCost, r.MSell),
            WorkMarginText = MarginText(r.WCost, r.WSell),
            MatMarginNeg = MarginNeg(r.MCost, r.MSell),
            WorkMarginNeg = MarginNeg(r.WCost, r.WSell),
            MarkupText = markup,
            NoCost = r.NoCost, NoPrice = r.NoPrice, NowCost = r.NowCost, NowSell = r.NowSell,
            CostMark = r.NoCost,
            PriceMark = kinds.Contains("noprice") && r.NoPrice && UnitText(r.UPrice, r.UPriceDiff, true).Length == 0,
            WorkCostMark = kinds.Contains("nowcost") && r.NowCost && UnitText(r.UWcost, r.UWcostDiff, false).Length == 0,
            WorkSellMark = kinds.Contains("nowsell") && r.NowSell && UnitText(r.UWsell, r.UWsellDiff, true).Length == 0,
        };
    }

    /// <summary>Шапка системы / поставщика: суммы группы и цены, вписываемые во все строки.</summary>
    private SummaryRow SummaryRowFromGroup(long systemId, string title, List<SummaryRec> items, HashSet<string> chosen)
    {
        decimal mCost = items.Sum(x => x.MCost), mSell = items.Sum(x => x.MSell);
        decimal wCost = items.Sum(x => x.WCost), wSell = items.Sum(x => x.WSell);

        static (string Text, string Raw, bool Uniform) Price(
            Func<SummaryRec, decimal?> get, List<SummaryRec> list, bool isPrice)
        {
            var values = list.Select(get).ToHashSet();
            if (values.Count == 1 && values.First() is decimal v)
                return (v != 0m || !isPrice ? Money.Format(v) : "",
                        v != 0m || !isPrice ? NumberText.QtyText(v) : "", true);
            return ("", "", false);
        }

        var cost = Price(x => x.UCost, items, isPrice: false);
        var price = Price(x => x.UPrice, items, isPrice: true);
        var wcost = Price(x => x.UWcost, items, isPrice: false);
        var wsell = Price(x => x.UWsell, items, isPrice: true);
        static string Tip(string what, bool uniform, int n) =>
            $"Впишите {what} — получат все {n} строк этой группы" + (uniform ? "" : " (сейчас цены у них разные)");

        return new SummaryRow
        {
            Kind = "system",
            SystemId = systemId,
            MemberIds = items.SelectMany(x => x.Ids).ToArray(),
            Name = title,
            QtyText = $"{items.Count} поз.",
            CollapseMark = systemId != 0 && _collapsedSummarySystems.Contains(systemId) ? "▸" : "▾",
            CostUnitText = cost.Text, CostUnitRaw = cost.Raw,
            CostUnitTip = Tip("закуп материала за ед.", cost.Uniform, items.Count),
            PriceUnitText = price.Text, PriceUnitRaw = price.Raw,
            PriceUnitTip = Tip("цена клиенту за ед.", price.Uniform, items.Count),
            WorkCostText = wcost.Text, WorkCostRaw = wcost.Raw,
            WorkCostTip = Tip("затраты на работу за ед.", wcost.Uniform, items.Count),
            WorkSellText = wsell.Text, WorkSellRaw = wsell.Raw,
            WorkSellTip = Tip("розница работы за ед.", wsell.Uniform, items.Count),
            MCostText = SumText(mCost), MSellText = SumText(mSell),
            WCostText = SumText(wCost), WSellText = SumText(wSell),
            TotalCostText = SumText(mCost + wCost), TotalSellText = SumText(mSell + wSell),
            MatMarginText = MarginText(mCost, mSell),
            WorkMarginText = MarginText(wCost, wSell),
            MatMarginNeg = MarginNeg(mCost, mSell),
            WorkMarginNeg = MarginNeg(wCost, wSell),
            NoCost = items.Any(x => x.NoCost),
            NoPrice = items.Any(x => x.NoPrice),
            NowCost = items.Any(x => x.NowCost),
            NowSell = items.Any(x => x.NowSell),
        };
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

    private void SummarySyncViewButtons()
    {
        if (SummaryResetViewButton is not null)
            SummaryResetViewButton.Visibility =
                _summaryMode != "kp" || _summarySort != "kp" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ toolbar events
    private void SummaryFilter_Changed(object sender, SelectionChangedEventArgs e) => SummaryRebuild();
    private void SummarySearch_TextChanged(object sender, TextChangedEventArgs e) => SummaryRebuild();

    private void ClearSummarySearch_Click(object sender, RoutedEventArgs e)
    {
        SummarySearch.Clear();
        SummarySearch.Focus();
    }

    private void SummaryMissing_Changed(object sender, RoutedEventArgs e)
    {
        _summaryMissingOn = SummaryMissingToggle?.IsChecked == true;
        SummaryRebuild();
    }

    private void SummaryMissingMenu_Click(object sender, RoutedEventArgs e)
    {
        if (SummaryMissingChrome is null) return;
        var menu = new ContextMenu { PlacementTarget = SummaryMissingChrome, Placement = PlacementMode.Bottom };
        _summaryMissingItems = new List<MenuItem>();
        foreach (var (key, text) in SummaryMissingKinds)
        {
            var item = new MenuItem
            {
                Header = $"{text}: {_summaryRecs.Count(r => SummaryFlag(r, key))}", IsCheckable = true, StaysOpenOnClick = true, Tag = key,
                IsChecked = _summaryMissingChecked.Contains(key),
            };
            item.Click += (_, _) =>
            {
                if (item.IsChecked) _summaryMissingChecked.Add(key);
                else _summaryMissingChecked.Remove(key);
                SummaryRebuild();
            };
            menu.Items.Add(item);
            _summaryMissingItems.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SummaryGroup_Click(object sender, RoutedEventArgs e)
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
            item.Click += (_, _) => { _summaryMode = key; SummaryRebuild(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SummarySort_Click(object sender, RoutedEventArgs e)
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
            item.Click += (_, _) => { _summarySort = key; SummaryRebuild(); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SummaryResetView_Click(object sender, RoutedEventArgs e)
    {
        _summaryMode = "kp";
        _summarySort = "kp";
        SummaryRebuild();
    }

    // ------------------------------------------------------------------ collapse
    private void SummaryToggleSystem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SummaryRow row } || row.Kind != "system") return;
        if (row.SystemId != 0)
        {
            if (!_collapsedSummarySystems.Add(row.SystemId)) _collapsedSummarySystems.Remove(row.SystemId);
            SummaryRebuild();
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ editing
    /// <summary>Имя колонки «Сводной» — из SortMemberPath (шапка — визуальный объект).</summary>
    private static string SummaryColumnKey(DataGridColumn column) => column.SortMemberPath ?? "";

    private void SummaryGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
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

    private void SummaryGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        var editor = MaterialsEditorTextBox(e.EditingElement);
        if (editor is null) return;
        RoutedEventHandler onLoaded = (_, _) => { editor.Focus(); editor.SelectAll(); };
        editor.Loaded += onLoaded;
        Dispatcher.BeginInvoke(() =>
        {
            editor.Focus();
            editor.SelectAll();
            editor.Loaded -= onLoaded;
        }, DispatcherPriority.Background);
    }

    private void SummaryGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (!SummarySnapshotReady) { e.Cancel = true; return; }
        if (e.Row.Item is not SummaryRow row) return;
        var editor = MaterialsEditorTextBox(e.EditingElement);
        if (editor is null) return;
        var column = SummaryColumnKey(e.Column);
        var text = editor.Text;

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
    private async void SummaryMarkupAll_Click(object sender, RoutedEventArgs e)
    {
        var ids = _summaryRecs.Select(r => r.Id).ToList();
        await SummaryMarkupAsync(ids, allRows: true);
    }

    private void SummaryGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
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
        var item = ContextMenuIcons.Create("Наценка на СМР…", "Edit", _estimate?.Locked == false);
        item.Click += (_, _) =>
        {
            if (SummarySnapshotReady && _contractId == contractId && _summaryRevision == revision &&
                SummarySelectedIds().OrderBy(id => id).SequenceEqual(ids.OrderBy(id => id)))
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
        if (!SummarySnapshotReady || revision != _summaryRevision ||
            !(allRows ? _summaryRecs.Select(r => r.Id) : SummarySelectedIds())
                .OrderBy(id => id).SequenceEqual(ids.OrderBy(id => id))) return;
        var percent = dialog.Percent;
        await RunSummaryWriteAsync(revision, async expected =>
        {
            var result = await Task.Run(() => SqlServerCostSummary.ApplySmrMarkupWithRevision(
                _sqlConnectionString, _contractId, ids, percent, expected));
            return (result.Revision, $"Наценка СМР: изменено {result.Changed}, пропущено {result.Skipped}.");
        });
    }

    private async void SummaryTransferSmr_Click(object sender, RoutedEventArgs e)
    {
        if (!SummarySnapshotReady || _estimate?.Locked != false) return;
        var revision = _summaryRevision;
        var answer = MessageBox.Show(Window.GetWindow(this),
            "Смета перейдёт в режим «По системам». Розница СМР по всем позициям, " +
            "включая скрытые фильтром, заменит суммы СМР соответствующих систем. " +
            "Системы без рассчитанных цен сохранят свои суммы.",
            "Перенести СМР в смету?", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes || revision != _summaryRevision) return;
        await RunSummaryWriteAsync(revision, async expected =>
        {
            var result = await Task.Run(() => SqlServerCostSummary.TransferSmrToSystemsWithRevision(
                _sqlConnectionString, _contractId, expected));
            return (result.Revision, $"СМР перенесены в смету: {result.Systems} систем.");
        });
    }

    // ------------------------------------------------------------------ export
    private void SummaryExcel_Click(object sender, RoutedEventArgs e)
    {
        if (SummaryGrid?.ItemsSource is not ObservableCollection<SummaryRow> rows) return;
        XlsxExport.Save(Window.GetWindow(this),
            "Сводная — " + (_estimate?.Title ?? _contractId.ToString()), sheet =>
        {
            XlsxExport.Header(sheet, 1, new[]
            {
                "Наименование", "Ед.", "Кол-во",
                "Материалы: закуп за ед.", "Материалы: розн. за ед.",
                "Материалы: закуп", "Материалы: розница",
                "Работы: закуп за ед.", "Работы: розн. за ед.",
                "Работы: закуп", "Работы: розница",
                "Итого: закуп", "Итого: розница",
                "Маржа материалов, ₽", "Маржа работ, ₽", "СМР: наценка, %",
            });
            var index = 2;
            foreach (var r in rows)
            {
                sheet.Cell(index, 1).Value = r.Name;
                sheet.Cell(index, 2).Value = r.Unit;
                sheet.Cell(index, 3).Value = r.QtyText;
                sheet.Cell(index, 4).Value = r.CostUnitText;
                sheet.Cell(index, 5).Value = r.PriceUnitText;
                sheet.Cell(index, 6).Value = r.MCostText;
                sheet.Cell(index, 7).Value = r.MSellText;
                sheet.Cell(index, 8).Value = r.WorkCostText;
                sheet.Cell(index, 9).Value = r.WorkSellText;
                sheet.Cell(index, 10).Value = r.WCostText;
                sheet.Cell(index, 11).Value = r.WSellText;
                sheet.Cell(index, 12).Value = r.TotalCostText;
                sheet.Cell(index, 13).Value = r.TotalSellText;
                sheet.Cell(index, 14).Value = r.MatMarginText;
                sheet.Cell(index, 15).Value = r.WorkMarginText;
                sheet.Cell(index, 16).Value = r.MarkupText;
                if (r.Kind is "system" or "total")
                    sheet.Row(index).Style.Font.Bold = true;
                index++;
            }
            sheet.SheetView.FreezeRows(1);
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
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        root.Children.Add(new TextBlock
        {
            Text = prompt + "\nНаценка, %",
            TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(0, 0, 0, 10),
        });
        _percent = new TextBox
        {
            Text = "50", Width = 120, HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Right,
        };
        root.Children.Add(_percent);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(12, 6, 12, 6) };
        cancel.Click += (_, _) => DialogResult = false;
        var ok = new Button
        {
            Content = "OK", Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(8, 0, 0, 0), IsDefault = true,
        };
        ok.Click += (_, _) => Apply();
        row.Children.Add(cancel);
        row.Children.Add(ok);
        root.Children.Add(row);
        Content = root;
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
