using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// Вкладка «Итоги» себестоимости (cost_totals_view): по статьям — что стоит нам
/// и что платит клиент, маржа и её процент; правка ПНР/транспорта/расходных/
/// прочих/непредвиденных и итога СМР (в режиме «сумма», с защитой подписанного
/// и этапов). Full-KP money guards live in the core writer.
/// </summary>
public partial class CostSummaryPane
{
    public sealed class CostTotalsPreviewRow
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string Cost { get; init; } = "";
        public string Sell { get; init; } = "";
        public string Margin { get; init; } = "";
        public string MarginPct { get; init; } = "";
        public bool CostEditable { get; init; }
        public bool SellEditable { get; init; }
        public bool IsTotalsRow { get; init; }
        public bool IsBold => IsTotalsRow;
        public bool IsSubtotal => Key == "__total__";
        public bool MarginNeg { get; init; }
    }

    private CostTotalsData? _totalsData;

    /// <summary>Загрузить вкладку «Итоги» (cost_totals_view.totals).</summary>
    private void ApplyTotalsTab(CostTotalsData d)
    {
        _totalsData = d;
        var items = new ObservableCollection<CostTotalsPreviewRow>();
        string M(decimal? v) => v is null || v == 0m ? "" : Money.Format(v.Value);
        string Margin(decimal? cost, decimal? sell)
        {
            if (sell is null) return cost is null || cost == 0m ? "" : "−" + Money.Format(cost.Value);
            if (cost is null || cost == 0m) return sell == 0m ? "" : "—";
            return Money.Format(sell.Value - cost.Value);
        }
        string Pct(decimal? cost, decimal? sell) =>
            cost is null || cost == 0m || sell is null ? "" :
            ((sell.Value - cost.Value) / cost.Value * 100m).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + "%";
        bool Negative(decimal? cost, decimal? sell) =>
            sell is null ? cost is not null && cost != 0m :
            cost is not null && cost != 0m && sell.Value - cost.Value < 0;

        foreach (var line in d.Lines)
        {
            var name = line.Key == "mat" && d.NoCost > 0
                ? line.Name + $"   (закуп не указан у {d.NoCost} поз.)" : line.Name;
            items.Add(new CostTotalsPreviewRow
            {
                Key = line.Key,
                Name = name,
                Cost = M(line.Cost),
                Sell = line.Sell is null ? "—" : M(line.Sell),
                Margin = Margin(line.Cost, line.Sell),
                MarginPct = Pct(line.Cost, line.Sell),
                CostEditable = !_readOnly && line.CostEditable,
                SellEditable = !_readOnly && line.SellEditable,
                MarginNeg = Negative(line.Cost, line.Sell),
            });
        }
        var costSum = d.Lines.Sum(l => l.Cost ?? 0m);
        var sellSum = d.Lines.Sum(l => l.Sell ?? 0m);
        items.Add(new CostTotalsPreviewRow
        {
            Key = "__total__",
            Name = "ИТОГО" + (d.VatTop ? " (без НДС)" : ""),
            Cost = Money.Format(costSum),
            Sell = Money.Format(sellSum),
            Margin = Margin(costSum, sellSum),
            MarginPct = Pct(costSum, sellSum),
            IsTotalsRow = true,
            MarginNeg = Negative(costSum, sellSum),
        });
        if (d.VatTop)
        {
            items.Add(new CostTotalsPreviewRow
            {
                Key = "__vat__",
                Name = $"НДС {d.VatRate:0.##}% (сверху)",
                Sell = Money.Format(d.Vat),
            });
            items.Add(new CostTotalsPreviewRow
            {
                Key = "__total_vat__",
                Name = "ИТОГО с НДС",
                Sell = Money.Format(d.Total),
                IsTotalsRow = true,
            });
        }
        ReplaceCostRows(TotalsGrid, items);
        TotCostValue.Text = Money.Format(costSum);
        TotSellCaption.Text = d.VatTop ? "Розница — всего с НДС" : "Розница — всего";
        TotSellValue.Text = Money.Format(d.VatTop ? d.Total : sellSum);
        var margin = sellSum - costSum;
        var pct = costSum == 0m ? "" :
            (margin / costSum * 100m).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + "%";
        TotMarginValue.Foreground = new System.Windows.Media.SolidColorBrush(margin < 0m
            ? System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26)
            : System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A));
        TotMarginValue.Text = margin != 0m || sellSum != 0m
            ? Money.Format(margin) + (pct.Length > 0 ? "  ·  " + pct : "") : "—";
    }

    private static string? TotalsField(DataGridColumn column, CostTotalsPreviewRow row) =>
        column.Header?.ToString() switch
        {
            "Себестоимость (нам)" => row.CostEditable ? CostKey(row) + "_cost" : null,
            "Розница (клиенту)" => row.SellEditable && row.Key != "mat" ? CostKey(row) + "_amount" : null,
            _ => null,
        };

    private static string CostKey(CostTotalsPreviewRow row) => row.Key switch
    {
        "pnr" => "pnr",
        "trans" => "transport",
        "smr" => "smr",
        _ => row.Key,
    };

    private void TotalsGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_readOnly || !CanUseSnapshot) { e.Cancel = true; return; }
        if (e.Row.Item is not CostTotalsPreviewRow row || row.IsTotalsRow) { e.Cancel = true; return; }
        if (TotalsField(e.Column, row) is null) e.Cancel = true;
    }

    private async void TotalsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (_readOnly || !CanUseSnapshot || e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not CostTotalsPreviewRow row || row.IsTotalsRow) return;
        if (e.EditingElement is not TextBox editor) return;
        var input = editor.Text.Trim().Replace("₽", "").Trim();
        var isCost = e.Column.Header?.ToString() == "Себестоимость (нам)";
        decimal? value = null;
        if (input.Length > 0)
        {
            if (!decimal.TryParse(input.Replace(',', '.'), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var parsed))
            {
                SetStatus("Недопустимое число: " + input);
                await ReloadAsync();
                return;
            }
            value = parsed;
        }
        var key = CostKey(row);
        string field;
        if (isCost)
        {
            if (value is null) return;                       // статью расходов нельзя опустошить
            field = key + "_cost";
            if (key is "mat" or "smr") return;               // считаются из сводной
        }
        else
        {
            if (key == "smr") field = "smr_amount";          // вводится ИТОГ СМР
            else
            {
                field = key + "_amount";
                if (value is null) value = 0m;               // суммы клиенту не бывают «неизвестными»
            }
            if (key is "consumables" or "other" or "reserve") return;
        }
        using var operation = BeginCostOperation();
        if (operation is null) return;
        SetStatus("Сохранение…");
        try
        {
            var result = await Task.Run(() => SqlServerCostTotals.SaveTotalsField(
                _connectionString, _contractId, field, value, ServerRevision()));
            if (!result.Saved)
            {
                SetStatus("Ошибка: " + result.Error);
                await ReloadAsync();
                return;
            }
            _revision = result.Revision;
            SetStatus(field is "smr_amount" or "pnr_amount" or "transport_amount"
                ? "Сохранено — итог сметы тоже обновлён"
                : "Сохранено");
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
        await ReloadAsync();
    }
}
