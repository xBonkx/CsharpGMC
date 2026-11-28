using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Raw material display amounts and group projections; formatting and WPF rows stay in the view.</summary>
public static class MaterialsProjection
{
    public sealed record PositionValues(decimal? UnitCost, decimal Amount, decimal? CostSum,
        decimal? Markup, decimal? Margin, bool MarkupWarn, bool NoCost, bool NoPrice);

    public static PositionValues Position(CostMaterialsPosition p)
    {
        var sub = p.ParentId is not null;
        var cost = p.UnitCost;
        var amount = Money.Round2(p.Qty * p.Price);
        decimal? costSum = cost is decimal unit ? Money.Round2(unit * p.Qty) : null;
        decimal? markup = !sub && p.Price != 0m && cost is decimal c && c != 0m
            ? (p.Price / c - 1m) * 100m : null;
        decimal? margin = markup is not null ? Money.Round2(amount - cost!.Value * p.Qty) : null;
        return new(cost, amount, costSum, markup, margin,
            markup is decimal value && (value > 300m || value < 0m),
            !sub && cost is null && p.Price != 0m && !p.Work, !sub && p.Price == 0m);
    }

    public static Dictionary<long, string> SystemLabels(
        IEnumerable<(long Id, string Name, bool IsSub, long? ParentId)> systems)
    {
        var list = systems.ToList();
        var names = list.ToDictionary(s => s.Id, s => s.Name);
        return list.ToDictionary(s => s.Id, s => s.IsSub && s.ParentId is long pid && names.TryGetValue(pid, out var name)
            ? name + " > " + s.Name : s.Name);
    }

    public sealed record SupplierGroup(string Supplier, List<CostMaterialsPosition> Members, decimal Cost);

    public static IEnumerable<SupplierGroup> SupplierGroups(IEnumerable<CostMaterialsPosition> top) =>
        top.GroupBy(p => p.Supplier)
            .OrderBy(g => g.Key == "" ? 1 : 0)
            .ThenBy(g => g.Key.ToLowerInvariant(), StringComparer.Ordinal)
            .Select(g => new SupplierGroup(g.Key, g.ToList(), g.Sum(p => (p.UnitCost ?? 0m) * p.Qty)));
}
