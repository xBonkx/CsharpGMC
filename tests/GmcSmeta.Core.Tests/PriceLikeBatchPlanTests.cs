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

    [Fact]
    public void IdenticalPricesDoNotProduceWrites()
    {
        Assert.Empty(PriceLikeBatchPlan.Build(new[] { P(1, 10m), P(2, 10m) },
            1, "price").TargetIds);
    }
}
