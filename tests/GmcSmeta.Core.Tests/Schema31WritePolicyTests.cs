using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class Schema31WritePolicyTests
{
    [Fact]
    public void DiscountModesPreserveZeroAndManualCorrection()
    {
        Assert.Equal(new DiscountPolicy.ActDiscount(false,-1,3.543436268m),DiscountPolicy.Normalize("contract",null,3.543436268m));
        Assert.Equal(new DiscountPolicy.ActDiscount(true,-1,0),DiscountPolicy.Normalize("percent",0,10));
        Assert.Equal(new DiscountPolicy.ActDiscount(true,0,0),DiscountPolicy.Normalize("total",0,10));
        Assert.Equal(new DiscountPolicy.ActDiscount(true,266938347,0),DiscountPolicy.Normalize("total",2669383.47m,10));
        Assert.Throws<ArgumentException>(()=>DiscountPolicy.Normalize("total",1.001m,0));
        Assert.Throws<ArgumentException>(()=>DiscountPolicy.Normalize("percent",101,0));
        Assert.False(DiscountPolicy.SupportsDiscount(false,false,"r4","Р4"));
        Assert.False(DiscountPolicy.SupportsDiscount(false,true,"",""));
        Assert.False(DiscountPolicy.SupportsDiscount(false,false,"","Днепровский"));
    }

    [Theory]
    [InlineData("GmcSmetaPilot_Cutover_20261120_160840")]
    [InlineData("GmcSmetaPilot_Test_Csharp")]
    [InlineData("GmcSmetaPilot_UnitTest")]
    public void DedicatedWriterRejectsOtherDatabasesBeforeConnecting(string name)
    {
        Assert.Throws<InvalidOperationException>(()=>SqlServerActs.SaveDiscountSchema31Test(
            $"Server=localhost;Database={name};Integrated Security=true",1,"total",0,"0"));
    }

    [Fact]
    public void DedicatedDatabaseCannotUseNormalWriterOrOpen()
    {
        var name=SqlSchemaCompatibility.WriteTestDatabase;
        var cs=$"Server=localhost;Database={name};Integrated Security=true";
        SqlSchemaCompatibility.ValidateWriteTest("31",name,"READ_WRITE",true);
        Assert.Throws<InvalidOperationException>(()=>SqlSchemaCompatibility.ValidateWriteTest("31",name,"READ_ONLY",true));
        Assert.Throws<InvalidOperationException>(()=>SqlServerWorkspace.RequirePilotDatabase(cs));
        Assert.Throws<InvalidOperationException>(()=>SqlServerDatabase.Open(cs));
    }

    [Fact]
    public void ParentBudgetIncludesChildButNotChildWithOwnBudget()
    {
        var systems=new[] {new KsSectionEditPolicy.SystemBudget(1,null,100),
            new KsSectionEditPolicy.SystemBudget(2,1,0),new KsSectionEditPolicy.SystemBudget(3,1,40)};
        var quantities=new[] {new KsSectionEditPolicy.Quantity(1,10,5,10),
            new KsSectionEditPolicy.Quantity(2,10,5,10),new KsSectionEditPolicy.Quantity(3,10,10,10),
            new KsSectionEditPolicy.Quantity(2,10,10,1000,true)};
        var limits=new Dictionary<long,decimal>{{1,100},{2,0},{3,30}};
        var result=KsSectionEditPolicy.Suggest(systems,quantities,"systems",140,60,140,60,limits);
        Assert.Equal(50m,result.Smr[1]);
        Assert.Equal(30m,result.Smr[3]);
        Assert.False(result.Smr.ContainsKey(2));
        Assert.Equal(40m,result.Transport.Values.Sum());
        var valid=KsSectionEditPolicy.Validate(result,new HashSet<long>{1,2,3},false,140,60,limits);
        Assert.Equal(80m,valid.Smr);
        Assert.Equal(40m,valid.Transport);
        Assert.Equal(50m,KsActAllocation.ReadManual(valid.Json)!.Smr[1]);
        Assert.Throws<ArgumentException>(()=>KsSectionEditPolicy.Validate(result,new HashSet<long>{1,2,3},true,140,60,limits));
        Assert.Throws<ArgumentException>(()=>KsSectionEditPolicy.Validate(result,new HashSet<long>{1,2,3},false,79,60,limits));
    }

    [Fact]
    public void GlobalCapKeepsExactKopecksAndPosModeOnlySuggestsTransport()
    {
        var systems=Enumerable.Range(1,3).Select(i=>new KsSectionEditPolicy.SystemBudget(i,null,0)).ToArray();
        var quantities=systems.Select(s=>new KsSectionEditPolicy.Quantity(s.Id,1,1,10)).ToArray();
        var result=KsSectionEditPolicy.Suggest(systems,quantities,"pos",100,10,100,1,
            new Dictionary<long,decimal>());
        Assert.Empty(result.Smr);
        Assert.Equal(1m,result.Transport.Values.Sum());
    }

    [Fact]
    public void OrdinarySchema31RoutesFailBeforeNetworkWhileGateIsClosed()
    {
        Assert.False(SqlSchemaCompatibility.OrdinarySchema31Enabled);
        const string cs="Server=invalid.invalid;Database=GmcSmetaPilot_UnitTest;Integrated Security=true";
        var discount=SqlServerActs.SaveDiscount(cs,1,"total",0m,"r1");
        Assert.False(discount.Saved);
        Assert.Equal("r1",discount.Revision);
        var amounts=new KsActAllocation.SectionAmounts(new Dictionary<long,decimal>(),new Dictionary<long,decimal>());
        Assert.False(SqlServerActs.SaveSections(cs,1,amounts,"r1").Saved);
        Assert.Throws<InvalidOperationException>(()=>SqlServerActs.ReadSections(cs,1));
    }

    [Fact]
    public void ManualTotalRemainsExactAfterChangingQuantities()
    {
        var input=DiscountPolicy.Normalize("total",2669383.47m,5m);
        Assert.Equal(2669383.47m,DiscountPolicy.Payable(3000000m,input.Percent,input.ManualTotalCents));
        Assert.Equal(2669383.47m,DiscountPolicy.Payable(3100000m,input.Percent,input.ManualTotalCents));
        Assert.Equal(430616.53m,DiscountPolicy.Amount(3100000m,input.Percent,input.ManualTotalCents));
    }
}
