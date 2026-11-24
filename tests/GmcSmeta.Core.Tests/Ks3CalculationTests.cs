using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class Ks3CalculationTests
{
    [Fact]
    public void CalculatesPeriodYearAndInceptionFromCurrentAct()
    {
        var acts = new (long Id, string Number, DateTime Date, decimal Total)[]
        {
            (1, "1", new DateTime(2025, 12, 20), 122m),
            (2, "2", new DateTime(2026, 1, 20), 244m),
            (3, "3", new DateTime(2026, 5, 20), 122m),
            (4, "4", new DateTime(2026, 11, 20), 366m)
        };
        var result = SqlServerActs.CalculateKs3Report(acts, 3L, 22m);
        Assert.Equal(100m, result.Period);
        Assert.Equal(300m, result.Year); // (244+122) / 1.22
        Assert.Equal(400m, result.Start); // (122+244+122) / 1.22
        Assert.Equal(22m, result.Vat);
        Assert.Equal(122m, result.Total);
        Assert.Equal(2, result.Previous.Count);
        Assert.Equal(100m, result.Previous[0].Net);
        Assert.Equal(200m, result.Previous[1].Net);
        Assert.DoesNotContain(result.Previous, p => p.Title.Contains("№ 4"));
    }

    [Fact]
    public void HandlesZeroVatAndMissingAct()
    {
        var rows = new (long Id, string Number, DateTime Date, decimal Total)[]
        {
            (101, "1", new DateTime(2026, 11, 21), 123.45m)
        };
        var result = SqlServerActs.CalculateKs3Report(rows, 101, 0m);
        Assert.Equal(123.45m, result.Period);
        Assert.Equal(123.45m, result.Year);
        Assert.Equal(0m, result.Vat);
        Assert.Empty(result.Previous);
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerActs.CalculateKs3Report(rows, 999, 22m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlServerActs.CalculateKs3Report(rows, 101, -1m));
    }
}
