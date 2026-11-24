using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class Schema31SectionReadTests
{
    [Fact]
    public void ManualAmountsAreNotReallocatedByMaterials()
    {
        var lines = new[] { new KsActAllocation.Line(3,1,1000,0) };
        var result = KsActAllocation.Resolve(lines,new HashSet<long>{3,4},
            567000,60000,false,true,
            """{"smr":{"3":498000,"4":69000},"transport":{"3":50000,"4":10000}}""");
        Assert.Equal(498000m,result.Smr[3]);
        Assert.Equal(69000m,result.Smr[4]);
        Assert.Equal(10000m,result.Transport[4]);
        Assert.Equal(567000m,result.Smr.Values.Sum());
        Assert.Equal(60000m,result.Transport.Values.Sum());
    }

    [Fact]
    public void DeletedSectionsAndUnallocatedAmountsKeepAuthoritativeTotal()
    {
        var result = KsActAllocation.Resolve(Array.Empty<KsActAllocation.Line>(),
            new HashSet<long>{3},100,50,false,true,
            """{"smr":{"3":20,"4":70,"0":10},"transport":{"4":50}}""");
        Assert.Equal(20m,result.Smr[3]);
        Assert.Equal(80m,result.Smr[0]);
        Assert.Equal(50m,result.Transport[0]);
    }

    [Fact]
    public void EmptyManualMapIsDifferentFromLegacyAndPosModeKeepsWorkPrices()
    {
        var lines = new[] { new KsActAllocation.Line(3,2,100,30) };
        var systems = new HashSet<long>{3};
        Assert.Equal(60m,KsActAllocation.Resolve(lines,systems,60,0,false,true,"").Smr[3]);
        Assert.Equal(60m,KsActAllocation.Resolve(lines,systems,60,0,false,true,"{}").Smr[0]);
        Assert.Equal(60m,KsActAllocation.Resolve(lines,systems,60,0,true,true,
            """{"smr":{"3":10}}""").Smr[3]);
    }
}
