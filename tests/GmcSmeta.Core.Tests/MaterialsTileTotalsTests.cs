using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class MaterialsTileTotalsTests
{
    private static CostMaterialsPosition Row(long id, decimal qty, decimal price, decimal? plan,
        decimal? fact = null, long? parent = null, bool work = false) =>
        new(id, 1, parent, "row", "шт", qty, price, plan, fact, "", "", "", work);

    [Fact]
    public void MaterialsPreserveCoverageFactComponentsAndWorkRules()
    {
        var counts = MaterialsTileTotals.Count(new[]
        {
            Row(1,2,100,30,40), Row(2,3,200,5,parent:1),
            Row(3,1,80,null), Row(4,1,50,null,work:true),
            Row(5,2,0,10), Row(6,1,70,20,work:true)
        });
        Assert.Equal((280m,135m,270m,1,1),counts);
    }

    [Fact]
    public void EmptyAndZeroCostHaveDistinctCoverage()
    {
        Assert.Equal((0m,0m,0m,0,0),MaterialsTileTotals.Count(Array.Empty<CostMaterialsPosition>()));
        Assert.Equal((100m,0m,100m,0,0),MaterialsTileTotals.Count(new[]{Row(1,1,100,10,0)}));
    }

    [Fact]
    public void TransportAddsRevenueAndOnlyKnownCostAddsCoverage()
    {
        var counts = (100m,30m,80m,1,0);
        Assert.Equal((150m,50m,130m),MaterialsTileTotals.IncludeTransport(counts,50,20));
        Assert.Equal((150m,30m,80m),MaterialsTileTotals.IncludeTransport(counts,50,null));
        Assert.Equal((150m,30m,130m),MaterialsTileTotals.IncludeTransport(counts,50,0));
        Assert.Equal((100m,30m,80m),MaterialsTileTotals.IncludeTransport(counts,0,20));
    }
}
