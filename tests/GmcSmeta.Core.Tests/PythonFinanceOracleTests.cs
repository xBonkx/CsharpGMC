using System.Text.Json;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>Frozen Python assertions always run. The independent oracle runner
/// additionally feeds actual Python results, without opening SQL Server.</summary>
public sealed class PythonFinanceOracleTests
{
    [Theory]
    [InlineData(200,0,-1,200)]
    [InlineData(300,5,-1,285)]
    [InlineData(1882837.04,10,178197078,1781970.78)]
    [InlineData(200,25,-1,150)]
    [InlineData(100,0,12345,123.45)]
    [InlineData(1864854,5,-1,1771611.30)]
    public void FrozenPythonExamples(double before,double percent,long manual,double payable)
        =>Assert.Equal((decimal)payable,DiscountPolicy.Payable((decimal)before,(decimal)percent,manual));

    [Fact]
    public void CompareActualPythonOracleWhenExplicitlyRequested()
    {
        var file=Environment.GetEnvironmentVariable("GMC_SMETA_PYTHON_FINANCE_ORACLE");
        if(string.IsNullOrWhiteSpace(file))return; // frozen cases above still run
        using var doc=JsonDocument.Parse(File.ReadAllText(file));
        Assert.Equal("aa6b34dcf0314df93086a297f01d549ff58cf705",doc.RootElement.GetProperty("source").GetString());
        var cases=doc.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Assert.Equal(8,cases.Count);
        Assert.Equal(8,cases.Select(c=>c.GetProperty("name").GetString()).Distinct().Count());
        foreach(var c in cases)
        {
            decimal D(string key)=>c.GetProperty(key).GetDecimal();
            bool vatTop=c.GetProperty("vatTop").GetBoolean();
            long manual=c.GetProperty("manual").ValueKind==JsonValueKind.Null?-1:c.GetProperty("manual").GetInt64();
            var act=KsFinancialPolicy.Calculate(D("materials"),D("smr"),D("transport"),D("pnr"),
                D("vatRate"),vatTop,c.GetProperty("adjustment").GetInt64(),D("percent"),manual);
            var label=c.GetProperty("name").GetString();
            void Same(decimal expected,decimal actual,string field)
                =>Assert.True(Money.Round2(expected)==Money.Round2(actual),$"{label}: {field}: {expected} != {actual}");
            Same(D("gross"),act.Gross,"act gross");
            Same(D("vat"),act.Vat,"act VAT");
            Same(D("ks3Gross"),act.Gross,"KS3 gross");
            var original=KsFinancialPolicy.Calculate(D("materials"),D("smr"),D("transport"),D("pnr"),
                D("vatRate"),vatTop,c.GetProperty("adjustment").GetInt64());
            Same(D("discount"),DiscountPolicy.Amount(original.Gross,D("percent"),manual),"act adjustment");
            var before=D("contractMaterials")+D("contractSmr")+D("contractTransport")+D("contractPnr");
            if(vatTop)before=Money.Round2(before+Money.Round2(before*D("vatRate")/100m));
            Same(D("contractGross"),DiscountPolicy.Payable(before,D("contractPercent")),"contract gross");
            Same(D("contractDiscount"),DiscountPolicy.Amount(before,D("contractPercent")),"contract discount");
        }
    }
}
