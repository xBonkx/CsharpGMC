using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class Ks3GrossCalculationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(22)]
    public void PublicAdapterKeepsEveryField(int vat)
    {
        (long Id, string Number, DateTime Date, decimal Total)[] rows =
        [
            (9,"9",new DateTime(2025,12,31),0.03m),
            (10,"10",new DateTime(2026,1,1),0.03m),
            (11,"11",new DateTime(2026,1,1),0.03m),
            (12,"12",new DateTime(2026,2,1),1000m)
        ];
        var domain = Ks3GrossCalculation.Calculate(rows,11,vat);
        var adapter = SqlServerActs.CalculateKs3Report(rows,11,vat);
        Assert.Equal((domain.Period,domain.Year,domain.Start,domain.Vat,domain.Total),
            (adapter.Period,adapter.Year,adapter.Start,adapter.Vat,adapter.Total));
        Assert.Equal(domain.Previous.ToArray(),adapter.Previous.ToArray());
        Assert.False(adapter.GrossCumulative);
        Assert.Equal(2,domain.Previous.Count);
        Assert.Equal(0.03m,domain.Total);
    }

    [Fact]
    public void ZeroVatRespectsSuppliedOrderAndNegativeCorrections()
    {
        (long Id,string Number,DateTime Date,decimal Total)[] rows =
        [
            (5,"5",new DateTime(2026,2,1),100m),
            (4,"4",new DateTime(2026,1,1),-10m),
            (6,"6",new DateTime(2026,3,1),500m)
        ];
        var report=Ks3GrossCalculation.Calculate(rows,4,0m);
        Assert.Equal(-10m,report.Period);
        Assert.Equal(90m,report.Year);
        Assert.Equal(90m,report.Start);
        Assert.Equal(0m,report.Vat);
        Assert.Single(report.Previous);
    }
}
