using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class Ks3FullTotalsTests
{
    [Fact]
    public void FullActTotalsAreUsedForCumulativeKs3InsteadOfMaterialLines()
    {
        var first = KsFinancialPolicy.Calculate(1000m, 600m, 20m, 30m, 22m, false);
        var second = KsFinancialPolicy.Calculate(2000m, 100m, 0m, 0m, 22m, false);
        var acts = new[]
        {
            new SqlServerActs.ActInfo(101, "1.1", new DateTime(2025,12,15),
                null,null,true,4,first.Gross,
                10,"Договор",first.Ks3Net,first.Vat,first.Materials,first.Smr,first.Transport,first.Pnr,22m),
            new SqlServerActs.ActInfo(102, "2.1", new DateTime(2026,1,15),
                null,null,false,5,second.Gross,
                11,"ДС №1",second.Ks3Net,second.Vat,second.Materials,second.Smr,second.Transport,second.Pnr,22m)
        };
        var report=SqlServerActs.CalculateKs3ReportFromActs(acts,102);
        Assert.Equal(second.Ks3Net, report.Period);
        Assert.Equal(second.Ks3Net, report.Year);
        Assert.Equal(first.Ks3Net+second.Ks3Net,report.Start);
        Assert.Equal(second.Vat,report.Vat);
        Assert.Equal(second.Gross,report.Total);
        Assert.Single(report.Previous);
    }

    [Fact]
    public void VatTopAdjustmentDoesNotDisappearFromReport()
    {
        var money=KsFinancialPolicy.Calculate(100m,20m,0m,0m,22m,true,5);
        var act=new SqlServerActs.ActInfo(1,"3",new DateTime(2026,3,1),
            null,null,false,1,money.Gross,10,"",
            money.Ks3Net,money.Vat,money.Materials,money.Smr,money.Transport,money.Pnr,22m,true);
        var report=SqlServerActs.CalculateKs3ReportFromActs(new[]{act},1);
        Assert.Equal(146.45m,report.Total);
        Assert.Equal(120.05m,report.Period);
        Assert.Equal(26.4m,report.Vat);
    }

    [Fact]
    public void UnknownActCannotReturnAnEmptyButValidLookingReport()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerActs.CalculateKs3ReportFromActs(Array.Empty<SqlServerActs.ActInfo>(), 1));
    }
}
