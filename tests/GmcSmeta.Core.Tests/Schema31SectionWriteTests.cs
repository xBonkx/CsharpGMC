using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class Schema31SectionWriteTests
{
    [Theory]
    [InlineData("GmcSmetaPilot_Cutover_20261120_160840")]
    [InlineData("GmcSmetaPilot_Test_Csharp")]
    [InlineData("GmcSmetaPilot_UnitTest")]
    public void ReadAndWriteRejectNonDedicatedDatabaseWithoutConnecting(string name)
    {
        var cs=$"Server=localhost;Database={name};Integrated Security=true";
        Assert.Throws<InvalidOperationException>(()=>SqlServerActs.ReadSectionsSchema31Test(cs,1));
        Assert.Throws<InvalidOperationException>(()=>SqlServerActs.SaveSectionsSchema31Test(cs,1,
            new(new Dictionary<long,decimal>(),new Dictionary<long,decimal>()),"0"));
    }

    [Fact]
    public void RemainingUsesAllPreviousActsAndSavedAllocations()
    {
        var systems=new[]{new KsSectionEditPolicy.SystemBudget(1,null,100),
            new KsSectionEditPolicy.SystemBudget(2,null,200)};
        var rows=new[]{new KsActAllocation.Line(1,1,100,0),new KsActAllocation.Line(2,3,100,0)};
        var others=new[]{
            new KsSectionEditPolicy.PriorAct(40,10,true,
                """{"smr":{"1":35,"2":5},"transport":{"1":10}}""",rows),
            new KsSectionEditPolicy.PriorAct(40,20,true,"",rows),
            new KsSectionEditPolicy.PriorAct(10,5,false,"",rows)
        };
        var left=KsSectionEditPolicy.Remaining(systems,others,"systems",300,50,false);
        Assert.Equal(210m,left.Smr);
        Assert.Equal(15m,left.Transport);
        Assert.Equal(55m,left.BySystem[1]);
        Assert.Equal(165m,left.BySystem[2]);
        var allowed=new KsActAllocation.SectionAmounts(
            new Dictionary<long,decimal>{{1,55},{2,100}},
            new Dictionary<long,decimal>{{0,15}});
        Assert.Equal(155m,KsSectionEditPolicy.Validate(allowed,new HashSet<long>{1,2},false,
            left.Smr,left.Transport,left.BySystem).Smr);
        var excessive=allowed with {Smr=new Dictionary<long,decimal>{{1,55.01m}}};
        Assert.Throws<ArgumentException>(()=>KsSectionEditPolicy.Validate(excessive,new HashSet<long>{1,2},
            false,left.Smr,left.Transport,left.BySystem));
        var foreign=allowed with {Transport=new Dictionary<long,decimal>{{42,1}}};
        Assert.Throws<ArgumentException>(()=>KsSectionEditPolicy.Validate(foreign,new HashSet<long>{1,2},
            false,left.Smr,left.Transport,left.BySystem));
    }

    [Fact]
    public void EmptyManualAllocationDoesNotReassignLegacyWorks()
    {
        var systems=new[]{new KsSectionEditPolicy.SystemBudget(1,null,100)};
        var rows=new[]{new KsActAllocation.Line(1,1,100,0)};
        var left=KsSectionEditPolicy.Remaining(systems,
            new[]{new KsSectionEditPolicy.PriorAct(25,0,true,"{}",rows)},"systems",100,0,false);
        Assert.Equal(75m,left.Smr);
        Assert.Equal(100m,left.BySystem[1]);
    }
}
