using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class CostSummaryPresentationTests
{
    private static CostSummaryRow Position(long id, long system, decimal unitCost, decimal unitSell,
        decimal workCost = 0m, decimal workSell = 0m, bool noCost = false) =>
        new(id, system, "Позиция " + id, "шт", 2m, "Поставщик",
            unitCost * 2m, unitSell * 2m, workCost * 2m, workSell * 2m,
            noCost, false, false, false, unitCost, unitSell,
            workCost == 0m ? null : workCost, workSell == 0m ? null : workSell,
            false, null, false, false);

    private static CostSummaryData Fixture() => new(
        new List<(long Id, string Label, bool IsSub)> { (1, "П1", false), (2, "П2", false) },
        new [] { Position(11, 1, 120m, 100m, noCost:true), Position(12, 1, 10m, 20m, 20m, 30m),
                 Position(21, 2, 5m, 7m) },
        new List<(string Name, decimal Cost, decimal Sell, string Kind)> {
            ("Транспорт", 15m, 25m, "trans"), ("Монтаж дополнительно", 30m, 0m, "works") },
        new List<string>(), 0m);

    [Fact]
    public void Build_AggregatesSystemsExtrasAndGrandTotalExactlyOnce()
    {
        var data = Fixture();
        var rows = CostSummaryPresentation.Build(data);
        Assert.Equal(2, rows.Count(r => r.IsSystem));
        Assert.Equal(3, rows.Count(r => r.IsPosition));
        Assert.Equal(2, rows.Count(r => r.IsExtra));
        var group = Assert.Single(rows.Where(r => r.IsSystem && r.SystemId == 1));
        Assert.Equal(2, group.PositionCount);
        Assert.Equal(260m, group.MatCost);
        Assert.Equal(240m, group.MatSell);
        Assert.Equal(-20m, group.MatMargin);
        var total = Assert.Single(rows.Where(r => r.IsTotal));
        Assert.Equal(data.TotalCost, total.TotalCost);
        Assert.Equal(data.TotalSell, total.TotalSell);
        Assert.Equal(data.TotalSell - data.TotalCost, total.TotalMargin);
        Assert.Equal(285m, total.MatCost); // 260 + 10 + transport 15. 
    }

    [Fact]
    public void Filter_ProvidesSystemSearchMissingAndFoldingWithoutChangingTotals()
    {
        var all = CostSummaryPresentation.Build(Fixture());
        var folded = CostSummaryPresentation.Filter(all, null, null, false, new HashSet<long>{1});
        Assert.Contains(folded, x => x.IsSystem && x.SystemId == 1 && x.IsCollapsed);
        Assert.DoesNotContain(folded, x => x.IsPosition && x.SystemId == 1);
        var search = CostSummaryPresentation.Filter(all, null, "Позиция 12", false, new HashSet<long>{1});
        Assert.Contains(search, x => x.IsPosition && x.PositionId == 12);
        Assert.DoesNotContain(search, x => x.IsPosition && x.PositionId == 11);
        var onlySystem = CostSummaryPresentation.Filter(all, 2, null, false);
        Assert.Single(onlySystem.Where(x => x.IsPosition));
        Assert.DoesNotContain(onlySystem, x => x.IsTotal || x.IsExtra);
        var missing = CostSummaryPresentation.Filter(all, null, null, true);
        Assert.Contains(missing, x => x.IsPosition && x.PositionId == 11);
        Assert.DoesNotContain(missing, x => x.IsPosition && x.PositionId == 12);
        Assert.Single(all.Where(x => x.IsTotal)); // Original totals remain unchanged.
    }
}
