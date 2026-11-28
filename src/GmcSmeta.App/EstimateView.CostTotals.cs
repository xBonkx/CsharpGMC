using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// Вкладка «Итоги» КП — перенос python\smeta\ui\cost_totals_view.py: итоговые цифры
/// документа перед выдачей. Статьи: Материалы и СМР (монтаж) — из строк (как «Сводная»),
/// ПНР/Транспортные/Расходные материалы/Прочие расходы/Непредвиденные — реквизиты
/// договора; расходные/прочие/непредвиденные входят в себестоимость и уменьшают маржу.
/// «Маржа, %» = (розница − себестоимость) / себестоимость × 100; при нулевой
/// себестоимости процент не показывается. Для «НДС сверху» — строки «НДС X% (сверху)»
/// и «ИТОГО с НДС». Вписываемые ячейки подсвечены #FFF7E6; записи через
/// SqlServerCostTotals.SaveTotalsField (смр_amount вводится как ИТОГ СМР).
/// </summary>
public partial class EstimateView
{
    // ------------------------------------------------------------------ model
    /// <summary>Строка «Итогов» — статья или итог (cost_totals_view._show).</summary>
    public sealed class TotalsRow
    {
        public string Kind { get; init; } = "line";   // line | total | vat | grand
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string NameTip { get; init; } = "";
        public string CostText { get; init; } = "";
        public string SellText { get; init; } = "";
        public string MarginText { get; init; } = "";
        public string PctText { get; init; } = "";
        public bool MarginNeg { get; init; }
        public bool CostEditable { get; init; }
        public bool SellEditable { get; init; }
        public string CostRaw { get; init; } = "";
        public string SellRaw { get; init; } = "";
        public string SellTip { get; init; } = "";
        public bool IsSubtotal => Kind == "total";
        public bool IsBold => Kind is "total" or "grand";
        public bool CanEdit(string column) => Kind == "line" && (column switch
        {
            "cost" => CostEditable,
            "sell" => SellEditable,
            _ => false,
        });

    }

    // ------------------------------------------------------------------ state
    private CostTotalsData? _costTotalsData;
    private CostSummaryData? _totalsSourceSummary;
    private string _totalsRevision = "";
    private bool TotalsSnapshotReady => !_detached && !_totalsLoading && !_totalsBusy &&
        !_serverConflict && !HasUnsettledCacheWrites &&
        !string.IsNullOrWhiteSpace(_totalsRevision) && _totalsRevision == _serverRevision;
    private bool _totalsLoading;
    private bool _totalsBusy;
    private int _totalsLoadVersion;
    private CancellationTokenSource? _totalsLoadCancellation;

    // ------------------------------------------------------------------ lazy UI
    private EstimateTotalsPanel? TotalsView => TotalsPanel?.Child as EstimateTotalsPanel;
    private DataGrid? TotalsGrid => TotalsView?.TotalsGrid;

    private EstimateTotalsPanel EnsureTotalsView()
    {
        if (TotalsView is { } existing) return existing;
        using var timing = GmcSmeta.Core.StartupTrace.Measure("estimate_lazy_totals");
        var view = new EstimateTotalsPanel();
        view.TotalsGrid.BeginningEdit += TotalsGrid_BeginningEdit;
        view.TotalsGrid.PreparingCellForEdit += TotalsGrid_PreparingCellForEdit;
        view.TotalsGrid.CellEditEnding += TotalsGrid_CellEditEnding;
        view.TotalsGrid.LayoutUpdated += (_, _) => SyncTotalsEditAvailability();
        TotalsPanel.Child = view;
        SyncTotalsEditAvailability();
        return view;
    }

    // The row eligibility is authoritative; transient load/write/conflict state
    // must not advertise an editor that BeginningEdit would reject.
    private void SyncTotalsEditAvailability()
    {
        if (TotalsGrid is { } grid && !Equals(grid.Tag, TotalsSnapshotReady))
            grid.Tag = TotalsSnapshotReady;
    }

    // ------------------------------------------------------------------ load
    private void SupersedeTotalsRead(bool cancel)
    {
        ++_totalsLoadVersion;
        var cancellation = _totalsLoadCancellation;
        _totalsLoadCancellation = null;
        _totalsLoading = false;
        SyncTotalsEditAvailability();
        if (cancel) cancellation?.Cancel();
    }

    private void CancelTotalsRead()
    {
        // Navigation must not cancel the forced readback of an active mutation.
        if (_totalsLoading && !_totalsBusy) SupersedeTotalsRead(cancel: true);
    }

