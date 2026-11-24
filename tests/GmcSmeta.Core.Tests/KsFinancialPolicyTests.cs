using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsFinancialPolicyTests
{
    [Fact]
    public void StandardVatIsInsideClientPriceAndCostsAreIncluded()
    {
        var x = KsFinancialPolicy.Calculate(1000m, 200m, 50m, 25m, 22m, false);
        Assert.Equal(1275m, x.Gross);
        Assert.Equal(1275m, x.Net);
        Assert.Equal(1045.08m, x.Ks3Net);
        Assert.Equal(229.92m, x.Vat);
    }

    [Fact]
    public void VatOnTopAndGrossAdjustmentAreHandledSeparately()
    {
        var x = KsFinancialPolicy.Calculate(100m, 20m, 0m, 0m, 22m, true, 3);
        Assert.Equal(120m, x.Net);
        Assert.Equal(26.40m, x.Vat);
        Assert.Equal(146.43m, x.Gross);
        Assert.Equal(120.03m, x.Ks3Net);
    }

    [Fact]
    public void ExactRowsRoundOnceAndNonExactRoundEachRow()
    {
        var lines = new[] {(Quantity: 0.005m, Price: 1m),
                           (Quantity: 0.005m, Price: 1m)};
        Assert.Equal(0.01m, KsFinancialPolicy.Materials(lines, true, false));
        Assert.Equal(0m, KsFinancialPolicy.Materials(lines, false, false));
        Assert.Equal(0.02m, KsFinancialPolicy.Materials(lines, false, true));
    }

    [Theory]
    [InlineData("{\"vat_top\":true}", "vat_top", true)]
    [InlineData("{\"round_rows\":false}", "round_rows", false)]
    [InlineData("{\"vat_top\":1}", "vat_top", false)]
    [InlineData("not valid json", "vat_top", false)]
    public void FormRuleMustBeJsonBooleanTrue(string json, string name, bool expected) =>
        Assert.Equal(expected, KsFinancialPolicy.HasRule(json, name));

    [Fact]
    public void WrongVatCannotProducePlausibleReport()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KsFinancialPolicy.Calculate(100, 0, 0, 0, -1, false));
    }
}
