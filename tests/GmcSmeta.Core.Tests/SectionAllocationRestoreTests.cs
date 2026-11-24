using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SectionAllocationRestoreTests
{
    [Fact]
    public void RestoredSectionsUseNewIdsAndKeepUnallocatedAmounts()
    {
        const string original="""{"smr":{"12":123.45,"13":6.55,"44":7,"0":3},"transport":{"12":10.12,"99":1.23}}""";
        var json=KsActAllocation.RemapManual(original,new Dictionary<long,long>{{12,120},{13,130},{44,440}},new HashSet<long>{12,13});
        var amounts=KsActAllocation.ReadManual(json)!;
        Assert.Equal(123.45m,amounts.Smr[120]);
        Assert.Equal(6.55m,amounts.Smr[130]);
        Assert.Equal(10m,amounts.Smr[0]);
        Assert.Equal(140m,amounts.Smr.Values.Sum());
        Assert.Equal(10.12m,amounts.Transport[120]);
        Assert.Equal(1.23m,amounts.Transport[0]);
        Assert.False(amounts.Smr.ContainsKey(440));
        var resolved=KsActAllocation.Resolve(Array.Empty<KsActAllocation.Line>(),new HashSet<long>{120,130},
            140m,11.35m,false,true,json);
        Assert.Equal(140m,resolved.Smr.Values.Sum());
        Assert.Equal(11.35m,resolved.Transport.Values.Sum());
    }

    [Fact]
    public void LegacyEmptyAndExplicitEmptyRemainDistinct()
    {
        var map=new Dictionary<long,long>();
        var owned=new HashSet<long>();
        Assert.Equal("",KsActAllocation.RemapManual("",map,owned));
        var manual=KsActAllocation.ReadManual(KsActAllocation.RemapManual("{}",map,owned));
        Assert.NotNull(manual);
        Assert.Empty(manual.Smr);
        Assert.Empty(manual.Transport);
    }
}
