using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class MaterialsMergeTests
{
    private static CostMaterialsPosition Row(long id, string name, string unit, decimal qty, decimal price,
        decimal? plan, decimal? fact = null, string supplier = "", string note = "") =>
        new(id, 1, null, name, unit, qty, price, plan, fact, supplier, "", note, false);

    [Fact]
    public void NormalizedKeysPreserveFirstSeenGroupAndMemberOrder()
    {
        var groups = MaterialsMerge.Groups(new[]
        {
            Row(4," Valve "," ШТ ",1,100,30), Row(2,"Pipe","м",1,100,30),
            Row(7,"valve","шт",1,100,30), Row(8,"Valve","м",1,100,30)
        }).ToArray();
        Assert.Equal(3,groups.Length);
        Assert.Equal(new long[]{4,7},groups[0].Select(p=>p.Id));
        Assert.Equal(2L,groups[1].Single().Id);
        Assert.Equal(8L,groups[2].Single().Id);
    }

    [Fact]
    public void EffectiveFactMissingCostsAndMixedFieldsRemainDistinct()
    {
        var result=MaterialsMerge.Summarize(new[]
        {
            Row(1,"A","шт",2,100,20,40,"S1","N1"),
            Row(2,"A","шт",3,80,null,supplier:"S2",note:"N2")
        });
        Assert.Equal(5m,result.Qty);
        Assert.Equal(440m,result.Sum);
        Assert.Equal(80m,result.CostSum);
        Assert.Equal(200m,result.Covered);
        Assert.Equal(2,result.Prices.Count);
        Assert.Contains((decimal?)40m,result.Costs);
        Assert.Contains((decimal?)null,result.Costs);
        Assert.Equal(2,result.Suppliers.Count);
        Assert.Equal(2,result.Notes.Count);
        Assert.Null(result.SingleMarkup);
        Assert.Contains((decimal?)150m,result.Markups);
        Assert.Contains((decimal?)null,result.Markups);
    }

    [Fact]
    public void IdenticalMarkupAndZeroCostFollowOriginalRules()
    {
        var same=MaterialsMerge.Summarize(new[]
        {
            Row(1,"A","шт",1,100,50), Row(2,"A","шт",1,200,100)
        });
        Assert.Equal(100m,same.SingleMarkup);
        var zero=MaterialsMerge.Summarize(new[]{Row(3,"B","шт",1,100,0)});
        Assert.Equal(100m,zero.Covered);
        Assert.Equal(0m,zero.CostSum);
        Assert.Null(zero.SingleMarkup);
    }

    [Fact]
    public void RevenueIsRoundedPerLineBeforeSumming()
    {
        var rows=new[]{Row(1,"A","шт",1,0.006m,0.002m),Row(2,"A","шт",1,0.006m,0.002m)};
        var result=MaterialsMerge.Summarize(rows);
        Assert.Equal(0.02m,result.Sum);
        Assert.Equal(0.02m,result.Covered);
        Assert.Equal(0.004m,result.CostSum);
    }
}
