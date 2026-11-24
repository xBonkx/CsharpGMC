using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

public sealed class FinanceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly long _contract, _system;
    public FinanceTests()
    {
        _conn.Open(); Database.Exec(_conn, "PRAGMA foreign_keys=ON"); Database.EnsureSchema(_conn);
        var obj = Documents.CreateObject(_conn, "Synthetic finance");
        _contract = Documents.CreateContract(_conn, obj, kind: "main");
        _system = Documents.AddSystem(_conn, _contract, "System");
    }
    private long Position(string name, double qty = 1, double price = 0, double? plan = null,
        long? parent = null, bool outside = false, double? work = null) =>
        RowWriter.Insert(_conn, "positions", new Dictionary<string, object?>
        { ["system_id"] = _system, ["name"] = name, ["qty"] = qty, ["price"] = price,
          ["purchase_price"] = plan, ["parent_id"] = parent, ["out_of_kp"] = outside, ["work_cost"] = work });

    [Fact]
    public void Cost_FactOverridesPlanEvenWhenZeroAndWorkAndOutsideStaySeparate()
    {
        var parent = Position("Unit", 2, 100, 30);
        RowWriter.Insert(_conn, "procurement", new Dictionary<string, object?> { ["position_id"] = parent, ["cost_price"] = 40d });
        Position("Component", 3, 0, 2, parent);
        Position("Outside", 4, 99, 5, outside: true);
        var work = Position("Монтаж установки", 2, 50, 20, work: 7);
        Position("Work component", 5, 0, 2, work);
        Position("Missing price", 2, 10);
        var free = Position("Free purchase", 1, 25, 10);
        RowWriter.Insert(_conn, "procurement", new Dictionary<string, object?> { ["position_id"] = free, ["cost_price"] = 0d });
        RowWriter.Insert(_conn, "smr_costs", new Dictionary<string, object?> { ["contract_id"] = _contract, ["amount"] = 6d });
        var finance = FinanceReader.ReadAll(_conn)[(int)_contract];
        Assert.Equal(86m, finance.Cost); Assert.Equal(225m, finance.CoveredRevenue);
        Assert.Equal(20m, finance.OutsideCost); Assert.Equal(20m, finance.SmrCost);
        Assert.Equal(1, finance.MissingPrices); Assert.Equal(199m, finance.Earned(100m));
        Assert.Null(new ContractFinance(OutsideCost: 20).Earned(100));
    }

    [Theory]
    [InlineData(2.675, 2.67)]
    [InlineData(-2.675, -2.67)]
    [InlineData(1.005, 1.0)]
    [InlineData(2.685, 2.69)]
    [InlineData(0.125, 0.12)]
    [InlineData(0.375, 0.38)]
    public void BinaryHalfKopeck_RoundsLikePython(double input, double expected) =>
        Assert.Equal(expected, FinanceReader.PythonRound(input));

    [Theory]
    [InlineData(false, false, 0.02)]
    [InlineData(true, false, 0.02)]
    [InlineData(true, true, 0.01)]
    public void Acts_UseFrozenPricesAndExactRowsOverrideRowRounding(bool roundRows, bool exact, double expected)
    {
        SetRules(roundRows, false);
        var position = Position("Actual price must not be used", price: 999);
        var act = RowWriter.Insert(_conn, "acts", new Dictionary<string, object?>
            { ["contract_id"] = _contract, ["signed"] = true, ["rows_exact"] = exact });
        for (var i = 0; i < 2; i++) RowWriter.Insert(_conn, "act_lines", new Dictionary<string, object?>
            { ["act_id"] = act, ["position_id"] = position, ["qty"] = 1d, ["price"] = .005d });
        Assert.Equal((decimal)expected, FinanceReader.ReadAll(_conn)[(int)_contract].Signed);
    }

    [Fact]
    public void ActVat_ExtraExpensesAndGrossAdjustmentAreAppliedToCorrectStatus()
    {
        SetRules(true, true);
        var position = Position("Unit", price: 999);
        var act = RowWriter.Insert(_conn, "acts", new Dictionary<string, object?>
            { ["contract_id"] = _contract, ["signed"] = false, ["smr_amount"] = 5d,
              ["transport_amount"] = 2d, ["pnr_amount"] = 1d, ["gross_adjustment_cents"] = 1 });
        RowWriter.Insert(_conn, "act_lines", new Dictionary<string, object?>
            { ["act_id"] = act, ["position_id"] = position, ["qty"] = 10d, ["price"] = 3d });
        var finance = FinanceReader.ReadAll(_conn)[(int)_contract];
        Assert.Equal(0m, finance.Signed); Assert.Equal(46.37m, finance.Pending);
        var summary = Assert.Single(FinanceReader.ReadActs(_conn));
        Assert.Equal(30m, summary.Materials); Assert.Equal(38m, summary.Net); Assert.Equal(8.36m, summary.Vat);
        var frozen = Assert.Single(Acts.Positions(_conn, (int)act));
        Assert.Equal(3m, frozen.Price); Assert.Equal(10m, frozen.Qty); Assert.Equal(30m, frozen.Amount);
    }

    [Fact]
    public void Progress_ClampsOnlyPaintedBarAndPreservesNegativeRemainder()
    {
        var progress = new ContractProgress(100, 80, 40);
        Assert.Equal(-20, progress.Remaining);
        Assert.Equal(.8, progress.SignedFraction);
        Assert.Equal(.2, progress.PendingFraction, 8);
        Assert.Equal("80%  ·  120% с поданными", progress.Caption);
        Assert.Equal("—", new ContractProgress(0, 0, 0).Caption);
    }

    [Fact]
    public void Finance_MatchesPythonForEveryDocument()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "db", "base.db"))) root = root.Parent;
        if (root is null) return;
        using var conn = Database.Open(Path.Combine(root!.FullName, "db", "base.db"), readOnly: true);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "db", "statistics_python.json")));
        var data = FinanceReader.ReadAll(conn);
        foreach (var row in json.RootElement.EnumerateObject())
        {
            var finance = data[int.Parse(row.Name)];
            var py = row.Value.GetProperty("finance");
            foreach (var (name, value) in new[] { ("cost", finance.Cost), ("covered", finance.CoveredRevenue),
                ("outside", finance.OutsideCost), ("smr_cost", finance.SmrCost), ("signed", finance.Signed), ("pending", finance.Pending) })
                Assert.True(Math.Abs((double)value - py.GetProperty(name).GetDouble()) < .006, $"Document {row.Name}, {name}: {value} != {py.GetProperty(name)}");
            Assert.Equal(py.GetProperty("missing").GetInt32(), finance.MissingPrices);
        }
        Assert.Equal(34, data.Count);
    }
    private void SetRules(bool roundRows, bool vatTop)
    {
        RowWriter.Insert(_conn, "tags", new Dictionary<string, object?>
            { ["key"] = "test", ["name"] = "Test", ["rules"] = JsonSerializer.Serialize(new { round_rows = roundRows, vat_top = vatTop }) });
        RowWriter.Update(_conn, "contracts", _contract, new Dictionary<string, object?> { ["form"] = "test" });
    }
    public void Dispose() => _conn.Dispose();
}
