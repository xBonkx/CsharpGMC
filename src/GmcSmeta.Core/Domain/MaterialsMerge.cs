using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Same-material grouping and aggregates. No UI, write or database access.</summary>
public static class MaterialsMerge
{
    public sealed record Summary(decimal Qty, decimal Sum, decimal CostSum, decimal Covered,
        IReadOnlySet<decimal> Prices, IReadOnlySet<decimal?> Costs, IReadOnlySet<string> Suppliers,
        IReadOnlySet<string> Notes, decimal? SingleMarkup, IReadOnlySet<decimal?> Markups);

    public static IEnumerable<List<CostMaterialsPosition>> Groups(IEnumerable<CostMaterialsPosition> positions) =>
        positions.GroupBy(p => (Name: p.Name.Trim().ToLowerInvariant(),
            Unit: (p.Unit ?? "").Trim().ToLowerInvariant())).Select(g => g.ToList());

    public static Summary Summarize(IEnumerable<CostMaterialsPosition> positions)
    {
        decimal qty = 0m, sum = 0m, costSum = 0m, cov = 0m;
        var prices = new HashSet<decimal>();
        var costs = new HashSet<decimal?>();
        var suppliers = new HashSet<string>();
        var notes = new HashSet<string>();
        decimal? oneMarkup = null;
        var markupValues = new HashSet<decimal?>();
        foreach (var p in positions)
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
        return new(qty, sum, costSum, cov, prices, costs, suppliers, notes, oneMarkup, markupValues);
    }
}
