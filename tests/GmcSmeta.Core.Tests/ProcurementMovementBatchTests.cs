using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementMovementBatchTests
{
    private static readonly SqlServerProcurement.MovementInput[] Three =
    {
        new(11L, 1.25m), new(12L, 0.000002m), new(13L, 2m)
    };

    [Theory]
    [InlineData("in")]
    [InlineData("out")]
    [InlineData("back")]
    [InlineData("back_supplier")]
    public void PythonMovementKinds_AcceptMultiPositionBatches(string kind) =>
        Assert.Null(SqlServerProcurement.ValidateMovementBatch(Three, kind));

    [Theory]
    [InlineData("paid")]
    [InlineData("ordered")]
    [InlineData("random")]
    public void StageCodes_AreNotInventoryMovements(string kind) =>
        Assert.NotNull(SqlServerProcurement.ValidateMovementBatch(Three, kind));

    [Fact]
    public void EmptyAndDuplicateBatchesAreRejectedBeforeSql()
    {
        Assert.NotNull(SqlServerProcurement.ValidateMovementBatch(
            Array.Empty<SqlServerProcurement.MovementInput>(), "in"));
        Assert.NotNull(SqlServerProcurement.ValidateMovementBatch(new[]
        {
            new SqlServerProcurement.MovementInput(22L, 1m),
            new SqlServerProcurement.MovementInput(22L, 2m)
        }, "in"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.0000001)]
    [InlineData(1000000001)]
    public void InvalidOrUnsafeQuantitiesCannotReachSql(double quantity)
    {
        var input = new[] { new SqlServerProcurement.MovementInput(4L, (decimal)quantity) };
        Assert.NotNull(SqlServerProcurement.ValidateMovementBatch(input, "in"));
    }

    [Fact]
    public void BatchLimitIsBoundedForUiAndSql()
    {
        var rows = Enumerable.Range(1, 2501)
            .Select(id => new SqlServerProcurement.MovementInput(id, 1m)).ToArray();
        Assert.Contains("2500", SqlServerProcurement.ValidateMovementBatch(rows, "in"));
        Assert.Null(SqlServerProcurement.ValidateMovementBatch(rows.Take(2500).ToArray(), "in"));
    }

    [Fact]
    public void OutgoingCannotBypassFixedIssueSheet()
    {
        var rejected = SqlServerProcurement.AddMovements(
            "Server=unused;Database=GmcSmetaPilot_Test", 1, Three,
            "out", DateTime.Today, "", "", "test-revision");
        Assert.False(rejected.Saved);
        Assert.Contains("ведомость", rejected.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ObsoleteWritersCannotBypassContractAndRevisionGuards()
    {
        const string invalidConnection = "not a connection string";
#pragma warning disable CS0618
        var movement = SqlServerProcurement.AddMovement(invalidConnection, 1, "out",
            1m, DateTime.Today, "", "", "rev");
        Assert.False(movement.Saved);
        Assert.Throws<NotSupportedException>(() => SqlServerProcurement.IssueViaList(
            invalidConnection, 1, 1, 1m, DateTime.Today, "", "rev"));
        Assert.False(SqlServerShipments.SaveHeader(invalidConnection, 1,
            "recipient", "Test", "rev").Saved);
#pragma warning restore CS0618
    }

    [Fact]
    public void InvalidQuantityCannotConnectToProduction()
    {
        var rejected = SqlServerProcurement.AddMovements(
            "Server=unused;Database=Production", 1,
            new[] { new SqlServerProcurement.MovementInput(1L, -4m) },
            "in", DateTime.Today, "", "", "rev");
        Assert.False(rejected.Saved);
    }
}
