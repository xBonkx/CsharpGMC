using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>Формулы объёмов КС-2 — перенос act_qty.py (без подключения к БД).</summary>
public sealed class ActQuantityFormulaTests
{
    private static Dictionary<long, (string, decimal)> Lines(params (long Id, string Formula, decimal Qty)[] rows) =>
        rows.ToDictionary(r => r.Id, r => (r.Formula, r.Qty));

    private static Dictionary<long, (decimal, string)> Edits(params (long Id, decimal Qty, string Formula)[] rows) =>
        rows.ToDictionary(r => r.Id, r => (r.Qty, r.Formula));

    private static HashSet<long> Sources(params long[] ids) => ids.ToHashSet();

    [Fact]
    public void Bind_ReplacesVisibleNumbers()
    {
        var bound = ActQuantityFormulas.Bind("К2*1,5", new Dictionary<long, int> { [42] = 2 });
        Assert.Equal("@42*1,5", bound);  // запятая в остальной формуле сохраняется, как в эталоне
    }

    [Fact]
    public void Bind_AmbiguousOrMissing_Rejected()
    {
        var numbers = new Dictionary<long, int> { [1] = 2, [2] = 2 };
        Assert.Throws<InvalidOperationException>(() => ActQuantityFormulas.Bind("К2", numbers));
        Assert.Throws<InvalidOperationException>(() => ActQuantityFormulas.Bind("К9", new Dictionary<long, int>()));
    }

    [Fact]
    public void Display_ConvertsBackToVisibleNumbers()
    {
        var shown = ActQuantityFormulas.Display("@42*1.5", new Dictionary<long, int> { [42] = 2 });
        Assert.Equal("К2*1.5", shown);
    }

    [Fact]
    public void Calculate_SimpleArithmetic()
    {
        // строка 42 = @41*1.5, строка 41 = 8 → 12
        var result = ActQuantityFormulas.Calculate(
            Lines((41, "", 8m), (42, "@41*1.5", 0m)),
            Edits((41, 8m, "")),
            Sources(41));
        Assert.Equal(12m, result[42]);
    }

    [Theory]
    [InlineData("@41@43")]
    [InlineData("@41.5")]
    [InlineData("2@41")]
    [InlineData("@41(2)")]
    [InlineData("@41+")]
    public void Calculate_ReferencesCannotBeJoinedIntoNumericLiterals(string formula)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ActQuantityFormulas.Calculate(
            Lines((41, "", 8m), (42, "", 0m), (43, "", 2m)),
            Edits((42, 0m, formula)),
            Sources(41, 43)));
        Assert.Contains("Проверьте выражение", error.Message);
    }

    [Fact]
    public void Calculate_ReferencesAreIndependentOperandsIncludingRepeatedReferences()
    {
        var result = ActQuantityFormulas.Calculate(
            Lines((41, "", 8m), (42, "", 0m), (43, "", 2m)),
            Edits((42, 0m, "(@41+@43)*@43-@41/2")),
            Sources(41, 43));
        Assert.Equal(16m, result[42]);
    }

    [Fact]
    public void Calculate_ExistingMissingReferenceRemainsZeroButCannotBeConcatenated()
    {
        var result = ActQuantityFormulas.Calculate(
            Lines((42, "@99+2", 0m)), Edits(), Sources());
        Assert.Equal(2m, result[42]);
        Assert.Throws<InvalidOperationException>(() => ActQuantityFormulas.Calculate(
            Lines((42, "@99.5", 0m)), Edits(), Sources()));
    }

    [Fact]
    public void Calculate_DependentChain_ResolvesInOrder()
    {
        // 43 = @41+@42; 42 = @41*2; 41 = 5 (edit) → 43 = 15
        var result = ActQuantityFormulas.Calculate(
            Lines((41, "", 1m), (42, "@41*2", 0m), (43, "@41+@42", 0m)),
            Edits((41, 5m, "")),
            Sources(41, 42));
        Assert.Equal(15m, result[43]);
        Assert.Equal(10m, result[42]);
    }

    [Fact]
    public void Calculate_Cycle_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ActQuantityFormulas.Calculate(
            Lines((41, "@42", 0m), (42, "@41", 0m)),
            Edits((41, 0m, "@42")),
            Sources(41, 42)));
    }

    [Fact]
    public void Calculate_ReferenceOutsideSources_Rejected()
    {
        // строка 99 — не верхняя строка сметы: ссылка из редактируемой строки запрещена
        Assert.Throws<InvalidOperationException>(() => ActQuantityFormulas.Calculate(
            Lines((41, "", 5m), (42, "", 0m)),
            Edits((42, 0m, "@99*2")),
            Sources(41)));
    }

    [Fact]
    public void Calculate_DivisionByZero_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ActQuantityFormulas.Calculate(
            Lines((41, "", 0m), (42, "@41/0", 0m)),
            Edits((41, 0m, "")),
            Sources(41)));
    }

    [Fact]
    public void Calculate_NegativeResult_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ActQuantityFormulas.Calculate(
            Lines((41, "", 0m), (42, "@41-5", 0m)),
            Edits((41, 2m, "")),
            Sources(41)));
    }

    [Fact]
    public void Calculate_StarStar_Rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ActQuantityFormulas.Calculate(
            Lines((41, "", 2m), (42, "@41**2", 0m)),
            Edits((41, 2m, "")),
            Sources(41)));
    }

    [Fact]
    public void Calculate_ParensAndDiv()
    {
        var result = ActQuantityFormulas.Calculate(
            Lines((41, "", 8m), (42, "(@41+2)/5", 0m)),
            Edits((41, 8m, "")),
            Sources(41));
        Assert.Equal(2m, result[42]);
    }

    [Fact]
    public void Calculate_WithoutFormulas_Empty()
    {
        var result = ActQuantityFormulas.Calculate(
            Lines((41, "", 5m)),
            Edits((41, 5m, "")),
            Sources(41));
        Assert.Empty(result);
    }

    [Fact]
    public void EditingReferences_PartialDuplicateAmbiguousAndStable()
    {
        var numbers=new Dictionary<long,string>{[1]="12",[2]="15.1",[3]="99",[4]="99"};
        Assert.Equal(new long[]{1,2,4},ActQuantityFormulas.EditingReferences("=K12+К15,1+@1+@4+К99+",numbers));
        Assert.Empty(ActQuantityFormulas.EditingReferences("=К999+@999+К",numbers));
        Assert.Empty(ActQuantityFormulas.EditingReferences("К12",numbers));
        Assert.Equal("@2",ActQuantityInput.Parse("=К15,1",numbers).Formula);
        Assert.Equal("К15.1",ActQuantityFormulas.Display("@2",numbers));
    }
}
