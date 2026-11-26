using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class CostMissingPriceIndexTests
{
    private static CostSummaryRow Row(long id, int mask) =>
        new(id, 1, "Позиция", "шт", 1m, "", 0m, 0m, 0m, 0m,
            (mask & 2) != 0, (mask & 1) != 0, (mask & 8) != 0, (mask & 4) != 0,
            null, 0m, null, null, false, null, false, false);

    [Fact]
    public void AllCombinationsMatchOriginalPredicateOn2500Rows()
    {
        var rows = Enumerable.Range(1, 2500).Select(id => Row(id, id % 16)).ToArray();
        var index = new CostMissingPriceIndex(rows);
        for (var mask = 0; mask < 16; mask++)
        {
            bool OldPredicate(CostSummaryRow r) => mask == 0 ||
                ((mask & 1) != 0 && r.NoPrice) || ((mask & 2) != 0 && r.NoCost) ||
                ((mask & 4) != 0 && r.NowSell) || ((mask & 8) != 0 && r.NowCost);
            var selected = (CostMissingPrice)mask;
            Assert.Equal(rows.Count(OldPredicate), index.Count(selected));
            Assert.Equal(rows.Where(OldPredicate).Select(r => r.Id).ToArray(),
                rows.Where(r => index.Includes(r.Id, selected)).Select(r => r.Id).ToArray());
            Assert.True(index.Includes(-1, selected));
            Assert.Equal(mask == 0, index.Includes(0, selected));
        }
    }

    [Fact]
    public void EmbeddedFilterMatchesOriginalAnyPredicateOn25000RowsIncludingEmptySelection()
    {
        // Duplicate IDs deliberately carry different flags. The embedded
        // summary matches the row itself, not the standalone per-ID union.
        var rows = Enumerable.Range(1, 25000)
            .Select(i => Row(i / 2, i % 16)).ToArray();
        var index = new CostMissingPriceIndex(rows);
        for (var mask = 0; mask < 16; mask++)
        {
            bool Original(CostSummaryRow r) =>
                ((mask & 1) != 0 && r.NoPrice) || ((mask & 2) != 0 && r.NoCost) ||
                ((mask & 4) != 0 && r.NowSell) || ((mask & 8) != 0 && r.NowCost);
            var selected = (CostMissingPrice)mask;
            Assert.Equal(rows.Count(Original), index.CountAny(selected));
            Assert.Equal(rows.Where(Original).ToArray(),
                rows.Where(r => CostMissingPriceIndex.MatchesAny(r, selected)).ToArray());
        }
        Assert.Equal(0, index.CountAny(CostMissingPrice.None));
        Assert.Equal(rows.Length, index.Count(CostMissingPrice.None));
    }

    [Fact]
    public void FreshSnapshotAndEmptyIndexDoNotReuseOldFlags()
    {
        var old = new CostMissingPriceIndex(new[] { Row(1, 15) });
        var fresh = new CostMissingPriceIndex(new[] { Row(1, 0), Row(2, 2) });
        var empty = new CostMissingPriceIndex(Array.Empty<CostSummaryRow>());
        Assert.Equal(1, old.Count(CostMissingPrice.MaterialPrice));
        Assert.Equal(0, fresh.Count(CostMissingPrice.MaterialPrice));
        Assert.False(fresh.Includes(1, CostMissingPrice.MaterialPrice));
        Assert.True(fresh.Includes(2, CostMissingPrice.MaterialCost));
        Assert.Equal(0, empty.Count(CostMissingPrice.MaterialCost));
        Assert.False(empty.Includes(123, CostMissingPrice.MaterialCost));
    }
}
