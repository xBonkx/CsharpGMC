using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
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
/// Вкладка «Материалы» КП — перенос python\smeta\ui\cost_view.py (section 0, mat_page):
/// таблица закуп-розница по позициям с редактированием закупа/цены/наценки/факта,
/// поставщика и пометок, группировки и сортировки, плитки «Цена клиенту /
/// Себестоимость / Транспорт / Маржа». Записи идут теми же сервисами, что и вкладка
/// «Смета» (SaveCell) и «Закупки» (SetProc), массовые — SqlServerCostOperations.
/// </summary>
public partial class EstimateView
{
    // ------------------------------------------------------------------ row model
    /// <summary>Строка таблицы «Материалов» (cost_view._pos_item или шапка системы/группы).</summary>
    public sealed class MaterialsRow
    {
        public bool IsSystemHeader { get; init; }
        public bool IsGroup { get; init; }
        public bool IsSub { get; init; }
        public bool IsWork { get; init; }
        public bool HasFact { get; init; }
        public bool CostMissing { get; init; }
        public bool CostWork { get; init; }
        public bool MarkupWarn { get; init; }
        public bool NoCostFlag { get; init; }
        public bool NoPriceFlag { get; init; }
        public long SystemId { get; init; }
        public long PositionId { get; init; }
        public long[] MemberIds { get; init; } = Array.Empty<long>();
        public string Num { get; init; } = "";
        public string Name { get; init; } = "";
        public string Unit { get; init; } = "";
        public string QtyText { get; init; } = "";
        public string PriceText { get; init; } = "";
        public string PriceRaw { get; init; } = "";
        public string AmountText { get; init; } = "";
        public string CostText { get; init; } = "";
        public string CostRaw { get; init; } = "";
        public string CostSumText { get; init; } = "";
        public string MarkupText { get; init; } = "";
        public string MarkupRaw { get; init; } = "";
        public string MarginText { get; init; } = "";
        public bool MarginNegative { get; init; }
        public string FactText { get; init; } = "";
        public string FactRaw { get; init; } = "";
        public string Supplier { get; init; } = "";
        public string NoteText { get; init; } = "";
        public string RowBackground { get; init; } = "White";
        public string SystemLabel { get; init; } = "";
        public decimal? UnitCost { get; init; }
        public decimal? Plan { get; init; }
        public decimal Price { get; init; }

        public string NameTooltip => IsGroup
            ? $"{Name}\n{MemberIds.Length} одинаковых строк — цена, закуп, наценка и поставщик, вписанные сюда, встанут во все."
            : $"{Name}\n{SystemLabel}\nДвойной щелчок — поправить наименование (меняется и в смете)";
        public string PriceTooltip => "";
        public string CostTooltip => HasFact
            ? $"Фактическая цена по счёту. План был: {(Plan is decimal plan ? Money.Format(plan) : "—")}"
            : CostWork ? "Строка работ («Монтаж …») — закупать нечего"
            : CostMissing ? "Закупочная цена не указана — двойной щелчок, чтобы вписать"
            : "";
        public string MarkupTooltip => MarkupWarn ? "Необычная наценка — проверьте закупочную цену" : "";
        public string NoteTooltip => (NoteText.Length == 0 ? "" : NoteText + "\n\n") +
            "Наши пометки — двойной щелчок, чтобы вписать. Заказчик их не видит";
    }

    // ------------------------------------------------------------------ state
    private CostMaterialsData? _materialsData;
    private List<CostMaterialsPosition> _materialsTop = new();
    private Dictionary<long, CostMaterialsPosition> _materialsById = new();
    private List<MaterialsRow> _materialsBuilt = new();
    private MaterialsVisibleRows _materialsVisibleRows = new(Array.Empty<MaterialsRow>());
    private bool _materialsUpdatingSystemFilter;
    private readonly GmcSmeta.Core.Domain.MaterialsOrder _materialsOrder = new();
    private string _materialsMode = "kp";
    private string _materialsSort = "kp";
    private bool _materialsLoading;
    private int _materialsLoadVersion;
    private CancellationTokenSource? _materialsLoadCancellation;
    private bool _materialsBusy;
    private string _materialsRevision = "";
    private bool MaterialsSnapshotReady => !_materialsLoading && !_materialsBusy &&
        !string.IsNullOrWhiteSpace(_materialsRevision) && !_serverConflict &&
        System.Threading.Volatile.Read(ref _pendingWrites) == 0;

    private static readonly (string Name, string Hex)[] MatRowColors =
    {
        ("Жёлтый", "#FFF1A8"), ("Зелёный", "#D4F0C8"), ("Красный", "#F9D0D0"),
        ("Голубой", "#D2E6FF"), ("Оранжевый", "#FFDDB3"), ("Сиреневый", "#E6DAFA"),
        ("Серый", "#E3E6EA"),
    };

    private static string MatMoney(decimal v) => Money.Format(v);

    // ------------------------------------------------------------------ load
    private void CancelMaterialsRead()
    {
        // A materials mutation owns its forced readback until it completes.
        if (_materialsBusy || !_materialsLoading) return;
        ++_materialsLoadVersion;
        var cancellation = _materialsLoadCancellation;
        _materialsLoadCancellation = null;
        _materialsLoading = false;
        cancellation?.Cancel();
    }

