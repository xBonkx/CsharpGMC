using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class Schema31DiscountLifecycleTests
{
    [Theory]
    [InlineData(false,false,-1L,true)]
    [InlineData(true,false,-1L,false)]
    [InlineData(false,true,-1L,false)]
    [InlineData(false,false,0L,false)]
    [InlineData(false,true,123456L,false)]
    public void OnlyUnadjustedDraftInheritsContractRate(bool signed,bool individual,long cents,bool expected)
        => Assert.Equal(expected,DiscountPolicy.InheritsContractRate(signed,individual,cents));

    [Theory]
    [InlineData("GmcSmetaPilot_Cutover_20261120_160840")]
    [InlineData("GmcSmetaPilot_Test_Csharp")]
    [InlineData("GmcSmetaPilot_UnitTest")]
    public void LifecycleWritersRejectOtherDatabasesBeforeConnecting(string name)
    {
        var cs=$"Server=localhost;Database={name};Integrated Security=true";
        Assert.Throws<InvalidOperationException>(()=>SqlServerActs.CreateSchema31Test(cs,1,"0"));
        Assert.Throws<InvalidOperationException>(()=>SqlServerActs.ToggleSignedSchema31Test(cs,1,false,"0"));
        Assert.Throws<InvalidOperationException>(()=>SqlServerActs.SaveContractDiscountSchema31Test(cs,1,10,"0"));
    }

    [Fact]
    public void ManualAndZeroPercentAdjustmentsAreDistinct()
    {
        var inherited=DiscountPolicy.Normalize("contract",null,5);
        var individual=DiscountPolicy.Normalize("percent",0,5);
        var manual=DiscountPolicy.Normalize("total",0,5);
        Assert.Equal(950m,DiscountPolicy.Payable(1000,inherited.Percent,inherited.ManualTotalCents));
        Assert.Equal(1000m,DiscountPolicy.Payable(1000,individual.Percent,individual.ManualTotalCents));
        Assert.Equal(0m,DiscountPolicy.Payable(1000,manual.Percent,manual.ManualTotalCents));
    }
}
