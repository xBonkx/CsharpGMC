using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

/// <summary>
/// SQL Server cost summary calculation: no database writes or SQLite fixtures.
/// </summary>
public sealed class SqlServerCostSummaryProjectionTests
{
    [Fact]
    public void FinalizeRows_PreservesFoldedComponentCost_AndDoesNotMutateInput()
    {
        var parent = new CostSummaryRow(
            Id: 11, SystemId: 3, Name: "Оборудование", Unit: "шт", Qty: 2m,
            Supplier: "", MCost: 70m, MSell: 0m, WCost: 0m, WSell: 0m,
            NoCost: false, NoPrice: false, NowCost: false, NowSell: false,
            UCost: 10m, UPrice: 50m, UWcost: null, UWsell: null,
            Fact: false, Plan: null, Kids: true, Work: false);
        var work = new CostSummaryRow(
            Id: 12, SystemId: 3, Name: "Монтаж", Unit: "шт", Qty: 3m,
            Supplier: "", MCost: 0m, MSell: 0m, WCost: 0m, WSell: 0m,
            NoCost: false, NoPrice: false, NowCost: false, NowSell: false,
            UCost: null, UPrice: 20m, UWcost: 5m, UWsell: 10m,
            Fact: false, Plan: null, Kids: false, Work: true);

        var source = new List<CostSummaryRow> { parent, work };
        var result = SqlServerCostSummary.FinalizeRows(source);

        Assert.Equal(2, result.Count);
        Assert.Equal(70m, source[0].MCost); // Projection must not rewrite the List<T>.
        Assert.Equal(90m, result[0].MCost); // 70 of component costs + 2 × 10 unit cost.
        Assert.Equal(100m, result[0].MSell);
        Assert.Equal(10m, result[0].MatMargin);
        Assert.True(result[0].Kids);
        Assert.Equal(0m, result[1].MSell); // Work rows are excluded from material sale.
        Assert.Equal(15m, result[1].WCost);
        Assert.Equal(30m, result[1].WSell);
        Assert.Equal(100m, result[1].MarkupPct);
    }

    [Fact]
    public void UnitCost_UsesActualThenPlanWithoutConfusingZeroAndMissing()
    {
        Assert.Equal(15m, SqlServerCostSummary.EffectiveUnitCost(15m, 10m));
        Assert.Equal(10m, SqlServerCostSummary.EffectiveUnitCost(null, 10m));
        Assert.Equal(0m, SqlServerCostSummary.EffectiveUnitCost(0m, 10m));
        Assert.Null(SqlServerCostSummary.EffectiveUnitCost(null, null));
        var source = new CostSummaryRow(
            Id: 5, SystemId: 1, Name: "Воздуховод", Unit: "м", Qty: 3m,
            Supplier: "", MCost: 0m, MSell: 0m, WCost: 0m, WSell: 0m,
            NoCost: false, NoPrice: false, NowCost: false, NowSell: false,
            UCost: SqlServerCostSummary.EffectiveUnitCost(null, 40m), UPrice: 70m,
            UWcost: null, UWsell: null, Fact: false, Plan: 40m,
            Kids: false, Work: false);
        var result = Assert.Single(SqlServerCostSummary.FinalizeRows(new[] { source }));
        Assert.Equal(120m, result.MCost);
        Assert.Equal(210m, result.MSell);
        Assert.False(result.NoCost);
        Assert.False(result.Fact);
    }

    [Fact]
    public void FinalizeRows_DoesNotChangeInputAndSupportsEmptySummary()
    {
        var empty = SqlServerCostSummary.FinalizeRows(Array.Empty<CostSummaryRow>());
        Assert.Empty(empty);
    }
    [Theory]
    [InlineData("10.005", "0", "10.01")]
    [InlineData("10.015", "0", "10.02")]
    [InlineData("33.33", "50", "49.99")]
    [InlineData("150", "-100", "0.00")]
    public void PythonMoneyMarkup_RoundsAtCentBoundary(
        string cost, string percent, string expected)
    {
        var result = SqlServerCostOperations.PriceFromMarkup(
            decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(percent, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), result);
    }

