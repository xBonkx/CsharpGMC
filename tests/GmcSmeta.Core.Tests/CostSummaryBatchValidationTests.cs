using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class CostSummaryBatchValidationTests
{
    private const string Pilot = "Server=invalid;Database=GmcSmetaPilot_BatchValidation;Integrated Security=true;Connect Timeout=1";
    private const string Production = "Server=invalid;Database=GmcSmeta;Integrated Security=true;Connect Timeout=1";

    [Fact]
    public void CancelledReadDoesNotAttemptToUseClosedConnection()
    {
        using var connection = new Microsoft.Data.SqlClient.SqlConnection(Pilot);
        var token = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => SqlServerCostSummary.Summary(connection, 1, token));
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void CancelledMaterialsReadDoesNotAttemptToUseClosedConnection()
    {
        using var connection = new Microsoft.Data.SqlClient.SqlConnection(Pilot);
        var token = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => SqlServerCostSummary.MaterialsData(connection, 1, token));
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    [Fact]
    public void BatchRefusesProductionBeforeOpeningConnection()
    {
        Assert.Throws<InvalidOperationException>(() => SqlServerCostSummary.SaveSummaryCells(
            Production, 1, new[] { (1L, "12"), (2L, "13") }, "price", "rev"));
    }

    [Theory]
    [InlineData("price", "bad")]
    [InlineData("purchase_price", "-1")]
    [InlineData("work_sell", "bad")]
    public void BatchValidatesAllInputsBeforeOpeningConnection(string field, string input)
    {
        var result = SqlServerCostSummary.SaveSummaryCells(
            Pilot, 1, new[] { (1L, "12"), (2L, input) }, field, "rev");
        Assert.False(result.Saved);
        Assert.Equal("rev", result.Revision);
    }

    [Fact]
    public void BatchRefusesDuplicateAndInvalidTargets()
    {
        foreach (var ids in new[] { new[] { (1L, "12"), (1L, "13") }, new[] { (1L, "12"), (0L, "13") } })
            Assert.False(SqlServerCostSummary.SaveSummaryCells(Pilot, 1, ids, "price", "rev").Saved);
    }
}
