using GmcSmeta.Core.Domain;
using static GmcSmeta.Core.Domain.EstimateDiscountInput;

namespace GmcSmeta.Core.Tests;
public sealed class EstimateDiscountInputTests
{
    private static State Baseline(decimal rate=0m,bool supported=true)=>new(11638545.40m,
        DiscountPolicy.Payable(11638545.40m,rate),9734849.55m,1903695.85m,false,rate,supported);

    [Fact]
    public void ExactTargetUsesNineDecimals()
    {
        var p=FromTarget(Baseline(),true,Parse("11 226 140,96"),true,false);
        Assert.NotNull(p);
        Assert.Equal(3.543436279m,p.Percent);
        Assert.Equal(11226140.96m,p.Total);
        Assert.True(p.Changed);
    }
    [Fact]
    public void RemovingDiscountAndBlockingDoubleDiscount()
    {
        var s=Baseline(3.543436268m);
        Assert.False(FromPercent(s,true,s.CurrentDiscount,true,false)!.Changed);
        var p=FromPercent(s,true,0m,true,false);
        Assert.Equal(s.Gross,p!.Total);
        Assert.True(p.Changed);
        Assert.Null(FromPercent(s,false,5m,true,false));
    }
    [Fact]
    public void SpecialFormsOnlyAllowLegacyAndInvalidTotalsAreRejected()
    {
        Assert.Null(FromPercent(Baseline(supported:false),true,5m,true,false));
        Assert.NotNull(FromPercent(Baseline(supported:false),false,5m,true,false));
        Assert.Null(FromPercent(Baseline(),true,-1m,true,false));
        Assert.Null(FromPercent(Baseline(),true,101m,true,false));
        Assert.Null(FromTarget(Baseline(),true,12000000m,true,false));
        Assert.Null(Parse("nan"));
    }
}
