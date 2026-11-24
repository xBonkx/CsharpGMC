using GmcSmeta.Core.Domain;
using System.Data;

namespace GmcSmeta.Core.Tests;

public class Schema31DiscountTests
{
    [Fact]
    public void ContractDiscountKeepsRowPricesAndRoundsToKopecks()
    {
        const decimal gross = 11638545.40m;
        const decimal rate = 3.543436268m;
        Assert.Equal(412404.44m, DiscountPolicy.Amount(gross, rate));
        Assert.Equal(11226140.96m, DiscountPolicy.Payable(gross, rate));
        Assert.Equal(100.005m, DiscountPolicy.Payable(100.005m, 0m));
        Assert.Equal(0.01m, DiscountPolicy.Amount(1m, 0.5m));
    }

    [Fact]
    public void SavedActRateAndManualTotalAreIndependent()
    {
        var act = KsFinancialPolicy.Calculate(1000m, 200m, 50m, 25m, 22m, false,
            discountPercent: 10m);
        Assert.Equal(1275m, act.Net);
        Assert.Equal(1147.50m, act.Gross);
        Assert.Equal(act.Gross, act.Ks3Net + act.Vat);
        var manual = KsFinancialPolicy.Calculate(1882837.04m, 0, 0, 0, 22m, false,
            discountPercent: 3.543436268m, manualTotalCents: 178197078L);
        Assert.Equal(1781970.78m, manual.Gross);
        Assert.Equal(manual.Gross, manual.Ks3Net + manual.Vat);
        Assert.Equal(0m, DiscountPolicy.Payable(100m, 10m, 0));
        Assert.Equal(110m, DiscountPolicy.Payable(100m, 10m, 11000));
        Assert.Equal(-10m, DiscountPolicy.Amount(100m, 10m, 11000));
    }

    [Fact]
    public void ReaderDoesNotTruncateDiscountToSixPlaces()
    {
        using var table = new DataTable();
        table.Columns.Add("discount_percent", typeof(double));
        table.Rows.Add(3.543436268d);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        Assert.Equal(3.543436268m, DiscountPolicy.ReadPercent(reader, 0));
    }
}
