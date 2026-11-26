using System.Data;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateTotalsProjectionTests
{
    [Theory]
    [InlineData(false, "sum")]
    [InlineData(false, "percent")]
    [InlineData(false, "systems")]
    [InlineData(false, "pos")]
    [InlineData(true, "sum")]
    [InlineData(true, "percent")]
    [InlineData(true, "systems")]
    [InlineData(true, "pos")]
    public void LargeProjectionPreservesMoneyAndCounts(bool roundRows, string mode)
    {
        using var table = new DataTable();
        table.Columns.Add("key", typeof(string));
        table.Columns.Add("rules", typeof(string));
        table.Rows.Add("qa", roundRows ? "{\"round_rows\":true,\"vat_top\":true}" : "{}");
        using var reader = table.CreateDataReader();
        var rules = TagRules.Read(reader);
        var contract = new TotalsContract(17, "main", "qa", 22m, mode, 12m, 10m, 5m, 7m, 2.5m);
        var systems = new[] { new TotalsSystem(20, 3m), new TotalsSystem(10, 7m), new TotalsSystem(30, 11m) };
        var rows = new List<TotalsPosition>(25000);
        for (var i = 0; i < 5000; i++)
        {
            rows.Add(new TotalsPosition(20, 1m, 1.005m, 0.125m, false, false));
            rows.Add(new TotalsPosition(10, -1m, 0.004m, 0.005m, false, false));
            rows.Add(new TotalsPosition(20, 1m, 999m, 999m, false, true));
            rows.Add(new TotalsPosition(10, 1m, 999m, 999m, true, false));
            rows.Add(new TotalsPosition(999, 1m, 999m, 999m, false, false));
        }
        var actual = Totals.CalculateOne(rules, contract, systems, rows, rows.Count);
        var materials = roundRows ? 5050m : 5005m;
        var smr = mode switch
        {
            "percent" => materials / 10m,
            "systems" => 21m,
            "pos" => 600m,
            _ => 12m,
        };
        var net = materials + smr + 12m;
        var vatTop = decimal.Round(net * 0.22m, 2, MidpointRounding.AwayFromZero);
        var gross = roundRows ? net + vatTop : net;
        var discount = decimal.Round(gross * 0.025m, 2, MidpointRounding.AwayFromZero);
        var payable = gross - discount;
        var vat = roundRows ? vatTop : payable * 22m / 122m;
        Assert.Equal(new ContractTotals(17, "main", "qa", 22m, materials, smr, net, payable,
            vat, 25000, 5m, 7m, 2.5m, discount), actual);
    }

    [Fact]
    public void EmptyProjectionPreservesFixedExpensesAndSystemSmr()
    {
        var contract = new TotalsContract(1, "main", "", 0m, "systems", 0m, 0m, 5m, 7m);
        var total = Totals.CalculateOne(new TagRules(), contract,
            new[] { new TotalsSystem(7, 3m) }, Array.Empty<TotalsPosition>(), 0);
        Assert.Equal(0m, total.Materials);
        Assert.Equal(3m, total.Smr);
        Assert.Equal(15m, total.Total);
        Assert.Equal(0, total.Positions);
    }
}
