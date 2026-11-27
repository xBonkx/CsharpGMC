using GmcSmeta.Core;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    // ------------------------------------------------------------------ row factory
    private static string SumText(decimal v) => Math.Abs(v) >= 0.005m ? Money.Format(v) : "";

    private SummaryRow SummaryRowFromRec(SummaryRec r, HashSet<string> kinds, bool odd, bool indented = false)
    {
        var markup = "";
        if (r.UWcost is { } wc && wc != 0m && r.UWsell is { } ws)
            markup = ((ws - wc) / wc * 100m).ToString("0.00", Ru).Replace('.', ',');
        var costTip = r.Fact
            ? "Закуп — факт по счёту (из закупок). Вписанное здесь станет плановым закупом"
            : r.Kids ? "Закуп строки + её комплектующие (в «Материалы: закуп»)" : "";
        var cost = SummaryMoneyDisplay.Unit(r.UCost, r.UCostDiff, isPrice: false, forceMissing: r.NoCost);
        var price = SummaryMoneyDisplay.Unit(r.UPrice, r.UPriceDiff, isPrice: true,
            markMissing: kinds.Contains("noprice") && r.NoPrice);
        var workCost = SummaryMoneyDisplay.Unit(r.UWcost, r.UWcostDiff, isPrice: false,
            markMissing: kinds.Contains("nowcost") && r.NowCost);
        var workSell = SummaryMoneyDisplay.Unit(r.UWsell, r.UWsellDiff, isPrice: false,
            markMissing: kinds.Contains("nowsell") && r.NowSell);
        var qty = NumberText.QtyText(r.Qty);
        var matMargin = SummaryMoneyDisplay.Margin(r.MCost, r.MSell);
        var workMargin = SummaryMoneyDisplay.Margin(r.WCost, r.WSell);
        return new SummaryRow
        {
            Kind = "pos", IsOdd = odd, IsIndented = indented,
            SystemId = r.SystemId,
            PositionId = r.Ids.Count == 1 ? r.Ids[0] : 0,
            MemberIds = r.Ids.ToArray(),
            IsWork = r.Work,
            IsFact = r.Fact,
            HasKids = r.Kids,
            Name = r.Name,
            Unit = r.Unit,
            QtyText = qty,
            QtyRaw = r.Ids.Count == 1 ? qty : "",
            CostUnitText = cost.Text,
            CostUnitRaw = cost.Raw,
            CostUnitTip = costTip,
            PriceUnitText = price.Text,
            PriceUnitRaw = price.Raw,
            PriceUnitTip = r.Work ? "Строка работ: цена клиенту — это цена работы, её правят во вкладке «Смета»" : "",
            WorkCostText = workCost.Text,
            WorkCostRaw = workCost.Raw,
            WorkSellText = workSell.Text,
            WorkSellRaw = workSell.Raw,
            MCostText = SumText(r.MCost), MSellText = SumText(r.MSell),
            WCostText = SumText(r.WCost), WSellText = SumText(r.WSell),
            TotalCostText = SumText(r.Cost), TotalSellText = SumText(r.Sell),
            MatLegacyMargin = r.MSell - r.MCost, WorkLegacyMargin = r.WSell - r.WCost,
            MatMarginText = matMargin.Text,
            WorkMarginText = workMargin.Text,
            MatMarginNeg = matMargin.Negative,
            WorkMarginNeg = workMargin.Negative,
            MarkupText = markup,
            NoCost = r.NoCost, NoPrice = r.NoPrice, NowCost = r.NowCost, NowSell = r.NowSell,
            CostMark = cost.Marked,
            PriceMark = price.Marked,
            WorkCostMark = workCost.Marked,
            WorkSellMark = workSell.Marked,
        };
    }

    /// <summary>Шапка системы / поставщика: суммы группы и цены, вписываемые во все строки.</summary>
    private SummaryRow SummaryRowFromGroup(long systemId, string? supplierKey, string title,
        List<SummaryRec> items, SummaryGroupAccumulator amounts)
    {
        var mCost = amounts.MaterialCost;
        var mSell = amounts.MaterialSell;
        var wCost = amounts.WorkCost;
        var wSell = amounts.WorkSell;
        var matMargin = SummaryMoneyDisplay.Margin(mCost, mSell);
        var workMargin = SummaryMoneyDisplay.Margin(wCost, wSell);

        static (string Text, string Raw, bool Uniform) Price(SummaryGroupAccumulator.UniformPrice price)
        {
            if (price.Value is decimal v)
                return (v != 0m ? Money.Format(v) : "", NumberText.QtyText(v), true);
            return ("", "", price.IsUniform);
        }

        var cost = Price(amounts.MaterialUnitCost);
        var price = Price(amounts.MaterialUnitSell);
        var wcost = Price(amounts.WorkUnitCost);
        var wsell = Price(amounts.WorkUnitSell);
        static string Tip(string what, bool uniform, int n) =>
            $"Впишите {what} — получат все {n} строк этой группы" + (uniform ? "" : " (сейчас цены у них разные)");

        return new SummaryRow
        {
            Kind = "system",
            SystemId = systemId,
            SupplierGroupKey = supplierKey,
            MemberIds = items.SelectMany(x => x.Ids).ToArray(),
            Name = title,
            QtyText = $"{items.Count} поз.",
            CollapseMark = SummaryGroupCollapsed(systemId, supplierKey) ? "▸" : "▾",
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
            MatLegacyMargin = mSell - mCost, WorkLegacyMargin = wSell - wCost,
            MatMarginText = matMargin.Text,
            WorkMarginText = workMargin.Text,
            MatMarginNeg = matMargin.Negative,
            WorkMarginNeg = workMargin.Negative,
            NoCost = amounts.NoCost,
            NoPrice = amounts.NoPrice,
            NowCost = amounts.NowCost,
            NowSell = amounts.NowSell,
        };
    }
}