    [Fact]
    public void MaterialMarkup_RetailAndReversePurchaseUseIndependentPrices()
    {
        Assert.Equal(154.31m, SqlServerCostOperations.PriceFromFactor(123.45m, 1.25m));
        Assert.Equal(154m, SqlServerCostOperations.PriceFromFactor(123.45m, 1.25m, wholeRubles: true));
        Assert.Equal(40m, SqlServerCostOperations.CostFromRetail(50m, 1.25m));
        Assert.Equal(4.20m, SqlServerCostOperations.TransportFromSuppliers(
            new[] { 120.10m, 89.90m }, 2m));
    }

    [Fact]
    public void MarkupRejectsImpossibleAndLossyFactors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqlServerCostOperations.PriceFromMarkup(200m, -100.01m));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqlServerCostOperations.PriceFromFactor(200m, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqlServerCostOperations.CostFromRetail(200m, 0m));
    }

    [Fact]
    public void Grouping_KeepsEmptySystemsAndStablePositionMembership()
    {
        CostSummaryRow Row(long id, long sys, string name, string supplier, string color = "") =>
            new(id, sys, name, "шт", 2m, supplier,
                20m, 40m, 6m, 12m, false, false, false, false,
                10m, 20m, 3m, 6m, false, null, false, false, color);
        var data = new CostSummaryData(
            new (long Id, string Label, bool IsSub)[] {
                (1, "П1", false), (2, "П2", false), (3, "В1", false) },
            new[] {
                Row(7, 1, "Воздуховод", "ГМЦ", "#FFF3B0"),
                Row(8, 2, "Воздуховод", "ГМЦ"),
                Row(9, 2, "Решетка", "Другой")
            },
            Array.Empty<(string Name, decimal Cost, decimal Sell, string Kind)>(),
            Array.Empty<string>(), 0m);
        var bySystem = CostSummaryGrouping.Build(data, "kp", "kp");
        Assert.Equal(3, bySystem.Count);
        Assert.True(bySystem[2].IsEmpty);
        Assert.Equal(new long[] { 8, 9 }, bySystem[1].PositionIds);
        var same = CostSummaryGrouping.Build(data, "same", "kp");
        Assert.Equal(2, same.Count);
        Assert.Equal(new long[] { 7, 8 }, same[0].PositionIds);
        Assert.Equal(40m, same[0].MaterialPurchase);
        Assert.Equal(80m, same[0].MaterialRetail);
        var supplier = CostSummaryGrouping.Build(data, "sup", "kp");
        Assert.Equal(new long[] { 7, 8 }, supplier[0].PositionIds);
        var sameSupplier = CostSummaryGrouping.Build(data, "sup_same", "kp");
        Assert.Equal(2, sameSupplier.Count);
        Assert.Equal(7L, CostSummaryGrouping.Build(data, "kp", "color")[0].Rows[0].Id);
    }

    [Fact]
    public void LargeCostViewProjection_KeepsAll2800OriginalPositionsInGroupViews()
    {
        var rows = Enumerable.Range(1, 2800)
            .Select(i => new CostSummaryRow(
                Id: i, SystemId: (i % 40) + 1, Name: "Материал " + i % 7,
                Unit: "шт", Qty: 1.25m, Supplier: "Поставщик " + i % 8,
                MCost: 120m, MSell: 150m, WCost: 0m, WSell: 0m,
                NoCost: false, NoPrice: false, NowCost: true, NowSell: true,
                UCost: 96m, UPrice: 120m, UWcost: null, UWsell: null,
                Fact: false, Plan: 96m, Kids: false, Work: false))
            .ToArray();
        var systems = Enumerable.Range(1, 42)
            .Select(i => ((long)i, "Система " + i, false)).ToArray();
        var data = new CostSummaryData(systems, rows,
            Array.Empty<(string Name, decimal Cost, decimal Sell, string Kind)>(),
            Array.Empty<string>(), 0m);
        foreach (var mode in CostSummaryGrouping.Modes)
        {
            var groups = CostSummaryGrouping.Build(data, mode, "cost_down");
            var ids = groups.SelectMany(x => x.PositionIds).ToArray();
            Assert.Equal(rows.Length, ids.Length);
            Assert.Equal(2800, ids.Distinct().Count());
            Assert.Equal(2800 * 120m, groups.Sum(g => g.MaterialPurchase));
        }
        Assert.Equal(2, CostSummaryGrouping.Build(data, "kp", "kp").Count(x => x.IsEmpty));
    }

}