    private async Task LoadMaterialsAsync(bool force = false)
    {
        if (_estimate is null || MaterialsPanel is null || (_detached && !_materialsBusy)) return;
        if (_materialsLoading) return;
        if (MaterialsView is null && _visibleEstimateMode != "materials") return;
        if (!force && _materialsData is not null && !string.IsNullOrWhiteSpace(_materialsRevision) &&
            _materialsRevision == _serverRevision)
        {
            // A cached read must not replace the live editor or its row.
            if (MaterialsGrid?.Items is not IEditableCollectionView { IsEditingItem: true }) MaterialsRebuild();
            return;
        }
        _materialsLoading = true;
        _materialsRevision = "";
        var version = ++_materialsLoadVersion;
        using var cancellation = new CancellationTokenSource();
        _materialsLoadCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            EnsureMaterialsView();
            var snapshot = await Task.Run(async () =>
            {
                using var conn = await SqlServerDatabase.OpenAsync(_sqlConnectionString, token).ConfigureAwait(false);
                var revision = await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false);
                var data = SqlServerCostSummary.MaterialsData(conn, _contractId, token);
                if (string.IsNullOrWhiteSpace(revision) ||
                    revision != await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false))
                    throw new InvalidOperationException("Данные изменились во время чтения материалов. Повторите обновление.");
                token.ThrowIfCancellationRequested();
                return (data, revision);
            }, token);
            if (token.IsCancellationRequested || version != _materialsLoadVersion ||
                (_detached && !_materialsBusy)) return;
            _materialsData = snapshot.data;
            MaterialsPrepareCache();
            MaterialsRebuild();
            _materialsRevision = snapshot.revision;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Navigation superseded this read, not a save failure.
        }
        catch (Exception ex)
        {
            if (version == _materialsLoadVersion)
                SetStatus("Материалы: ошибка загрузки — " + ex.Message, true);
        }
        finally
        {
            if (ReferenceEquals(_materialsLoadCancellation, cancellation))
            {
                _materialsLoadCancellation = null;
                _materialsLoading = false;
            }
        }
    }

    private void MaterialsPrepareCache()
    {
        var data = _materialsData!;
        var top = new List<CostMaterialsPosition>();
        // A reload replaces the snapshot; deleted rows must not remain rooted
        // or be found by a stale material/group lookup.
        _materialsById.Clear();
        foreach (var p in data.Positions)
            if (p.ParentId is null)
            {
                top.Add(p);
                _materialsById[p.Id] = p;
            }
        _materialsTop = top;
    }

    // ------------------------------------------------------------------ build
    private void MaterialsRebuild()
    {
        var data = _materialsData;
        if (data is null || MaterialsGrid is null) return;
        var counts = MaterialsCountTotals();
        _materialsBuilt = _materialsMode == "kp" && _materialsSort == "kp"
            ? MaterialsBuildKp(data)
            : MaterialsBuildView(data);
        _materialsVisibleRows = new MaterialsVisibleRows(_materialsBuilt);
        MaterialsFillTiles(counts);
        MaterialsSyncFilters(counts);
        MaterialsFillSystemFilter();
        MaterialsApplyViewFilter();
        MaterialsSyncViewButtons();
    }

    /// <summary>Σ плиток (cost_view._money): rev — верхние не-работы; cost — все строки с закупом
    /// (включая комплектующие); cov — верхние с закупом и ценой; пропуски для фильтров.</summary>
    private (decimal Rev, decimal Cost, decimal Cov, int Missing, int NoPrice) MaterialsCountTotals()
    {
        decimal rev = 0m, cost = 0m, cov = 0m;
        int missing = 0, noprice = 0;
        foreach (var p in _materialsData!.Positions)
        {
            var sub = p.ParentId is not null;
            var cu = p.UnitCost;
            if (!sub && !p.Work)
                rev += p.Qty * p.Price;
            if (cu is decimal unit)
                cost += unit * p.Qty;
            if (!sub && cu is not null && p.Price != 0m)
                cov += p.Qty * p.Price;
            if (!sub && cu is null && p.Price != 0m && !p.Work)
                missing++;
            if (!sub && p.Price == 0m)
                noprice++;
        }
        return (rev, cost, cov, missing, noprice);
    }

    private Dictionary<long, string> _materialsLabels = new();

    private string MaterialsSystemLabel(long systemId) =>
        _materialsLabels.GetValueOrDefault(systemId, "");

    private void MaterialsSystemLabel(long systemId, string value) =>
        _materialsLabels[systemId] = value;

    private MaterialsRow MaterialsPositionRow(CostMaterialsPosition p, string num, string? systemLabel = null)
    {
        var cu = p.UnitCost;
        var sub = p.ParentId is not null;
        var hasFact = p.Fact is not null;
        var label = systemLabel ?? MaterialsSystemLabel(p.SystemId);
        var priceText = sub ? "" : MatMoney(p.Price);
        var amount = Money.Round2(p.Qty * p.Price);
        string costText, costSumText = "", markupText = "", markupRaw = "", marginText = "";
        var costMissing = false;
        var costWork = false;
        var markupWarn = false;
        var marginNegative = false;
        if (cu is decimal unit)
        {
            costText = MatMoney(unit);
            costSumText = MatMoney(Money.Round2(unit * p.Qty));
            if (!sub && p.Price != 0m)
            {
                var mk = unit == 0m ? (decimal?)null : (p.Price / unit - 1m) * 100m;
                if (mk is decimal value)
                {
                    markupText = Math.Round(value, 0, MidpointRounding.ToEven).ToString("0", Ru);
                    markupRaw = value.ToString("0.0", Ru);
                    markupWarn = value > 300m || value < 0m;
                    var margin = Money.Round2(amount - unit * p.Qty);
                    marginText = MatMoney(margin);
                    marginNegative = margin < 0m;
                }
            }
        }
        else if (!sub && p.Price != 0m && p.Work)
        {
            costText = "работа";
            costWork = true;
        }
        else if (!sub && p.Price != 0m)
        {
            costText = "—";
            costMissing = true;
        }
        else
        {
            costText = "";
        }
        return new MaterialsRow
        {
            SystemId = p.SystemId,
            PositionId = p.Id,
            IsSub = sub,
            IsWork = p.Work,
            HasFact = hasFact,
            CostMissing = costMissing,
            CostWork = costWork,
            MarkupWarn = markupWarn,
            MarginNegative = marginNegative,
            NoCostFlag = !sub && cu is null && p.Price != 0m && !p.Work,
            NoPriceFlag = !sub && p.Price == 0m,
            Num = num,
            Name = p.Name,
            Unit = p.Unit,
            QtyText = NumberText.QtyText(p.Qty),
            PriceText = priceText,
            PriceRaw = !sub && p.Price != 0m ? NumberText.QtyText(p.Price) : "",
            AmountText = sub ? "" : MatMoney(amount),
            CostText = costText,
            CostRaw = p.Plan is decimal plan ? NumberText.QtyText(plan) : "",
            CostSumText = costSumText,
            MarkupText = markupText,
            MarkupRaw = markupRaw,
            MarginText = marginText,
            FactText = p.Fact is decimal fact ? MatMoney(fact) : "",
            FactRaw = p.Fact is decimal ? NumberText.QtyText(p.Fact!.Value) : "",
            Supplier = p.Supplier,
            NoteText = p.CostNote,
            RowBackground = string.IsNullOrWhiteSpace(p.CostColor) ? "White" : p.CostColor,
            SystemLabel = label,
            UnitCost = cu,
            Plan = p.Plan,
            Price = p.Price,
        };
    }

    private List<MaterialsRow> MaterialsBuildKp(CostMaterialsData data)
    {
        var rows = new List<MaterialsRow>();
        MaterialsComputeSystemLabels();
        var labels = _materialsLabels;
        var children = data.Positions.Where(p => p.ParentId is not null)
            .GroupBy(p => p.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        var bySystem = data.Positions.ToLookup(p => p.SystemId);
        var topBySystem = _materialsTop.ToLookup(p => p.SystemId);
        var num = 1;
        foreach (var (id, name, isSub, _) in data.Systems)
        {
            var members = topBySystem[id];
            decimal sRev = 0m, sCost = 0m, sCov = 0m;
            foreach (var p in bySystem[id])
            {
                var cu = p.UnitCost;
                if (p.ParentId is null && !p.Work)
                    sRev += p.Qty * p.Price;
                if (cu is decimal unit)
                    sCost += unit * p.Qty;
                if (p.ParentId is null && cu is not null && p.Price != 0m)
                    sCov += p.Qty * p.Price;
            }
            rows.Add(new MaterialsRow
            {
                IsSystemHeader = true,
                SystemId = id,
                Num = name != "Без системы" ? num.ToString(Ru) : "",
                Name = labels.GetValueOrDefault(id, name),
                AmountText = MatMoney(sRev),
                CostSumText = sCost != 0m ? MatMoney(Money.Round2(sCost)) : "",
                MarginText = sCost != 0m ? MatMoney(Money.Round2(sCov - sCost)) : "",
                RowBackground = isSub ? "#E8F5FC" : "#F1F4F9",
            });
            if (name != "Без системы") num++;
            foreach (var p in members)
            {
                rows.Add(MaterialsPositionRow(p, num.ToString(Ru), labels.GetValueOrDefault(p.SystemId)));
                if (children.TryGetValue(p.Id, out var kids))
                    foreach (var kid in kids)
                        rows.Add(MaterialsPositionRow(kid, ""));
                num++;
            }
        }
        return rows;
    }

    /// <summary>Подписи систем для всплывашек: «Родитель > подраздел» (System.label).</summary>
    private void MaterialsComputeSystemLabels()
    {
        var data = _materialsData!;
        var names = data.Systems.ToDictionary(s => s.Id, s => s.Name);
        _materialsLabels = new Dictionary<long, string>();
        foreach (var (id, name, isSub, parentId) in data.Systems)
            _materialsLabels[id] = isSub && parentId is long pid && names.ContainsKey(pid)
                ? $"{names[pid]} > {name}"
                : name;
    }

    private List<MaterialsRow> MaterialsBuildView(CostMaterialsData data)
    {
        var rows = new List<MaterialsRow>();
        MaterialsComputeSystemLabels();
        var top = MaterialsOrderedTop(data);
        var labels = _materialsLabels;
        if (_materialsMode == "kp")
        {
            foreach (var p in top)
                rows.Add(MaterialsPositionRow(p, "", labels.GetValueOrDefault(p.SystemId)));
            return rows;
        }
        if (_materialsMode is "sup" or "sup_same")
        {
            var groups = top.GroupBy(p => p.Supplier).ToList();
            foreach (var g in groups.OrderBy(g => g.Key == "" ? 1 : 0).ThenBy(g => g.Key, StringComparer.CurrentCulture))
            {
                var members = g.ToList();
                decimal cost = 0m;
                foreach (var p in members)
                    if (p.UnitCost is decimal unit)
                        cost += unit * p.Qty;
                rows.Add(new MaterialsRow
                {
                    IsGroup = true,
                    IsSystemHeader = true,   // жирная шапка, не редактируется
                    Name = (g.Key.Length == 0 ? "Поставщик не указан" : g.Key) + "  ·  " + members.Count + " поз.",
                    CostSumText = cost != 0m ? MatMoney(Money.Round2(cost)) : "",
                    RowBackground = g.Key.Length == 0 ? "#FEF3C7" : "#F1F4F9",
                    MemberIds = members.Select(p => p.Id).ToArray(),
                });
                if (_materialsMode == "sup")
                {
                    foreach (var p in members)
                        rows.Add(MaterialsPositionRow(p, "", labels.GetValueOrDefault(p.SystemId)));
                }
                else
                {
                    rows.AddRange(MaterialsMergedRows(members, labels));
                }
            }
            return rows;
        }
        // «Одинаковые вместе» (same): имя+ед. на одной строке, сумма количеств.
        rows.AddRange(MaterialsMergedRows(top, labels));
        return rows;
    }

    private List<MaterialsRow> MaterialsMergedRows(List<CostMaterialsPosition> top, Dictionary<long, string> labels)
    {
        var rows = new List<MaterialsRow>();
        foreach (var g in top.GroupBy(p => (Name: p.Name.Trim().ToLowerInvariant(), Unit: (p.Unit ?? "").Trim().ToLowerInvariant())))
        {
            var ps = g.ToList();
            if (ps.Count == 1)
            {
                rows.Add(MaterialsPositionRow(ps[0], "", labels.GetValueOrDefault(ps[0].SystemId)));
                continue;
            }
            var p0 = ps[0];
            decimal qty = 0m, sum = 0m, costSum = 0m, cov = 0m;
            var prices = new HashSet<decimal>();
            var costs = new HashSet<decimal?>();
            var suppliers = new HashSet<string>();
            var notes = new HashSet<string>();
            decimal? oneMarkup = null;
            var markupValues = new HashSet<decimal?>();
            foreach (var p in ps)
            {
                qty += p.Qty;
                var amount = Money.Round2(p.Qty * p.Price);
                sum += amount;
                prices.Add(Money.Round2(p.Price));
                var cu = p.UnitCost;
                costs.Add(cu is decimal unit ? Money.Round2(unit) : null);
                if (cu is decimal u)
                {
                    costSum += u * p.Qty;
                    if (p.Price != 0m) cov += amount;
                }
                suppliers.Add(p.Supplier);
                notes.Add(p.CostNote);
                markupValues.Add(cu is decimal uu && p.Price != 0m && uu != 0m
                    ? Money.Round2((p.Price / uu - 1m) * 100m) : null);
            }
            oneMarkup = markupValues.Count == 1 ? markupValues.First() : null;
            string Same(decimal? v) => v is decimal value ? MatMoney(value) : "";
            rows.Add(new MaterialsRow
            {
                IsGroup = true,
                SystemId = p0.SystemId,
                Name = p0.Name,
                Unit = p0.Unit,
                QtyText = NumberText.QtyText(qty),
                PriceText = prices.Count == 1 ? MatMoney(prices.First()) : "разные",
                PriceRaw = "",
                AmountText = MatMoney(sum),
                CostText = costs.Count == 1 ? Same(costs.First()) : "разные",
                CostRaw = "",
                CostMissing = costs.Any(c => c is null),
                CostSumText = costSum != 0m ? MatMoney(Money.Round2(costSum)) : "",
                MarkupText = oneMarkup is decimal mk
                    ? Math.Round(mk, 0, MidpointRounding.ToEven).ToString("0", Ru)
                    : markupValues.Count > 1 ? "разные" : "",
                MarkupRaw = oneMarkup is decimal m1 ? m1.ToString("0.#", Ru) : "",
                MarginText = costSum != 0m ? MatMoney(Money.Round2(cov - costSum)) : "",
                Supplier = suppliers.Count == 1 ? suppliers.First() : "разные",
                NoteText = notes.Count == 1 ? notes.First() : "разные",
                RowBackground = "#E8F0FE",
                MemberIds = ps.Select(p => p.Id).ToArray(),
                SystemLabel = labels.GetValueOrDefault(p0.SystemId),
            });
        }
        return rows;
    }

    // ------------------------------------------------------------------ sorting
    private List<CostMaterialsPosition> MaterialsOrderedTop(CostMaterialsData data) =>
        _materialsOrder.Order(_contractId, _materialsMode, _materialsSort, _materialsTop, MatRowColors);

    // ------------------------------------------------------------------ filtering
    private void MaterialsApplyViewFilter()
    {
        if (MaterialsGrid is null || _materialsUpdatingSystemFilter) return;
        var grid = MaterialsGrid;
        var result = _materialsVisibleRows.Filter(MaterialsSearch?.Text,
            MaterialsNoCostToggle?.IsChecked == true,
            MaterialsNoPriceToggle?.IsChecked == true,
            (MaterialsSystemFilter?.SelectedItem as MatSystemOption)?.Id);
        // Keep selection, viewport and edit controls for an unchanged view.
        // New authoritative rows always have different references.
        if (grid.ItemsSource is ObservableCollection<MaterialsRow> existing &&
            MaterialsVisibleRows.SameReferences(existing, result))
            return;
        grid.ItemsSource = new ObservableCollection<MaterialsRow>(result);
    }

    private void MaterialsSyncFilters((decimal Rev, decimal Cost, decimal Cov, int Missing, int NoPrice) counts)
    {
        if (MaterialsNoCostToggle is not null)
        {
            MaterialsNoCostText.Text = $"Без закупочных цен: {counts.Missing}";
            MaterialsNoCostToggle.Visibility =
                counts.Missing > 0 || MaterialsNoCostToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        if (MaterialsNoPriceToggle is not null)
        {
            MaterialsNoPriceText.Text = counts.NoPrice > 0 ? $"Без цены: {counts.NoPrice}" : "Без цены";
            MaterialsNoPriceToggle.Visibility =
                counts.NoPrice > 0 || MaterialsNoPriceToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void MaterialsFillTiles((decimal Rev, decimal Cost, decimal Cov, int Missing, int NoPrice) counts)
    {
        var data = _materialsData!;
        var rev = counts.Rev;
        var cost = counts.Cost;
        var cov = counts.Cov;
        var tAmount = data.TransportAmount;
        var tCost = data.TransportCost;
        if (tAmount != 0m)
        {
            rev += tAmount;
            if (tCost is decimal tc)
            {
                cost += tc;
                cov += tAmount;
            }
        }
        MatTileRevCaption.Text = tAmount != 0m ? "Цена клиенту (материалы + транспорт)" : "Цена клиенту (материалы)";
        MatTileRevValue.Text = MatMoney(rev);
        MatTileCostValue.Text = cost != 0m ? MatMoney(cost) : "не внесена";
        var transCaption = MatTileTransCaption ??= MatTileTrans.Template?.FindName("MatTileTransCaption", MatTileTrans) as TextBlock;
        var transValue = MatTileTransValue ??= MatTileTrans.Template?.FindName("MatTileTransValue", MatTileTrans) as TextBlock;
        var noteText = MaterialsTransportNote(data.TransportNoteJson);
        if (transCaption is not null)
            transCaption.Text = tAmount != 0m
                ? $"Транспорт · клиенту {MatMoney(tAmount)}" + (noteText.Length > 0 ? "  ·  от поставщика" : "")
                : "Транспорт · в смете нет";
        if (transValue is not null)
            transValue.Text = tCost is decimal ourCost
                ? $"нам {MatMoney(ourCost)}"
                : tAmount != 0m ? "впишите расход" : "—";
        MatTileTrans.ToolTip = tCost is decimal cost2
            ? $"Клиенту: {MatMoney(tAmount)}\nНам: {MatMoney(cost2)}\nМаржа на транспорте: {MatMoney(tAmount - cost2)}" +
              (noteText.Length > 0 ? $"\nПосчитано: {noteText}" : "") +
              "\n\nЩёлкните, чтобы поправить, сколько транспорт стоит нам"
            : "Щёлкните и впишите, сколько транспорт стоит нам. Сумма клиенту — из сметы («Транспорт» в итогах сметы)";
        var margin = cov - cost;
        MatTileMarginCaption.Text = (tAmount != 0m && tCost is not null
            ? "Маржа: материалы + транспорт" : "Маржа на материалах") + (cov == 0m ? "  ·  впишите закуп" : "");
        MatTileMarginValue.Text = cov != 0m
            ? $"{MatMoney(margin)}  ·  {(margin / cov * 100m).ToString("0.0", Ru)}%"
            : "—";
        MatTileMarginValue.ToolTip = "Маржа считается по строкам, где известна закупочная цена";
    }

    private static string MaterialsTransportNote(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            var node = System.Text.Json.JsonDocument.Parse(json).RootElement;
            if (node.ValueKind != System.Text.Json.JsonValueKind.Object) return "";
            if (!node.TryGetProperty("sup", out var sup) || sup.GetArrayLength() == 0) return "";
            var names = sup.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0);
            decimal pct = 0m, baseAmount = 0m;
            if (node.TryGetProperty("pct", out var pctEl) && pctEl.TryGetDecimal(out var p)) pct = p;
            if (node.TryGetProperty("base", out var baseEl) && baseEl.TryGetDecimal(out var b)) baseAmount = b;
            return $"{string.Join(", ", names)} — {NumberText.QtyText(pct)} % от {MatMoney(baseAmount)}";
        }
        catch (System.Text.Json.JsonException)
        {
            return "";
        }
    }

    private void MaterialsFillSystemFilter()
    {
        if (MaterialsSystemFilter is null) return;
        var selected = MaterialsSystemFilter.SelectedItem as MatSystemOption;
        var options = new List<MatSystemOption> { new(null, "Все системы") };
        foreach (var (id, name, _, _) in _materialsData!.Systems)
            options.Add(new MatSystemOption(id, _materialsLabels.GetValueOrDefault(id, name)));
        if (MaterialsSystemFilter.ItemsSource is IEnumerable<MatSystemOption> existing &&
            existing.SequenceEqual(options) && selected is not null) return;
        _materialsUpdatingSystemFilter = true;
        try
        {
            MaterialsSystemFilter.ItemsSource = options;
            MaterialsSystemFilter.SelectedItem =
                options.FirstOrDefault(o => o.Id == selected?.Id) ?? options[0];
        }
        finally { _materialsUpdatingSystemFilter = false; }
    }

    private void MaterialsSyncViewButtons()
    {
        if (MaterialsResetViewButton is not null)
            MaterialsResetViewButton.Visibility =
                _materialsMode != "kp" || _materialsSort != "kp" ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record MatSystemOption(long? Id, string Label)
    {
        public override string ToString() => Label;
    }

    // ------------------------------------------------------------------ helpers
    private bool MaterialsLocked => _estimate?.Locked != false || _materialsData?.Locked != false;

    /// <summary>Выделенные позиции (cost_view._selected_positions): строки, члены групп,
    /// целые системы (только верхние строки, вне КП).</summary>
    private List<long> MaterialsSelectedIds()
    {
        var ids = new List<long>();
        var seen = new HashSet<long>();
        void Add(long id)
        {
            if (id > 0 && seen.Add(id)) ids.Add(id);
        }
        foreach (var row in MaterialsGrid.SelectedItems.OfType<MaterialsRow>())
        {
            if (row.IsSystemHeader && !row.IsGroup)
            {
                foreach (var p in _materialsTop.Where(p => p.SystemId == row.SystemId)) Add(p.Id);
            }
            else if (row.MemberIds.Length > 0)
            {
                foreach (var id in row.MemberIds) Add(id);
            }
            else if (row.PositionId > 0 && !row.IsSub)
            {
                Add(row.PositionId);
            }
        }
        return ids;
    }

    // ------------------------------------------------------------------ editing
    private void MaterialsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (!MaterialsSnapshotReady || e.Row.Item is not MaterialsRow row)
        {
            e.Cancel = true;
            return;
        }
        var column = e.Column.Header?.ToString() ?? "";
        if (row.IsSystemHeader && !row.IsGroup)
        {
            e.Cancel = true;
            return;
        }
        var locked = MaterialsLocked;
        switch (column)
        {
            case "Наименование":
                if (row.IsSub) { e.Cancel = true; return; }
                if (locked)
                {
                    SetStatus("Смета подписана и заблокирована. Опечатку в названии — во вкладке «Смета»: «Исправить опечатку» на зелёной полосе", true);
                    e.Cancel = true;
                }
                return;
            case "Цена клиенту":
                if (row.IsSub) { e.Cancel = true; return; }
                if (locked)
                {
                    SetStatus("Смета подписана и заблокирована: цену клиенту здесь не поменять. Закуп вписывать можно", true);
                    e.Cancel = true;
                }
                return;
            case "Наценка, %":
                if (row.IsSub) { e.Cancel = true; return; }
                if (locked)
                {
                    SetStatus("Смета подписана и заблокирована: цену клиенту здесь не поменять. Закуп вписывать можно", true);
                    e.Cancel = true;
                }
                return;
        }
    }

    private void MaterialsGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (sender is DataGrid grid) GridEditInteraction.FocusTextEditor(grid, e);
    }

    private static TextBox? MaterialsEditorTextBox(DependencyObject? container) =>
        GridEditInteraction.FindTextEditor(container);

    // Текстовые блоки плитки «Транспорт» живут внутри ControlTemplate кнопки.
    private TextBlock? MatTileTransCaption;
    private TextBlock? MatTileTransValue;

    private void MaterialsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (!MaterialsSnapshotReady) { e.Cancel = true; return; }
        if (e.Row.Item is not MaterialsRow row) return;
        var editor = MaterialsEditorTextBox(e.EditingElement);
        if (editor is null) return;
        var column = e.Column.Header?.ToString() ?? "";
        var text = editor.Text;
        // Opening and leaving an unchanged cell is not a write (including mixed groups).
        if (text == (column switch
        {
            "Наименование" => row.Name,
            "Цена клиенту" => row.PriceRaw,
            "Закуп за ед." => row.CostRaw,
            "Наценка, %" => row.MarkupRaw,
            "Факт по счёту" => row.FactRaw,
            "Поставщик" => row.Supplier,
            "Пометки" => row.NoteText,
            _ => null
        })) return;
        var ids = row.IsGroup && row.MemberIds.Length > 0
            ? row.MemberIds.ToList()
            : row.PositionId > 0 ? new List<long> { row.PositionId } : new List<long>();
        if (ids.Count == 0) { e.Cancel = true; return; }

        // Валидация до записи (cost_view.save_cell). Поставщик и пометки — текст без валидации.
        switch (column)
        {
            case "Наименование":
                if (text.Trim().Length == 0)
                {
                    SetStatus("Наименование не может быть пустым", true);
                    e.Cancel = true;
                    return;
                }
                break;
            case "Цена клиенту":
            case "Закуп за ед.":
                if (text.Trim().Length > 0 && NumberText.Parse(text) is null)
                {
                    SetStatus("Недопустимое число: " + text.Trim(), true);
                    e.Cancel = true;
                    return;
                }
                break;
            case "Наценка, %":
                if (text.Trim().Length == 0) { e.Cancel = true; return; }
                var pct = NumberText.Parse(text);
                if (pct is null)
                {
                    SetStatus("Недопустимое число: " + text.Trim(), true);
                    e.Cancel = true;
                    return;
                }
                if (pct < -100m)
                {
                    SetStatus("Наценка не может быть меньше −100 %: цена ушла бы в минус", true);
                    e.Cancel = true;
                    return;
                }
                break;
            case "Факт по счёту":
                if (text.Trim().Length > 0 && NumberText.Parse(text) is null)
                {
                    SetStatus("Недопустимое число: " + text.Trim(), true);
                    e.Cancel = true;
                    return;
                }
                break;
            case "Поставщик":
            case "Пометки":
                break;
            default:
                return;
        }
        _materialsBusy = true;
        SetStatus($"materials write: ids=[{string.Join(",", ids)}] text='{text}'", false);
        _ = MaterialsCommitAsync(column, row, ids, text);
    }

    private async Task MaterialsCommitAsync(string column, MaterialsRow row, List<long> ids, string text)
    {
        try
        {
            var revision = _materialsRevision;
            if (string.IsNullOrWhiteSpace(revision))
                throw new InvalidOperationException("Снимок материалов не подтверждён.");
            _materialsRevision = "";
            switch (column)
            {
                case "Наименование":
                {
                    var name = text.Trim();
                    var edits = ids.Select(id => (id, name)).ToArray();
                    var result = await Task.Run(() => SqlServerCostSummary.SaveSummaryCells(
                        _sqlConnectionString, _contractId, edits, "name", revision));
                    if (!result.Saved)
                    {
                        SetStatus(result.Error ?? "Не сохранено", true);
                        await LoadMaterialsAsync(force: true);
                        return;
                    }
                    revision = result.Revision;
                    SetStatus(ids.Count > 1
                        ? $"Наименование поправлено у {ids.Count} строк — в смете тоже. Вернуть — Ctrl+Z"
                        : "Наименование поправлено — в смете тоже. Вернуть — Ctrl+Z", false);
                    break;
                }
                case "Цена клиенту":
                {
                    var edits = ids.Select(id => (id, text.Trim())).ToArray();
                    var result = await Task.Run(() => SqlServerCostSummary.SaveSummaryCells(
                        _sqlConnectionString, _contractId, edits, "price", revision));
                    if (!result.Saved)
                    {
                        SetStatus(result.Error ?? "Не сохранено", true);
                        await LoadMaterialsAsync(force: true);
                        return;
                    }
                    revision = result.Revision;
                    break;
                }
                case "Закуп за ед.":
                {
                    var edits = ids.Select(id => (id, text.Trim())).ToArray();
                    var result = await Task.Run(() => SqlServerCostSummary.SaveSummaryCells(
                        _sqlConnectionString, _contractId, edits, "purchase_price", revision));
                    if (!result.Saved)
                    {
                        SetStatus(result.Error ?? "Не сохранено", true);
                        await LoadMaterialsAsync(force: true);
                        return;
                    }
                    revision = result.Revision;
                    break;
                }
                case "Наценка, %":
                {
                    var pct = NumberText.Parse(text)!;
                    var edits = new List<(long PositionId, string Input)>();
                    foreach (var id in ids)
                    {
                        var position = _materialsById.GetValueOrDefault(id);
                        var cu = position?.UnitCost;
                        if (cu is not decimal unit || unit == 0m) continue;
                        var price = SqlServerCostOperations.PriceFromMarkup(unit, pct.Value, wholeRubles: false);
                        edits.Add((id, price.ToString("0.##", CultureInfo.InvariantCulture)));
                    }
                    if (edits.Count == 0)
                    {
                        SetStatus("Сначала впишите закупочную цену", true);
                        await LoadMaterialsAsync(force: true);
                        return;
                    }
                    var result = await Task.Run(() => SqlServerCostSummary.SaveSummaryCells(
                        _sqlConnectionString, _contractId, edits, "price", revision));
                    if (!result.Saved)
                    {
                        SetStatus(result.Error ?? "Не сохранено", true);
                        await LoadMaterialsAsync(force: true);
                        return;
                    }
                    revision = result.Revision;
                    break;
                }
                case "Факт по счёту":
                {
                    decimal? fact = text.Trim().Length == 0 ? null : NumberText.Parse(text);
                    var result = await Task.Run(() => SqlServerProcurement.SetProc(
                        _sqlConnectionString, _contractId, ids, null, null, null, null,
                        fact, null, null, null, null, revision, clearCostPrice: fact is null));
                    if (!result.Saved)
                    {
                        SetStatus(result.Error ?? "Не сохранено", true);
                        await LoadMaterialsAsync(force: true);
                        return;
                    }
                    revision = result.Revision;
                    break;
                }
                case "Поставщик":
                {
                    // Тот же поставщик, что на вкладке «Закупки» (procurement.set_proc).
                    var result = await Task.Run(() => SqlServerCostOperations.SetSupplierBatch(
                        _sqlConnectionString, _contractId, ids, text.Trim(), revision));
                    _serverRevision = result.Revision;
                    await LoadMaterialsAsync(force: true);
                    SetStatus(text.Trim().Length == 0
                        ? $"Поставщик убран — у {result.Changed} строк"
                        : $"Поставщик «{text.Trim()}» — у {result.Changed} строк", false);
                    return;
                }
                case "Пометки":
                {
                    var result = await Task.Run(() => SqlServerCostOperations.SetCostFormatting(
                        _sqlConnectionString, _contractId, ids, "cost_note", text.Trim(), revision));
                    _serverRevision = result.Revision;
                    await LoadMaterialsAsync(force: true);
                    return;
                }
            }
            _serverRevision = revision;
            await LoadMaterialsAsync(force: true);
            if (column is "Наименование" or "Цена клиенту" or "Закуп за ед." or "Наценка, %")
                _ = ReloadDocumentAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Материалы: ошибка сохранения — " + ex.Message, true);
            await LoadMaterialsAsync(force: true);
        }
        finally
        {
            _materialsBusy = false;
        }
    }

    // ------------------------------------------------------------------ actions
    private void MaterialsFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton)
            MaterialsApplyViewFilter();
    }

    private void MaterialsSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        MaterialsApplyViewFilter();

    private void MaterialsSystemFilter_Changed(object sender, SelectionChangedEventArgs e) =>
        MaterialsApplyViewFilter();

    private void MaterialsResetView_Click(object sender, RoutedEventArgs e) =>
        MaterialsSetView("kp", "kp");

    private void MaterialsSetView(string mode, string sort)
    {
        // Python cost_view._set_view clears frozen keys even when the same
        // sort is chosen again. Ordinary edits/rebuilds still keep row order.
        _materialsOrder.Reset();
        _materialsMode = mode;
        _materialsSort = sort;
        MaterialsRebuild();
    }

    private void MaterialsGrouping_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        void Header(string text)
        {
            var item = new MenuItem { Header = text, IsEnabled = false };
            menu.Items.Add(item);
        }
        Header("Как группировать");
        var modes = new (string Key, string Title)[]
        {
            ("kp", "Как в КП (по системам)"),
            ("same", "Одинаковые вместе"),
            ("sup", "По поставщику"),
            ("sup_same", "По поставщику, одинаковые вместе — для заявки"),
        };
        foreach (var (key, title) in modes)
        {
            var item = new MenuItem
            {
                Header = title,
                IsCheckable = true,
                IsChecked = _materialsMode == key,
            };
            item.Click += (_, _) => MaterialsSetView(key, _materialsSort);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var reset = ContextMenuIcons.Create("Вернуть как в КП", "X");
        reset.Click += (_, _) => MaterialsSetView("kp", "kp");
        menu.Items.Add(reset);
        NameWrapPreference.AddMenuItem(menu);
        menu.IsOpen = true;
    }

    private void MaterialsSorting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var header = new MenuItem { Header = "Как сортировать", IsEnabled = false };
        menu.Items.Add(header);
        var sorts = new (string Key, string Title)[]
        {
            ("kp", "Как в КП"),
            ("name", "По названию"),
            ("cost_up", "По закупу — дешёвые сверху"),
            ("cost_down", "По закупу — дорогие сверху"),
            ("costsum", "По сумме закупа"),
            ("markup", "По наценке"),
            ("color", "По цвету — цветные сверху"),
        };
        foreach (var (key, title) in sorts)
        {
            var item = new MenuItem
            {
                Header = title,
                IsCheckable = true,
                IsChecked = _materialsSort == key,
            };
            item.Click += (_, _) => MaterialsSetView(_materialsMode, key);
            menu.Items.Add(item);
        }
        var hint = new MenuItem
        {
            Header = "Сортировка — один раз, как в Excel: поправленная строка остаётся на месте. Пересортировать — выбрать сортировку снова",
            IsEnabled = false,
        };
        menu.Items.Add(hint);
        menu.IsOpen = true;
    }

    private void MaterialsColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !MaterialsSnapshotReady) return;
        var ids = MaterialsSelectedIds().OrderBy(id => id).ToArray();
        if (ids.Length == 0) return;
        var revision = _materialsRevision;
        var contractId = _contractId;
        bool CanExecute() => MaterialsSnapshotReady && contractId == _contractId &&
            revision == _materialsRevision &&
            MaterialsSelectedIds().OrderBy(id => id).SequenceEqual(ids);
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        FillMaterialsColorItems(menu.Items, CanExecute);
        menu.IsOpen = true;
    }

    /// <summary>
    /// ПКМ по «Материалам» — те же пять действий, что в эталоне (cost_view._menu): команды
    /// тулбара, поставщик для выделенных и подменю «Цвет строки». Без выделения меню нет.
    /// </summary>
    private void MaterialsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var cell = ContextMenuTarget.TargetCell(MaterialsGrid, e);
        if (!MaterialsSnapshotReady || ContextMenuTarget.SelectRow<MaterialsRow>(MaterialsGrid, e) is null)
        {
            e.Handled = true;
            return;
        }
        var ids = MaterialsSelectedIds();
        var clearField = cell.IsValid ? MaterialsClearField(cell.Column?.Header?.ToString()) : null;
        var clearIds = clearField is null ? Array.Empty<long>() : MaterialsClearIds(clearField.Value.Field);
        if (ids.Count == 0 && clearIds.Length == 0)
        {
            e.Handled = true;
            return;
        }
        var menu = new ContextMenu { PlacementTarget = MaterialsGrid,
            Placement = e.CursorLeft < 0 && e.CursorTop < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint };
        var selectedIds = ids.OrderBy(id => id).ToArray();
        var contractId = _contractId;
        var revision = _materialsRevision;
        bool CanExecute() => MaterialsSnapshotReady && contractId == _contractId && revision == _materialsRevision &&
            MaterialsSelectedIds().OrderBy(id => id).SequenceEqual(selectedIds);
        void Add(string header, RoutedEventHandler handler, string glyph)
        {
            var item = ContextMenuIcons.Create(header, glyph);
            item.Click += (sender, args) =>
            {
                if (CanExecute()) handler(sender, args);
                else SetStatus("Выбор или данные изменились. Откройте меню снова.", true);
            };
            menu.Items.Add(item);
        }
        if (clearField is { } clear && clearIds.Length > 0 &&
            MaterialsGrid.SelectedItems.Contains(cell.Item))
        {
            var item = ContextMenuIcons.Create("Очистить " + clear.Label, "X",
                !MaterialsLocked || clear.Field is not ("price" or "cost_price"), "Backspace");
            item.Click += async (_, _) => await ClearMaterialsAsync(clear.Field, clear.Label, clearIds, revision);
            menu.Items.Add(item);
        }
        if (ids.Count > 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            Add("Наценка для выделенных…", MaterialsMarkup_Click, "List");
            Add("Подставить закуп из справочника (где пусто)", MaterialsCatalog_Click, "Book");
            Add(ids.Count > 1 ? $"Поставщик для выделенных… ({ids.Count})" : "Поставщик для выделенных…",
                MaterialsSupplier_Click, "Building");
            Add("Заявка поставщику…", MaterialsRequest_Click, "File");
            menu.Items.Add(new Separator());
            var colors = ContextMenuIcons.Create("Цвет строки", "Edit");
            FillMaterialsColorItems(colors.Items, CanExecute);
            menu.Items.Add(colors);
        }
        e.Handled = true;
        menu.IsOpen = true;
    }

    private static (string Field, string Label)? MaterialsClearField(string? header) => header switch
    {
        "Цена клиенту" => ("price", "цену клиенту"),
        "Закуп за ед." => ("purchase_price", "закуп"),
        "Факт по счёту" => ("cost_price", "факт по счёту"),
        "Поставщик" => ("supplier", "поставщика"),
        "Пометки" => ("cost_note", "пометку"),
        _ => null
    };

    private long[] MaterialsClearIds(string field) => MaterialsGrid.SelectedItems.OfType<MaterialsRow>()
        .Where(row => !row.IsSystemHeader || row.IsGroup)
        .Where(row => field != "price" || !row.IsSub && !row.IsWork)
        .SelectMany(row => row.IsGroup ? row.MemberIds : new[] { row.PositionId })
        .Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();

    private async void MaterialsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Back or Key.Delete) || Keyboard.Modifiers != ModifierKeys.None ||
            e.OriginalSource is TextBox || e.OriginalSource is ComboBox) return;
        var field = MaterialsClearField(MaterialsGrid.CurrentColumn?.Header?.ToString());
        if (field is null) return;
        e.Handled = true;
        await ClearMaterialsAsync(field.Value.Field, field.Value.Label,
            MaterialsClearIds(field.Value.Field), _materialsRevision);
    }

    private async Task ClearMaterialsAsync(string field, string label, long[] ids, string revision)
    {
        if (!MaterialsSnapshotReady || revision != _materialsRevision || ids.Length == 0 ||
            !MaterialsClearIds(field).SequenceEqual(ids) || (field is "price" or "cost_price") && MaterialsLocked ||
            !MaterialsGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !MaterialsGrid.CommitEdit(DataGridEditingUnit.Row, true) || !MaterialsSnapshotReady ||
            revision != _materialsRevision || !MaterialsClearIds(field).SequenceEqual(ids))
        {
            SetStatus("Очистка недоступна: проверьте выделение, блокировку и обновите материалы.", true);
            return;
        }
        _materialsBusy = true;
        try
        {
            if (MessageBox.Show(Window.GetWindow(this), $"Очистить {label} у {ids.Length} позиций?",
                    "Очистка материалов", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (_materialsRevision != revision || !MaterialsClearIds(field).SequenceEqual(ids) ||
                (field is "price" or "cost_price") && MaterialsLocked) return;
            _materialsRevision = "";
            var result = await Task.Run(() => SqlServerCostOperations.ClearMaterialCells(
                _sqlConnectionString, _contractId, ids, field, revision));
            SetStatus($"Очищено: {result.Changed}; пропущено: {result.Skipped}.", false);
            await LoadMaterialsAsync(force: true);
            _ = ReloadDocumentAsync();
        }
        catch (Exception ex)
        {
            _materialsRevision = "";
            SetStatus("Очистка не подтверждена: " + ex.Message + ". Проверьте данные перед повтором.", true);
            await LoadMaterialsAsync(force: true);
        }
        finally { _materialsBusy = false; }
    }

    /// <summary>Палитра строк — общая для кнопки «Цвет» и ПКМ (cost_view._color_menu).</summary>
    private void FillMaterialsColorItems(ItemCollection items, Func<bool>? canExecute = null)
    {
        foreach (var (name, hex) in MatRowColors)
        {
            var item = new MenuItem { Header = name };
            item.Icon = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            };
            var color = hex;
            item.Click += (_, _) => { if (canExecute?.Invoke() != false) MaterialsSetColor(color); };
            items.Add(item);
        }
        items.Add(new Separator());
        var none = ContextMenuIcons.Create("Без цвета", "X");
        none.Click += (_, _) => { if (canExecute?.Invoke() != false) MaterialsSetColor(""); };
        items.Add(none);
    }

    /// <summary>Поставщик для выделенных строк (cost_view.set_supplier_selected).</summary>
    private async void MaterialsSupplier_Click(object sender, RoutedEventArgs e)
    {
        if (!MaterialsSnapshotReady) return;
        var ids = MaterialsSelectedIds();
        if (ids.Count == 0)
        {
            SetStatus("Выделите строки (можно систему или группу)", true);
            return;
        }
        var current = (MaterialsGrid.SelectedItem as MaterialsRow)?.Supplier ?? "";
        if (current == "разные") current = "";
        var dialog = new PromptDialog("Поставщик",
            $"Поставщик для {ids.Count} строк (можно ввести нового):", current)
        { Owner = Window.GetWindow(this) };
        var selectedRevision = _materialsRevision;
        _materialsBusy = true;
        try
        {
            if (dialog.ShowDialog() != true) return;
            if (_materialsRevision != selectedRevision ||
                !MaterialsSelectedIds().OrderBy(id => id).SequenceEqual(ids.OrderBy(id => id)))
            {
                SetStatus("Данные или выбор изменились. Повторите действие.", true);
                return;
            }
            var revision = _materialsRevision;
            if (string.IsNullOrWhiteSpace(revision))
                throw new InvalidOperationException("Снимок материалов не подтверждён.");
            _materialsRevision = "";
            var result = await Task.Run(() => SqlServerCostOperations.SetSupplierBatch(
                _sqlConnectionString, _contractId, ids, dialog.Value, revision));
            _serverRevision = result.Revision;
            SetStatus(dialog.Value.Length == 0
                ? $"Поставщик убран — у {result.Changed} строк"
                : $"Поставщик «{dialog.Value}» — у {result.Changed} строк", false);
            await LoadMaterialsAsync(force: true);
        }
        catch (Exception ex)
        {
            SetStatus("Поставщик не сохранён: " + ex.Message, true);
            await LoadMaterialsAsync(force: true);
        }
        finally
        {
            _materialsBusy = false;
        }
    }

    private async void MaterialsSetColor(string color)
    {
        if (!MaterialsSnapshotReady) return;
        var ids = MaterialsSelectedIds();
        if (ids.Count == 0)
        {
            SetStatus("Выделите строки, которые покрасить (можно систему или группу)", true);
            return;
        }
        _materialsBusy = true;
        _materialsBusy = true;
        try
        {
            var revision = _materialsRevision;
            if (string.IsNullOrWhiteSpace(revision))
                throw new InvalidOperationException("Снимок материалов не подтверждён.");
            _materialsRevision = "";
            var result = await Task.Run(() => SqlServerCostOperations.SetCostFormatting(
                _sqlConnectionString, _contractId, ids, "cost_color", color, revision));
            _serverRevision = result.Revision;
            SetStatus(color.Length == 0
                ? $"Цвет снят: {ids.Count} строк"
                : $"Покрашено строк: {ids.Count}", false);
            await LoadMaterialsAsync(force: true);
        }
        catch (Exception ex)
        {
            SetStatus("Материалы: цвет не сохранён — " + ex.Message, true);
            await LoadMaterialsAsync(force: true);
        }
        finally { _materialsBusy = false; }
    }

    private async void MaterialsCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (!MaterialsSnapshotReady || MaterialsLocked) return;
        _materialsBusy = true;
        try
        {
            var revision = _materialsRevision;
            if (string.IsNullOrWhiteSpace(revision))
                throw new InvalidOperationException("Снимок материалов не подтверждён.");
            _materialsRevision = "";
            var result = await Task.Run(() => SqlServerCostOperations.FillPurchaseFromCatalog(
                _sqlConnectionString, _contractId, revision));
            _serverRevision = result.Revision;
            SetStatus(result.Changed > 0
                ? $"Закуп подставлен из справочника: {result.Changed} поз."
                : "В справочнике нет закупочных цен для пустых строк", false);
            await LoadMaterialsAsync(force: true);
            if (result.Changed > 0) _ = ReloadDocumentAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Материалы: справочник — " + ex.Message, true);
            await LoadMaterialsAsync(force: true);
        }
        finally
        {
            _materialsBusy = false;
        }
    }

    private async void MaterialsMarkup_Click(object sender, RoutedEventArgs e)
    {
        if (!MaterialsSnapshotReady) return;
        var ids = MaterialsSelectedIds();
        if (ids.Count == 0)
        {
            SetStatus("Выделите строки (можно целую систему)", true);
            return;
        }
        if (MaterialsLocked)
        {
            SetStatus("Смета подписана и заблокирована: цену клиенту не пересчитать. Снять блокировку — во вкладке «Смета»", true);
            return;
        }
        var dialog = new MaterialsMarkupDialog(ids.Count) { Owner = Window.GetWindow(this) };
        var selectedRevision = _materialsRevision;
        _materialsBusy = true;
        try
        {
            if (dialog.ShowDialog() != true) return;
            if (_materialsRevision != selectedRevision ||
                !MaterialsSelectedIds().OrderBy(id => id).SequenceEqual(ids.OrderBy(id => id)))
            {
                SetStatus("Данные или выбор изменились. Повторите действие.", true);
                return;
            }
            var revision = _materialsRevision;
            if (string.IsNullOrWhiteSpace(revision))
                throw new InvalidOperationException("Снимок материалов не подтверждён.");
            _materialsRevision = "";
            var result = await Task.Run(() => SqlServerCostOperations.MarkupMaterials(
                _sqlConnectionString, _contractId, ids, dialog.Factor, dialog.PriceFromCost,
                dialog.OnlyEmpty, dialog.WholeRubles, revision));
            _serverRevision = result.Revision;
            SetStatus($"Пересчитано строк: {result.Changed}" +
                (result.Skipped > 0 ? $" · без закупа пропущено: {result.Skipped}" : ""), false);
            await LoadMaterialsAsync(force: true);
            if (result.Changed > 0) _ = ReloadDocumentAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Наценка не применена: " + ex.Message, true);
            await LoadMaterialsAsync(force: true);
        }
        finally
        {
            _materialsBusy = false;
        }
    }

    private async void MaterialsRequest_Click(object sender, RoutedEventArgs e)
    {
        if (!MaterialsSnapshotReady) return;
        _materialsBusy = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.Open(_sqlConnectionString);
                var revision = SqlServerDatabase.Revision(conn);
                var rows = SqlServerProcurement.BuyRows(_sqlConnectionString, _contractId);
                var children = ProcurementRequestPlanner.ChildPositionIds(_sqlConnectionString, _contractId);
                var info = ProcurementRequestPlanner.ReadDocumentInfo(_sqlConnectionString, _contractId);
                var number = ProcurementRequestPlanner.NextNumber(_sqlConnectionString);
                if (string.IsNullOrWhiteSpace(revision) || revision != SqlServerDatabase.Revision(conn))
                    throw new InvalidOperationException("Закупки изменились при чтении. Повторите формирование заявки.");
                return (revision, rows, children, info, number);
            });
            var selectedSupplier = (MaterialsGrid.SelectedItem as MaterialsRow)?.Supplier ?? "";
            var supplier = selectedSupplier.Length > 0 ? selectedSupplier : "";
            var dialog = new ProcurementRequestDialog(
                snapshot.info, snapshot.rows, snapshot.children,
                snapshot.number, supplier)
            { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || !dialog.FileSaved)
            {
                SetStatus("Формирование заявки отменено.", false);
                return;
            }
            if (dialog.MarkedIds.Count > 0)
            {
                var saved = await Task.Run(() => SqlServerProcurement.SetProc(
                    _sqlConnectionString, _contractId, dialog.MarkedIds, null, "ordered",
                    $"Заявка №{(string.IsNullOrWhiteSpace(dialog.RequestNumber) ? "б/н" : dialog.RequestNumber)}",
                    null, null, null, null, null, null, snapshot.revision));
                if (!saved.Saved)
                {
                    SetStatus("Excel сохранён, но строки НЕ отмечены заказанными: " + saved.Error, true);
                    await LoadMaterialsAsync(force: true);
                    return;
                }
                _serverRevision = saved.Revision;
                SetStatus($"Заявка сохранена; отмечено «Заказано»: {dialog.MarkedIds.Count} позиций.", false);
            }
            else
                SetStatus("Заявка Excel сохранена без изменения статусов закупок.", false);
            await LoadMaterialsAsync(force: true);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка заявки: " + ex.Message, true);
        }
        finally
        {
            _materialsBusy = false;
        }
    }

    private void MaterialsExport_Click(object sender, RoutedEventArgs e)
    {
        if (MaterialsGrid?.ItemsSource is not ObservableCollection<MaterialsRow> rows) return;
        var objectName = _estimate?.ObjectName ?? "";
        var title = _estimate?.Title ?? "";
        XlsxExport.Save(Window.GetWindow(this),
            $"Закуп-розница {objectName} {title}".Trim(), sheet =>
            {
                XlsxExport.Header(sheet, 1, new[]
                {
                    "№", "Наименование", "Ед.", "Кол-во", "Цена клиенту", "Сумма клиенту",
                    "Закуп за ед.", "Закуп сумма", "Наценка, %", "Маржа", "Факт по счёту",
                    "Поставщик", "Пометки",
                });
                var index = 2;
                foreach (var r in rows)
                {
                    sheet.Cell(index, 1).Value = r.Num;
                    sheet.Cell(index, 2).Value = r.Name;
                    sheet.Cell(index, 3).Value = r.Unit;
                    sheet.Cell(index, 4).Value = r.QtyText;
                    sheet.Cell(index, 5).Value = r.PriceText;
                    sheet.Cell(index, 6).Value = r.AmountText;
                    sheet.Cell(index, 7).Value = r.CostText;
                    sheet.Cell(index, 8).Value = r.CostSumText;
                    sheet.Cell(index, 9).Value = r.MarkupText;
                    sheet.Cell(index, 10).Value = r.MarginText;
                    sheet.Cell(index, 11).Value = r.FactText;
                    sheet.Cell(index, 12).Value = r.Supplier;
                    sheet.Cell(index, 13).Value = r.NoteText;
                    if (r.IsSystemHeader)
                        sheet.Row(index).Style.Font.Bold = true;
                    index++;
                }
                sheet.SheetView.FreezeRows(1);
            });
    }

    private async void MatTileTrans_Click(object sender, RoutedEventArgs e)
    {
        if (!MaterialsSnapshotReady) return;
        var data = _materialsData;
        if (data is null) return;
        var revision = _materialsRevision;
        _materialsBusy = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.Open(_sqlConnectionString);
                if (revision != SqlServerDatabase.Revision(conn))
                    throw new InvalidOperationException("Материалы изменились. Обновите таблицу.");
                var current = SqlServerCostOperations.ReadTransport(_sqlConnectionString, _contractId);
                var suppliers = SqlServerCostOperations.SupplierCosts(_sqlConnectionString, _contractId);
                if (revision != SqlServerDatabase.Revision(conn))
                    throw new InvalidOperationException("Материалы изменились при расчёте транспорта.");
                return (current, suppliers);
            });
            var dialog = new CostTransportDialog(snapshot.current, snapshot.suppliers, data.Locked)
            { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true || revision != _materialsRevision) return;
            _materialsRevision = "";
            var result = await Task.Run(() => SqlServerCostOperations.SaveTransport(
                _sqlConnectionString, _contractId, dialog.OurCost, dialog.CustomerAmount,
                dialog.CalculationNote, revision));
            if (!result.Saved)
            {
                SetStatus("Транспорт: " + result.Error, true);
                await LoadMaterialsAsync(force: true);
                return;
            }
            _serverRevision = result.Revision;
            SetStatus("Транспорт сохранён" + (data.Locked ? "" : " — итог сметы тоже обновлён"), false);
            await LoadMaterialsAsync(force: true);
            _ = ReloadDocumentAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Транспорт: " + ex.Message, true);
            await LoadMaterialsAsync(force: true);
        }
        finally
        {
            _materialsBusy = false;
        }
    }

    private void MaterialsHintClose_Click(object sender, RoutedEventArgs e)
    {
        _uiSettings.MaterialsHintHidden = true;
        _uiSettings.Save();
        MaterialsHintBar.Visibility = Visibility.Collapsed;
    }
}
