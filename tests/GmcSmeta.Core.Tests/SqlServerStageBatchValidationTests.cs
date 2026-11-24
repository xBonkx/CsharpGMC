using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SqlServerStageBatchValidationTests
{
    private static readonly string Production =
        "Server=localhost;Database=GmcSmetaProduction;Integrated Security=True;TrustServerCertificate=True";

    private static StageBatchPlanner.Snapshot Row(long id) =>
        new(id, 10m, 5m, new decimal[] { 2, 3 });

    [Fact]
    public void UnsafeSelectionRejectedBeforeAnySqlConnection()
    {
        var result = SqlServerStages.ChangeSelected(Production, 1, 10,
            StageBatchPlanner.Action.PutRemaining, new long[] { 1, 1 },
            new long[] { 10, 11 }, new[] { Row(1), Row(1) }, "rev");
        Assert.False(result.Saved);
        Assert.Equal("rev", result.Revision);
        Assert.Equal(0, result.Changes);
    }

    [Fact]
    public void ForeignStageIdRejectedBeforeAnyConnection()
    {
        var result = SqlServerStages.ChangeSelected(Production, 1, 12,
            StageBatchPlanner.Action.PutRemaining, new long[] { 1 },
            new long[] { 10, 11 }, new[] { Row(1) }, "rev");
        Assert.False(result.Saved);
        Assert.Contains("этап", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionDatabaseIsNotEligibleForBulkWrites()
    {
        var result = SqlServerStages.ChangeSelected(Production, 1, 10,
            StageBatchPlanner.Action.PutRemaining, new long[] { 1 },
            new long[] { 10, 11 }, new[] { Row(1) }, "rev");
        Assert.False(result.Saved);
        Assert.Contains("GmcSmetaPilot_", result.Error);
    }

    [Fact]
    public void ClearActionCannotCarryTargetStage()
    {
        var result = SqlServerStages.ChangeSelected(Production, 1, 10,
            StageBatchPlanner.Action.ReturnSelected, new long[] { 1 },
            new long[] { 10, 11 }, new[] { Row(1) }, "rev");
        Assert.False(result.Saved);
    }

    [Fact]
    public void EmptySelectionCannotOpenConnection()
    {
        var result = SqlServerStages.ChangeSelected(Production, 1, null,
            StageBatchPlanner.Action.ReturnSelected, Array.Empty<long>(),
            new long[] { 10 }, Array.Empty<StageBatchPlanner.Snapshot>(), "rev");
        Assert.False(result.Saved);
    }
}
