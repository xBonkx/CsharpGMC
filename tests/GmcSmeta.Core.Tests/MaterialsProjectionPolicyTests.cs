using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class MaterialsProjectionPolicyTests
{
    private static CostMaterialsPosition P(long id, long system = 1, long? parent = null,
        decimal qty = 2m, decimal price = 100m, decimal? plan = 30m, decimal? fact = null,
        string supplier = "", bool work = false) =>
        new(id, system, parent, "A", "шт", qty, price, plan, fact, supplier, "", "", work);

    [Fact]
    public void EffectiveFactAndRawMarkupUseOriginalRounding()
    {
        var values = MaterialsProjection.Position(P(1, fact: 40m));
        Assert.Equal(40m, values.UnitCost);
        Assert.Equal(200m, values.Amount);
        Assert.Equal(80m, values.CostSum);
        Assert.Equal(150m, values.Markup);
        Assert.Equal(120m, values.Margin);
        Assert.False(values.MarkupWarn);
    }

    [Fact]
    public void ZeroMissingWorkAndSubRowsKeepDistinctSemantics()
    {
        var zero = MaterialsProjection.Position(P(1, plan: 0m));
        Assert.Equal(0m, zero.CostSum);
        Assert.Null(zero.Margin);
        Assert.False(zero.NoCost);
        Assert.True(MaterialsProjection.Position(P(2, plan: null)).NoCost);
        Assert.False(MaterialsProjection.Position(P(3, plan: null, work: true)).NoCost);
        Assert.False(MaterialsProjection.Position(P(4, parent: 1, plan: null)).NoCost);
        Assert.Null(MaterialsProjection.Position(P(5, parent: 1)).Markup);
        Assert.True(MaterialsProjection.Position(P(6, price: 0m)).NoPrice);
    }

    [Fact]
    public void SupplierGroupsPreserveCaseSensitiveMembershipAndFirstSeenTieOrder()
    {
        var groups = MaterialsProjection.SupplierGroups(new[] { P(1, supplier: ""), P(2, supplier: "B"),
            P(3, supplier: "a"), P(4, supplier: "A"), P(5, supplier: "a", fact: 40m) }).ToArray();
        Assert.Equal(new[] { "a", "A", "B", "" }, groups.Select(g => g.Supplier));
        Assert.Equal(new long[] { 3, 5 }, groups[0].Members.Select(p => p.Id));
        Assert.Equal(140m, groups[0].Cost);
    }

    [Fact]
    public void SystemLabelsUseDirectParentAndMissingParentFallback()
    {
        var labels = MaterialsProjection.SystemLabels(new (long, string, bool, long?)[]
        { (1, "A", false, null), (2, "B", true, 1), (3, "C", true, 99) });
        Assert.Equal("A > B", labels[2]);
        Assert.Equal("C", labels[3]);
    }

    [Fact]
    public void SelectionExpandsTopSystemRowsAndGroupsWithoutDuplicates()
    {
        var selected = new MaterialsSelectionRow[]
        {
            new(false, false, false, false, 1, 2, []),
            new(true, false, false, false, 1, 0, []),
            new(true, true, false, false, 0, 0, [2, 4]),
            new(false, false, true, false, 1, 3, [])
        };
        Assert.Equal(new long[] { 2, 1, 4 }, MaterialsEditPolicy.SelectedIds(new[] { P(1), P(2) }, selected));
        Assert.Equal(new long[] { 2 }, MaterialsEditPolicy.ClearIds("price", selected));
        Assert.Equal(new long[] { 2, 3 }, MaterialsEditPolicy.ClearIds("cost_note", selected));
    }

    [Theory]
    [InlineData("Наименование", " ", false)]
    [InlineData("Цена клиенту", "", true)]
    [InlineData("Цена клиенту", "=4+5", true)]
    [InlineData("Закуп за ед.", "garbage", false)]
    [InlineData("Факт по счёту", "", true)]
    [InlineData("Наценка, %", "", false)]
    [InlineData("Наценка, %", "-100", true)]
    [InlineData("Наценка, %", "-100,01", false)]
    [InlineData("Поставщик", "", true)]
    public void EditValidationMatchesExistingColumnRules(string column, string text, bool allowed)
    {
        var result = MaterialsEditPolicy.Validate(column, text);
        Assert.True(result.Handled);
        Assert.Equal(allowed, result.Allowed);
    }

    [Fact]
    public void MarkupPlanUsesFactAndSkipsMissingZeroAndUnknownCosts()
    {
        var rows = new[] { P(1, fact: 40m), P(2, plan: null), P(3, plan: 0m) }.ToDictionary(p => p.Id);
        Assert.Equal(new[] { (1L, "60") }, MaterialsEditPolicy.MarkupEdits(new long[] { 2, 1, 3, 99 }, rows, 50m));
    }
}
