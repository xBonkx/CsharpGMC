using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateSourceCheckTests
{
    private const decimal Gross=11638545.40m;
    private const decimal Rate=3.543436268m;
    private static ContractTotals Totals(decimal rate=Rate)=>new(1,"main","",22m,Gross,0m,Gross,
        DiscountPolicy.Payable(Gross,rate),0m,1,DiscountPercent:rate,DiscountAmount:DiscountPolicy.Amount(Gross,rate));

    [Fact]
    public void Python0677_SourceTotalsBeforeOrAfterDiscount()
    {
        var totals=Totals();
        Assert.Equal(11226140.96m,totals.Total);
        var before=EstimateSourceCheck.Compare(Gross,Gross,totals);
        Assert.True(before.Matches);
        Assert.Contains("Совпадает с итогом до скидки",before.Details);
        var after=EstimateSourceCheck.Compare(Gross,totals.Total,totals);
        Assert.True(after.Matches);
        Assert.Contains("Совпадает с итогом после скидки",after.Details);
        var wrong=EstimateSourceCheck.Compare(Gross,11000000m,totals);
        Assert.False(wrong.Matches);
        Assert.Contains("Не совпадает",wrong.Details);
        Assert.False(EstimateSourceCheck.Compare(11000000m,Gross,totals).Matches);
        Assert.True(EstimateSourceCheck.Compare(null,Gross,Totals(0m)).Matches);
        Assert.Null(EstimateSourceCheck.Compare(null,null,totals).Matches);
    }

    [Fact]
    public void OneRubleToleranceIsInclusiveAndMaterialIndependent()
    {
        var totals=Totals();
        Assert.True(EstimateSourceCheck.Compare(Gross+1m,totals.Total-1m,totals).Matches);
        Assert.False(EstimateSourceCheck.Compare(Gross+1.01m,totals.Total,totals).Matches);
    }
}
