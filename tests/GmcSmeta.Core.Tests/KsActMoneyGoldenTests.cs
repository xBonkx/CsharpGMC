using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>Frozen expected amounts calculated from Python db.Act materials/net/total/vat.</summary>
public sealed class KsActMoneyGoldenTests
{
    [Fact]
    public void DnsVatOnTopIncludesPnrTransportAndFiveKopeckAdjustment()
    {
        var calc=KsFinancialPolicy.Calculate(1000m,200m,30m,20m,20m,true,5);
        Assert.Equal(1250m,calc.Net);
        Assert.Equal(250m,calc.Vat);
        Assert.Equal(1500.05m,calc.Gross);
        Assert.Equal(1250.05m,calc.Ks3Net);
    }

    [Fact]
    public void IncludedVatIsExtractedNotAddedToActTotal()
    {
        var calc=KsFinancialPolicy.Calculate(1000m,200m,30m,20m,20m,false);
        Assert.Equal(1250m,calc.Gross);
        Assert.Equal(208.33m,calc.Vat);
        Assert.Equal(1041.67m,calc.Ks3Net);
    }

    [Fact]
    public void RoundRowsAndRowsExactDoNotLoseFractionalPennies()
    {
        var rows=new[] { (Quantity:1m,Price:0.335m),(Quantity:1m,Price:0.335m) };
        Assert.Equal(0.68m,KsFinancialPolicy.Materials(rows,false,true));
        Assert.Equal(0.67m,KsFinancialPolicy.Materials(rows,true,false));
    }

    [Fact]
    public void DnsRoundsProductWithOriginalPrecisionBeforeRoundingToKopecks()
    {
        var rows = new[] { (Quantity: 0.3349999999m, Price: 1m), (Quantity: 1.0000004m, Price: 12499.999m) };
        Assert.Equal(12500.34m, KsFinancialPolicy.Materials(rows, false, true));
    }

    [Theory]
    [InlineData(2500)]
    [InlineData(10000)]
    public void LargeKsTotalsAreDeterministicToCent(int count)
    {
        var rows=Enumerable.Repeat((Quantity:2m,Price:4.25m),count).ToArray();
        var material=KsFinancialPolicy.Materials(rows,false,false);
        var calc=KsFinancialPolicy.Calculate(material,100m,23.45m,16.55m,20m,true);
        Assert.Equal(count*8.5m,material);
        Assert.Equal(material+140m,calc.Net);
        Assert.Equal(decimal.Round(calc.Net*.2m,2,MidpointRounding.ToEven),calc.Vat);
        Assert.Equal(calc.Net+calc.Vat,calc.Gross);
    }

    [Fact]
    public void ExtraRowsAndPerSystemSmrAreCountedOnce()
    {
        var perSystem=KsActAllocation.Split(new[]
        {
            new KsActAllocation.Line(10,2m,100m,9m),
            new KsActAllocation.Line(20,1m,50m,3m)
        },31.03m,false);
        Assert.Equal(31.03m,perSystem.Values.Sum());
        var money=KsFinancialPolicy.Calculate(250m,perSystem.Values.Sum(),0m,5m,20m,false);
        Assert.Equal(286.03m,money.Gross);
    }
}
