using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Tests;

public sealed class CostSummaryDeltaTests
{
    private static CostSummaryRow Row() => new(1, 2, "QA", "m", 2m, "A",
        12m, 20m, 6m, 8m, false, false, false, false,
        5m, 10m, 3m, 4m, false, 5m, true, false);

    [Fact]
    public void OnlyDeclaredFieldsMayChange()
    {
        var row = Row();
        Assert.True(SqlServerCostSummary.CanPatchCostRow(row, row with { Supplier = "B" }, "supplier"));
        Assert.True(SqlServerCostSummary.CanPatchCostRow(row, row with { MCost = 16, UCost = 7, Plan = 7 }, "purchase_price"));
        Assert.True(SqlServerCostSummary.CanPatchCostRow(row, row with { UWcost = null, WCost = 0, NowCost = true }, "work_cost"));
        Assert.True(SqlServerCostSummary.CanPatchCostRow(row, row with { UWsell = 5, WSell = 10 }, "work_sell"));
        Assert.False(SqlServerCostSummary.CanPatchCostRow(row, row with { Supplier = "B", Qty = 3 }, "supplier"));
        Assert.False(SqlServerCostSummary.CanPatchCostRow(row, row with { SystemId = 3 }, "purchase_price"));
        Assert.False(SqlServerCostSummary.CanPatchCostRow(row, row with { FactPrice = 7 }, "supplier"));
        Assert.False(SqlServerCostSummary.CanPatchCostRow(row, row, "price"));
    }

    [Fact]
    public void UnsupportedOrMissingIdentityAndCancelledReadDoNotContactSql()
    {
        using var conn = new SqlConnection();
        var summary = new CostSummaryData([], [Row()], [], [], 0);
        Assert.Null(SqlServerCostSummary.ReadChangedCostRow(conn, 1, summary, 1, "price", "r"));
        Assert.Null(SqlServerCostSummary.ReadChangedCostRow(conn, 1, summary, 2, "supplier", "r"));
        Assert.Null(SqlServerCostSummary.ReadChangedCostRow(conn, 1, summary, 1, "supplier", ""));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlServerCostSummary.ReadChangedCostRow(conn, 1, summary, 1, "supplier", "r", cancellation.Token));
    }
}
