using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SummaryGroupAccumulatorTests
{
    [Theory]
    [InlineData(null, null, false, true)]
    [InlineData(0, 0, false, true)]
    [InlineData(5, 5, false, true)]
    [InlineData(5, 6, false, false)]
    [InlineData(null, 5, false, false)]
    [InlineData(5, null, false, false)]
    [InlineData(5, 5, true, false)]
    [InlineData(null, null, true, false)]
    public void UniformPriceKeepsNullZeroAndMixedDistinct(int? first, int? second,
        bool differs, bool uniform)
    {
        var acc = new SummaryGroupAccumulator();
        acc.Add(1, 2, 3, 4, first, first, first, first,
            false, false, false, false, true, false, true, false);
        acc.Add(5, 6, 7, 8, second, second, second, second,
            differs, differs, differs, differs, false, true, false, true);
        foreach (var price in new[] { acc.MaterialUnitCost, acc.MaterialUnitSell,
                     acc.WorkUnitCost, acc.WorkUnitSell })
        {
            Assert.Equal(uniform, price.IsUniform);
            Assert.Equal(uniform ? (decimal?)first : null, price.Value);
        }
        Assert.Equal(6m, acc.MaterialCost);
        Assert.Equal(8m, acc.MaterialSell);
        Assert.Equal(10m, acc.WorkCost);
        Assert.Equal(12m, acc.WorkSell);
        Assert.True(acc.NoCost && acc.NoPrice && acc.NowCost && acc.NowSell);
    }

    [Fact]
    public void LargeGroupMatchesIndependentSumsAndFlags()
    {
        var values = Enumerable.Range(1, 25000).Select(i =>
            (Cost: i * 0.13m, Sell: i * 0.15m, Work: -i * 0.02m, WorkSell: i * 0.04m)).ToArray();
        var acc = new SummaryGroupAccumulator();
        foreach (var row in values)
            acc.Add(row.Cost, row.Sell, row.Work, row.WorkSell, 1, 2, null, 0,
                false, false, false, false, false, false, true, false);
        Assert.Equal(values.Sum(r => r.Cost), acc.MaterialCost);
        Assert.Equal(values.Sum(r => r.Sell), acc.MaterialSell);
        Assert.Equal(values.Sum(r => r.Work), acc.WorkCost);
        Assert.Equal(values.Sum(r => r.WorkSell), acc.WorkSell);
        Assert.True(acc.WorkUnitCost.IsUniform);
        Assert.Null(acc.WorkUnitCost.Value);
        Assert.True(acc.NowCost);
        Assert.False(acc.NoCost || acc.NoPrice || acc.NowSell);
    }

    [Fact]
    public void EmptyAndMixedCannotBecomeUniformAgain()
    {
        var acc = new SummaryGroupAccumulator();
        Assert.False(acc.MaterialUnitCost.IsUniform);
        foreach (var price in new decimal?[] { null, 2, null, null })
            acc.Add(0, 0, 0, 0, price, 0, 0, 0,
                false, false, false, false, false, false, false, false);
        Assert.False(acc.MaterialUnitCost.IsUniform);
        Assert.True(acc.MaterialUnitSell.IsUniform);
    }
}
