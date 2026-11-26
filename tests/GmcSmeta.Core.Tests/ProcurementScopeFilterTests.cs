using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementScopeFilterTests
{
    private static bool Check(long id = 21, long? parent = 5, string system = "ПВ-1",
        string? supplier = "Магазин", long[]? selected = null, string? supplierChoice = null,
        string? search = "", string name = "Воздуховод ф315", string order = "Заявка 7",
        string invoice = "Счет 3", string replacement = "") =>
        ProcurementScopeFilter.Matches(id, parent, system, supplier, selected ?? [],
            supplierChoice, search, name, order, invoice, replacement);

    [Fact]
    public void EmptySystemAndSupplierFilters_ShowAllRows()
    {
        Assert.True(Check());
        Assert.True(Check(system: "ОВ-7", supplier: null));
        Assert.True(Check(id: 99, parent: null));
    }

    [Fact]
    public void MultipleSelectedSystemsAndParent_IncludesDirectChildren()
    {
        Assert.True(Check(selected: [21, 44]));
        Assert.True(Check(selected: [5], parent: 5));
        Assert.True(Check(selected: [5, 88], parent: 5));
        Assert.False(Check(selected: [5], parent: null));
        Assert.False(Check(selected: [99, 88]));
        Assert.False(Check(selected: [3], parent: 5));
    }

    [Fact]
    public void SupplierChoice_UsesExplicitNoSupplierAndExactSupplierName()
    {
        Assert.True(Check(supplier: null, supplierChoice: ""));
        Assert.True(Check(supplier: "", supplierChoice: ""));
        Assert.False(Check(supplier: "Магазин", supplierChoice: ""));
        Assert.True(Check(supplierChoice: "Магазин"));
        Assert.False(Check(supplierChoice: "магазин"));
        Assert.False(Check(supplier: null, supplierChoice: "Магазин"));
    }

    [Theory]
    [InlineData("пв-1 воздуховод", true)]
    [InlineData("ф315 ЗАЯВКА", true)]
    [InlineData("магазин счет", true)]
    [InlineData("ф315 отсутствует", false)]
    [InlineData("ов-2", false)]
    [InlineData("   ", true)]
    public void Search_UsesAllWordsAcrossDocumentFields(string search, bool expected) =>
        Assert.Equal(expected, Check(search: search));

    [Fact]
    public void CombinedFilters_AreIntersection_NotUnion()
    {
        Assert.True(Check(selected: [5], supplierChoice: "Магазин",
            search: "пв-1 ф315"));
        Assert.False(Check(selected: [5], supplierChoice: "",
            search: "пв-1 ф315"));
        Assert.False(Check(selected: [101], supplierChoice: "Магазин",
            search: "пв-1 ф315"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ф315\tЗАЯВКА  ")]
    [InlineData("ПВ-1 магазин")]
    [InlineData("счет замена")]
    [InlineData("нет")]
    public void PreparedSearchMatchesLegacyPredicateAcrossLargeInput(string search)
    {
        var words = ProcurementScopeFilter.PrepareSearch(search);
        var selected = new HashSet<long> { 5, 21 };
        for (var i = 0; i < 25000; i++)
        {
            var supplier = i % 3 == 0 ? null : "Магазин";
            var id = i % 5 == 0 ? 21 : 100;
            long? parent = i % 2 == 0 ? 5 : null;
            var name = i % 7 == 0 ? "Воздуховод ф315" : "Решетка";
            var fields = new[] { name, "ПВ-1", supplier ?? "", "Заявка 7", "Счет 3", "Замена" };
            var expected = (selected.Contains(id) || (parent.HasValue && selected.Contains(parent.Value))) &&
                words.All(word => fields.Any(field => field.Contains(word, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(expected, ProcurementScopeFilter.MatchesPrepared(id, parent, "ПВ-1", supplier,
                selected, null, words, name, "Заявка 7", "Счет 3", "Замена"));
        }
    }

    [Fact]
    public void NullSelectedSystems_FailsFast()
    {
        Assert.Throws<ArgumentNullException>(() => ProcurementScopeFilter.Matches(
            1, null, "", "", null!, null, "", "", "", "", ""));
    }
}