    private void InvalidateCostTotals()
    {
        // An authoritative estimate supersedes the old projection. If a
        // mutation owns the read, let it finish without publishing stale data.
        SupersedeTotalsRead(cancel: !_totalsBusy);
        _costTotalsData = null;
        _totalsSourceSummary = null;
        _totalsRevision = "";
        SyncTotalsEditAvailability();
        if (TotalsGrid is { } grid) grid.ItemsSource = null;
    }

    private async Task LoadCostTotalsAsync(bool force = false)
    {
        if (_estimate is null || TotalsPanel is null ||
            (_detached && !(force && _totalsBusy))) return;
        if (_totalsLoading) return;
        // Refreshes and cache checks must not instantiate an unopened tab.
        if (TotalsView is null && _visibleEstimateMode != "totals") return;
        if (!force && _costTotalsData is not null && TotalsSnapshotReady) return;
        var cachedSummary = !force && SummarySnapshotReady && !HasUnsettledCacheWrites
            ? _costSummaryData : null;
        var cachedRevision = cachedSummary is null ? "" : _summaryRevision;
        _totalsLoading = true;
        _totalsRevision = "";
        SyncTotalsEditAvailability();
        var version = ++_totalsLoadVersion;
        using var cancellation = new CancellationTokenSource();
        _totalsLoadCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            EnsureTotalsView();
            var snapshot = await Task.Run(() => CostFinancialSnapshot.ReadTotalsAsync(
                _sqlConnectionString, _contractId, cachedSummary, cachedRevision, token), token);
            if (token.IsCancellationRequested || version != _totalsLoadVersion ||
                (_detached && !(force && _totalsBusy))) return;
            _costTotalsData = snapshot.Totals;
            _totalsSourceSummary = snapshot.Summary;
            TotalsShow();
            _totalsRevision = snapshot.Revision;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Cancelled read is not a failed save.
        }
        catch (Exception ex)
        {
            if (version == _totalsLoadVersion)
                SetStatus("Итоги: ошибка загрузки — " + ex.Message, true);
        }
        finally
        {
            if (ReferenceEquals(_totalsLoadCancellation, cancellation))
            {
                _totalsLoadCancellation = null;
                _totalsLoading = false;
                SyncTotalsEditAvailability();
            }
        }
    }

    // ------------------------------------------------------------------ show
    private static string TotalsMoney(decimal? value) => GmcSmeta.Core.Domain.TotalsDisplayPolicy.MoneyText(value);

    private static string TotalsPct(decimal cost, decimal sell) => GmcSmeta.Core.Domain.TotalsDisplayPolicy.PercentText(cost, sell);

    private static (string MarginText, string PctText, bool Neg) TotalsMargin(decimal? cost, decimal? sell) =>
        GmcSmeta.Core.Domain.TotalsDisplayPolicy.Margin(cost, sell);

    private void TotalsShow()
    {
        var d = _costTotalsData;
        var view = TotalsView;
        if (d is null || view is null) return;
        var rows = new List<TotalsRow>();
        decimal costSum = 0m, sellSum = 0m;

        foreach (var line in d.Lines)
        {
            var nameTip = line.Key == "mat" && d.NoCost > 0
                ? line.Name + $"   (закуп не указан у {d.NoCost} поз.)" : line.Name;
            var sellTip = line.Key == "smr"
                ? line.SellEditable
                    ? "Итог СМР с учётом строк монтажа"
                    : "Ручной ввод доступен в режиме «Суммой, ₽» для незаблокированной сметы " +
                      "с самостоятельным итогом СМР"
                : "";
            var (marginText, pctText, neg) = TotalsMargin(line.Cost, line.Sell);
            rows.Add(new TotalsRow
            {
                Kind = "line", Key = line.Key, Name = line.Name, NameTip = nameTip,
                CostText = TotalsMoney(line.Cost),
                CostRaw = line.CostEditable && line.Cost is decimal c ? NumberText.QtyText(c) : "",
                SellText = GmcSmeta.Core.Domain.TotalsDisplayPolicy.SellText(line.Sell),
                SellRaw = line.SellEditable && line.Sell is decimal s ? NumberText.QtyText(s) : "",
                SellTip = sellTip,
                MarginText = marginText, PctText = pctText, MarginNeg = neg,
                CostEditable = line.CostEditable, SellEditable = line.SellEditable,
            });
            costSum += line.Cost ?? 0m;
            sellSum += line.Sell ?? 0m;
        }

        // ИТОГО (+ строки НДС сверху для «ДНС» и им подобных)
        var (totalMargin, totalPct, totalNeg) = TotalsMargin(costSum, sellSum);
        rows.Add(new TotalsRow
        {
            Kind = "total", Key = "total",
            Name = "ИТОГО" + (d.VatTop ? " (без НДС)" : ""),
            CostText = Money.Format(costSum), SellText = Money.Format(sellSum),
            MarginText = totalMargin, PctText = totalPct, MarginNeg = totalNeg,
        });
        if (d.VatTop)
        {
            rows.Add(new TotalsRow
            {
                Kind = "vat", Key = "vat",
                Name = $"НДС {d.VatRate:0.##}% (сверху)", SellText = Money.Format(d.Vat),
            });
            rows.Add(new TotalsRow
            {
                Kind = "grand", Key = "grand", Name = "ИТОГО с НДС", SellText = Money.Format(d.Total),
            });
        }
        view.TotalsGrid.ItemsSource = new ObservableCollection<TotalsRow>(rows);

        // плитки (cost_totals_view._show)
        view.TotalsTileCostValue.Text = Money.Format(costSum);
        view.TotalsTileSellCaption.Text = d.VatTop ? "Розница — всего с НДС" : "Розница — всего";
        view.TotalsTileSellValue.Text = Money.Format(d.VatTop ? d.Total : sellSum);
        var marg = sellSum - costSum;
        var pct = TotalsPct(costSum, sellSum);
        view.TotalsTileMarginValue.Text = pct.Length > 0
            ? $"{Money.Format(marg)}  ·  {pct}"
            : sellSum != 0m ? Money.Format(marg) : "—";
        view.TotalsTileMarginValue.Foreground = marg < 0m
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DC2626"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F172A"));
    }

    // ------------------------------------------------------------------ editing
    private void TotalsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (!TotalsSnapshotReady || e.Row.Item is not TotalsRow row)
        {
            e.Cancel = true;
            if (!TotalsSnapshotReady) SetStatus("Обнови итоги перед редактированием.", true);
            return;
        }
        var column = e.Column.SortMemberPath ?? "";
        if (row.CanEdit(column)) return;
        if (column == "sell" && (row.Key is "pnr" or "trans" or "smr"))
            SetStatus("Ручной ввод этой статьи сейчас недоступен", true);
        e.Cancel = true;
    }

    private void TotalsGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (sender is DataGrid grid) GridEditInteraction.FocusTextEditor(grid, e);
    }

    private void TotalsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (!TotalsSnapshotReady) { e.Cancel = true; return; }
        if (e.Row.Item is not TotalsRow row) return;
        var editor = MaterialsEditorTextBox(e.EditingElement);
        if (editor is null) return;
        var column = e.Column.SortMemberPath ?? "";
        var text = editor.Text.Trim().Replace("₽", "").Trim();
        if (!row.CanEdit(column)) { e.Cancel = true; return; }
        if (editor.Text == (column == "cost" ? row.CostRaw : row.SellRaw)) return;

        var field = column == "cost"
            ? row.Key switch
            {
                "pnr" => "pnr_cost",
                "trans" => "transport_cost",
                "consumables" => "consumables_cost",
                "other" => "other_cost",
                "reserve" => "reserve_cost",
                _ => "",
            }
            : row.Key switch
            {
                "smr" => "smr_amount",
                "pnr" => "pnr_amount",
                "trans" => "transport_amount",
                _ => "",
            };
        if (field.Length == 0) return;

        var value = text.Length == 0 ? (decimal?)null : NumberText.Parse(text);
        if (text.Length > 0 && value is null)
        {
            SetStatus("Недопустимое число: " + text, true);
            e.Cancel = true;
            return;
        }
        // суммы клиенту не бывают «неизвестны» (cost_totals_view._changed)
        if (value is null && field is "pnr_amount" or "transport_amount")
            value = 0m;
        _totalsBusy = true;
        SyncTotalsEditAvailability();
        _ = TotalsCommitAsync(field, value, _totalsRevision);
    }

    private async Task TotalsCommitAsync(string field, decimal? value, string revision)
    {
        try
        {
            if (_serverConflict)
            {
                SetStatus("Сначала обнови смету: данные на сервере изменились.", true);
                return;
            }
            if (!await SaveTotalsFieldAsync(field, value, "Итог", revision))
            {
                await LoadCostTotalsAsync(force: true);
                return;
            }
            await LoadCostTotalsAsync(force: true);
        }
        catch (Exception ex)
        {
            SetStatus("Итоги: ошибка сохранения — " + ex.Message, true);
            await LoadCostTotalsAsync(force: true);
        }
        finally
        {
            _totalsBusy = false;
            SyncTotalsEditAvailability();
        }
    }
}
