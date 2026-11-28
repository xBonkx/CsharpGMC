using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

public static class MaterialsTileTotals
{
    /// <summary>Σ плиток (cost_view._money): rev — верхние не-работы; cost — все строки с закупом
    /// (включая комплектующие); cov — верхние с закупом и ценой; пропуски для фильтров.</summary>
    public static (decimal Rev, decimal Cost, decimal Cov, int Missing, int NoPrice) Count(IEnumerable<CostMaterialsPosition> positions)
    {
        decimal rev = 0m, cost = 0m, cov = 0m;
        int missing = 0, noprice = 0;
        foreach (var p in positions)
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

    public static (decimal Rev, decimal Cost, decimal Cov) IncludeTransport(
        (decimal Rev, decimal Cost, decimal Cov, int Missing, int NoPrice) counts,
        decimal tAmount, decimal? tCost)
    {
        var rev = counts.Rev;
        var cost = counts.Cost;
        var cov = counts.Cov;
        if (tAmount != 0m)
        {
            rev += tAmount;
            if (tCost is decimal tc)
            {
                cost += tc;
                cov += tAmount;
            }
        }
        return (rev, cost, cov);
    }
}
