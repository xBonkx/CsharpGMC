using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class AreaCollapsePlanTests
{
    private static AreaCollapsePlan.Position P(long id, long system,
        string name, string unit = "м", decimal qty = 5m,
        bool children = false, bool dependencies = false,
        string extra = "", bool outside = false) =>
        new(id, system, "П1", name, unit, qty, 100m, 60m,
            null, (int)id, extra, outside, children, dependencies);

    [Fact]
    public void PerSystemGroupingKeepsSeparateSystems()
    {
        var rows = new[]
        {
            P(1, 1, "Воздуховод 200x100"),
            P(2, 2, "Воздуховод 200x100")
        };
        var split = AreaCollapsePlan.Build(rows, false);
        var whole = AreaCollapsePlan.Build(rows, true);
        Assert.Equal(2, split.Groups.Count);
        Assert.Single(whole.Groups);
        Assert.Equal(2, whole.Rows);
        Assert.Equal(6m, AreaCollapsePlan.SummaryQty(whole.Groups[0].Rows));
    }

    [Fact]
    public void PreservesAmountsAndSkipsProtectedOrUnknownRows()
    {
        var rows = new[]
        {
            P(1, 1, "Воздуховод 200x100"),
            P(2, 1, "Воздуховод без сечения"),
            P(3, 1, "Воздуховод 200x100", children:true),
            P(4, 1, "Воздуховод 200x100", dependencies:true),
            P(5, 1, "Воздуховод 200x100", outside:true)
        };
        var plan = AreaCollapsePlan.Build(rows, false);
        Assert.Single(plan.Groups);
        Assert.Equal(1, plan.Rows);
        Assert.Equal(1, plan.NoArea);
        Assert.Equal(2, plan.Excluded);
        Assert.Equal(500m, plan.Groups[0].Money);
        Assert.Equal(300m, plan.Groups[0].Purchase);
    }

    [Fact]
    public void OriginalLocationAndUnrelatedMetadataSurviveRoundTrip()
    {
        const string original = "{\"source\":\"excel\",\"custom\":42}";
        var collapsed = AreaCollapsePlan.CollapsedMetadata(original, 47, 12);
        Assert.Equal((47L, 12), AreaCollapsePlan.OriginalLocation(collapsed));
        var restored = AreaCollapsePlan.RestoredMetadata(collapsed);
        Assert.Contains("\"source\"", restored);
        Assert.Contains("\"custom\"", restored);
        Assert.DoesNotContain("m2_sys", restored);
        Assert.Null(AreaCollapsePlan.OriginalLocation("{}"));
        Assert.Null(AreaCollapsePlan.OriginalLocation("{broken"));
    }

    [Fact]
    public void CalculatedSummaryMoneyFollowsPythonRounding()
    {
        Assert.Equal(166.67m, AreaCollapsePlan.PricePerSquareMeter(500m, 3m));
        Assert.Equal(0m, AreaCollapsePlan.PricePerSquareMeter(0m, 3m));
        Assert.Equal(0m, AreaCollapsePlan.PricePerSquareMeter(500m, 0m));
    }

    [Fact]
    public void RepeatedCollapseReweightsBothPricesWithoutLosingAmount()
    {
        var initial = AreaCollapsePlan.ExtendSummary(0m, 0m, null,
            10m, 1000m, 600m);
        var later = AreaCollapsePlan.ExtendSummary(
            initial.Qty, initial.Price, initial.PurchasePrice,
            10m, 2000m, 1200m);

        Assert.Equal(20m, later.Qty);
        Assert.Equal(150m, later.Price);
        Assert.Equal(90m, later.PurchasePrice);
        Assert.Equal(3000m, later.Qty * later.Price);
        Assert.Equal(1800m, later.Qty * later.PurchasePrice!.Value);
    }

    [Fact]
    public void RepeatedCollapseWithNoNewPurchaseKeepsPurchaseTotals()
    {
        var result = AreaCollapsePlan.ExtendSummary(5m, 200m, 120m,
            5m, 0m, 0m);
        Assert.Equal(10m, result.Qty);
        Assert.Equal(100m, result.Price);
        Assert.Equal(60m, result.PurchasePrice);

        var noPurchase = AreaCollapsePlan.ExtendSummary(0m, 0m, null,
            5m, 100m, 0m);
        Assert.Null(noPurchase.PurchasePrice);
    }

    [Fact]
    public void BrokenAreaMergeIsRejectedBeforeAnySqlWrite()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AreaCollapsePlan.ExtendSummary(5m, 100m, null, 0m, 100m, 0m));
        Assert.Throws<InvalidOperationException>(() =>
            AreaCollapsePlan.ExtendSummary(5m, 100m, null, -1m, 100m, 0m));
        Assert.Throws<InvalidOperationException>(() =>
            AreaCollapsePlan.ExtendSummary(NumberText.MaxNumber, 1m, null,
                1m, 1m, 0m));
    }

    [Fact]
    public void FinancialSummaryIsRebuiltFromOriginalRowsNotRoundedParentPrice()
    {
        // The old summary might have 33.33 ₽/m², but its child rows retain
        // their exact money; accumulation of parent price must never drift.
        var source = new[]
        {
            new AreaCollapsePlan.SummaryComponent(
                "Воздуховод 200x100", "м", 5m, 100.01m, 60.01m, null),
            new AreaCollapsePlan.SummaryComponent(
                "Воздуховод 200x100", "м", 5m, 200.03m, 120.03m, null)
        };
        var first = AreaCollapsePlan.SumSourceRows(source.Take(1).ToArray());
        var all = AreaCollapsePlan.SumSourceRows(source);
        Assert.Equal(3m, first.Qty);
        Assert.Equal(6m, all.Qty);
        Assert.Equal(250.03m, all.Price);
        Assert.Equal(150.03m, all.PurchasePrice);
        Assert.Equal(all, AreaCollapsePlan.SumSourceRows(source.Reverse().ToArray()));
    }

    [Fact]
    public void RebuiltSummaryUsesManualAreaAndRejectsDamagedChildren()
    {
        var valid = AreaCollapsePlan.SumSourceRows(new[]
        {
            new AreaCollapsePlan.SummaryComponent(
                "Воздуховод без размера", "шт", 2m, 75m, null, 1.5)
        });
        Assert.Equal(3m, valid.Qty);
        Assert.Equal(50m, valid.Price);
        Assert.Null(valid.PurchasePrice);

        Assert.Throws<InvalidOperationException>(() =>
            AreaCollapsePlan.SumSourceRows(Array.Empty<AreaCollapsePlan.SummaryComponent>()));
        Assert.Throws<InvalidOperationException>(() =>
            AreaCollapsePlan.SumSourceRows(new[]
            {
                new AreaCollapsePlan.SummaryComponent("Воздуховод без размера",
                    "шт", 1m, 100m, null, null)
            }));
        Assert.Throws<InvalidOperationException>(() =>
            AreaCollapsePlan.SumSourceRows(new[]
            {
                new AreaCollapsePlan.SummaryComponent("Воздуховод 200x100",
                    "м", 1m, -10m, null, null)
            }));
    }

    [Fact]
    public void RebuiltSummaryHandlesLargeKpWithoutRepeatedRounding()
    {
        var rows = Enumerable.Range(1, 2501).Select(_ =>
            new AreaCollapsePlan.SummaryComponent("Воздуховод 300x200", "м",
                1m, 99.99m, 50m, null)).ToArray();
        var merged = AreaCollapsePlan.SumSourceRows(rows);
        Assert.Equal(2501m, merged.Qty);
        Assert.Equal(99.99m, merged.Price);
        Assert.Equal(50m, merged.PurchasePrice);
    }

    [Fact]
    public void ExistingCollapsedLocationCannotBeSilentlyOverwritten()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AreaCollapsePlan.CollapsedMetadata(
                "{\"m2_sys\":33,\"m2_order\":5}", 42, 6));
        Assert.Throws<InvalidOperationException>(() =>
            AreaCollapsePlan.CollapsedMetadata("{\"m2\":\"steel:1\"}", 42, 6));
        Assert.Throws<InvalidOperationException>(() =>
            AreaCollapsePlan.CollapsedMetadata("{}", 42, -1));
    }

    [Fact]
    public void ExistingSummaryKeyIsReadFromJsonAndNotName()
    {
        Assert.Equal("steel:steel:0.5", AreaCollapsePlan.ExistingKey("{\"m2\":\"steel:steel:0.5\"}"));
        Assert.Equal("", AreaCollapsePlan.ExistingKey("{\"other\":1}"));
    }

    [Fact]
    public void LargeKpPlanDoesNotLoseAnyUnprotectedRows()
    {
        var rows = Enumerable.Range(1, 2501)
            .Select(i => P(i, 1, "Воздуховод 300x200")).ToArray();
        var plan = AreaCollapsePlan.Build(rows, false);
        Assert.Equal(2501, plan.Rows);
        Assert.Single(plan.Groups);
        Assert.Equal(12505m, AreaCollapsePlan.SummaryQty(plan.Groups[0].Rows));
    }

    [Fact]
    public void CorruptedExtraJsonStopsReversibleCollapse()
    {
        Assert.Throws<InvalidOperationException>(() =>
            AreaCollapsePlan.CollapsedMetadata("{oops", 1, 0));
    }
}
