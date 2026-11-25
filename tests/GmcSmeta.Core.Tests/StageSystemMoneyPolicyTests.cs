using GmcSmeta.Core.Domain;
using static GmcSmeta.Core.Domain.StageSystemMoneyPolicy;

namespace GmcSmeta.Core.Tests;

public sealed class StageSystemMoneyPolicyTests
{
    [Fact]
    public void UsesEachLinkedSectionNotWholeDocument()
    {
        var result=Calculate(new Source[]{new(1,100m),new(2,600m),new(3,50m)},
            new Destination[]{new(11,1),new(12,2),new(13,3),new(14,null),new(15,999)},
            new Row[]{new(1,10m,20m,false),new(11,3m,20m,false),
                new(2,5m,100m,false),new(12,1m,100m,false),new(13,1m,100m,false)});
        Assert.Equal(30m,result[11]);
        Assert.Equal(120m,result[12]);
        Assert.Equal(0m,result[13]);
        Assert.False(result.ContainsKey(14));
        Assert.False(result.ContainsKey(15));
    }

    [Fact]
    public void RespectsPerRowRoundingAndKeepsExactProductsOtherwise()
    {
        var sources=new Source[]{new(1,100m)};
        var destinations=new Destination[]{new(2,1)};
        Assert.Equal(50.75m,Calculate(sources,destinations,new Row[]{
            new(1,2m,.335m,false),new(2,1m,.335m,true)})[2]);
        Assert.Equal(50m,Calculate(sources,destinations,new Row[]{
            new(1,2m,.335m,false),new(2,1m,.335m,false)})[2]);
    }
}
