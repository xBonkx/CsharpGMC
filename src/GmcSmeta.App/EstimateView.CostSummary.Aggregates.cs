using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    private static SummaryGroupAccumulator AccumulateSummaryGroup(IEnumerable<SummaryRec> rows)
    {
        var amounts = new SummaryGroupAccumulator();
        foreach (var row in rows)
            amounts.Add(row.MCost, row.MSell, row.WCost, row.WSell,
                row.UCost, row.UPrice, row.UWcost, row.UWsell,
                row.UCostDiff, row.UPriceDiff, row.UWcostDiff, row.UWsellDiff,
                row.NoCost, row.NoPrice, row.NowCost, row.NowSell);
        return amounts;
    }
}
