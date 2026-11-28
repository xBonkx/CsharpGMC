using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class PriceLikeBatchPlanTests
{
    private static PriceLikeBatchPlan.Position P(long id, decimal price,
        decimal? purchase = null, string formulas = "",
        bool root = true, bool outside = false, string name = "Решётка 200x100",
        string unit = "шт") =>
        new(id, name, unit, root, outside, price, purchase, formulas);

    [Fact]
    public void CopiesOnlyChangedIdenticalRootsAndKeepsSource()
    {
        var rows = new[]
        {
            P(1, 600m), P(2, 200m, formulas:"{\"price\":\"10*20\",\"qty\":\"2*5\"}"),
            P(3, 600m), P(4, 150m, root:false), P(5, 200m, outside:true),
            P(6, 100m, name:"Решётка другая"),
            P(7, 100m, name:"  решётка 200x100 ", unit:"шт.")
        };
        var plan = PriceLikeBatchPlan.Build(rows, 1, "price");
        Assert.Equal(600m, plan.Price);
        Assert.Equal(new long[] { 2, 7 }, plan.TargetIds);
    }

    [Fact]
    public void PurchaseNullCanClearOnlyOtherPurchasePrices()
    {
        var plan = PriceLikeBatchPlan.Build(new[]
        {
            P(1, 100m, null), P(2, 100m, 50m), P(3, 100m, null)
        }, 1, "purchase_price");
        Assert.Null(plan.Price);
        Assert.Equal(new long[] { 2 }, plan.TargetIds);
    }

    [Fact]
    public void RejectsBrokenJsonBeforeChangingAnyRow()
    {
        var bad = new[] { P(1, 500m), P(2, 200m, formulas:"{bad") };
        Assert.Throws<InvalidOperationException>(() =>
            PriceLikeBatchPlan.Build(bad, 1, "price"));
        var arrayJson = new[] { P(1, 500m), P(2, 200m, formulas:"[]") };
        Assert.Throws<InvalidOperationException>(() =>
            PriceLikeBatchPlan.Build(arrayJson, 1, "price"));
    }

    [Fact]
    public void DetectsMissingSourceAndUnsafeFields()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PriceLikeBatchPlan.Build(new[] { P(1, 500m) }, 2, "price"));
        Assert.Throws<InvalidOperationException>(() =>
            PriceLikeBatchPlan.Build(new[] { P(1, 500m) }, 1, "qty"));
        Assert.Throws<InvalidOperationException>(() =>
            PriceLikeBatchPlan.Build(new[] { P(1, -5m), P(2, 40m) }, 1, "price"));
    }

    [Fact]
    public void PlansLargeContractInLinearNumberOfTargets()
    {
        var rows = new[] { P(1, 10m) }
            .Concat(Enumerable.Range(2, 2501).Select(i => P(i, 5m))).ToArray();
        var plan = PriceLikeBatchPlan.Build(rows, 1, "price");
        Assert.Equal(2501, plan.TargetIds.Count);
        Assert.Equal(2L, plan.TargetIds[0]);
        Assert.Equal(2502L, plan.TargetIds[^1]);
    }

    private static EstimateRow E(int id, decimal price, decimal? purchase = null,
        string name = "Решётка", string unit = "шт", bool sub = false, bool outside = false,
        RowKind kind = RowKind.Position) =>
        new(kind, 1, id, id, sub, "", name, unit, 1m, price, price, purchase, outside, "")
        { ParentPositionId = sub ? 99 : null };

    [Fact]
    public void MenuPreviewCountsOnlyDifferentEligibleRowsFromFullSnapshot()
    {
        var rows = new[] { E(1, 100m, 50m), E(2, 10m, 50m, " решётка ", "шт."),
            E(3, 100m, 10m), E(4, 10m, 20m, sub: true), E(5, 10m, 20m, outside: true),
            E(6, 10m, 20m, name: "Другая"), E(7, 10m, 20m, unit: "м"),
            E(1, 0m, kind: RowKind.System) };
        var preview = PriceLikeBatchPlan.Preview(rows, 1);
        Assert.Equal(100m, preview.ClientPrice);
        Assert.Equal(50m, preview.PurchasePrice);
        Assert.Equal(1, preview.ClientCount);
        Assert.Equal(1, preview.PurchaseCount);
        Assert.Equal(new long[] { 2 }, preview.ClientIds);
        Assert.Equal(new long[] { 3 }, preview.PurchaseIds);
        var persisted = rows.Where(r => !r.IsSystem).Select(r =>
            new PriceLikeBatchPlan.Position(r.Id, r.Name, r.Unit, r.ParentPositionId is null,
                r.OutOfKp, r.Price, r.PurchasePrice, r.Formulas)).ToArray();
        Assert.Equal(PriceLikeBatchPlan.Build(persisted, 1, "price").TargetIds.Count, preview.ClientCount);
        Assert.Equal(PriceLikeBatchPlan.Build(persisted, 1, "purchase_price").TargetIds.Count, preview.PurchaseCount);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public void MenuNeverOffersClearingOrInvalidPrice(int client, int? purchase)
    {
        var preview = PriceLikeBatchPlan.Preview(new[] { E(1, client, purchase), E(2, 10m, 20m) }, 1);
        Assert.Equal(0, preview.ClientCount);
        Assert.Equal(0, preview.PurchaseCount);
    }

    [Fact]
    public void MenuPreviewRejectsMissingSourceComponentOrBlankName()
    {
        var rows = new[] { E(1, 10m, sub: true), E(2, 10m, name: " "), E(3, 20m), E(4, 10m, outside: true) };
        foreach (var id in new[] { 1, 2, 4, 99 })
            Assert.Equal(0, PriceLikeBatchPlan.Preview(rows, id).ClientCount);
    }

    [Fact]
    public void MenuPreviewCountsLargeSnapshotAndIgnoresEqualPrices()
    {
        var rows = new[] { E(1, 10m, 10m) }.Concat(
            Enumerable.Range(2, 2501).Select(i => E(i, 5m, 10m))).ToArray();
        var preview = PriceLikeBatchPlan.Preview(rows, 1);
        Assert.Equal(2501, preview.ClientCount);
        Assert.Equal(0, preview.PurchaseCount);
    }

    [Fact]
    public void CapturedPlanChecksPriceAndExactIdsNotOnlyCount()
    {
        var preview = PriceLikeBatchPlan.Preview(new[] { E(1, 10m), E(2, 5m), E(3, 7m) }, 1);
        var expected = preview.ExpectedPlan("price");
        Assert.True(PriceLikeBatchPlan.Matches(expected, new(10m, new long[] { 3, 2 })));
        Assert.False(PriceLikeBatchPlan.Matches(expected, new(10m, new long[] { 3, 4 })));
        Assert.False(PriceLikeBatchPlan.Matches(expected, new(11m, new long[] { 2, 3 })));
        Assert.False(PriceLikeBatchPlan.Matches(expected, new(10m, new long[] { 2, 2 })));
    }

    [Fact]
    public void OutsideSourceIsNotAnEstimatePriceSource()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PriceLikeBatchPlan.Build(new[] { P(1, 10m, outside: true), P(2, 5m) }, 1, "price"));
        Assert.False(PriceLikeBatchPlan.PositivePrice(null));
        Assert.False(PriceLikeBatchPlan.PositivePrice(0m));
        Assert.False(PriceLikeBatchPlan.PositivePrice(-1m));
        Assert.True(PriceLikeBatchPlan.PositivePrice(1m));
    }

    [Fact]
    public void IdenticalPricesDoNotProduceWrites()
    {
        Assert.Empty(PriceLikeBatchPlan.Build(new[] { P(1, 10m), P(2, 10m) },
            1, "price").TargetIds);
    }
}
