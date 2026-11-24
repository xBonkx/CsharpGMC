using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>
/// Guard tests that run without a SQL connection. Live transaction rollback,
/// KS protections and rest sync require GmcSmetaPilot_* on the user's PC.
/// </summary>
public sealed class SqlServerStageAtomicWriteTests
{
    [Theory]
    [InlineData(0L, 10L, 1)]
    [InlineData(-3L, 10L, 1)]
    [InlineData(10L, 0L, 1)]
    [InlineData(10L, -2L, 1)]
    [InlineData(10L, 12L, -1)]
    public void UnsafeStageWriteFailsBeforeOpeningConnection(long stageId, long sourceId, int qty)
    {
        var result = SqlServerStages.SetStageQty("invalid-connection-string",
            stageId, sourceId, qty, "current-revision", 0, 1);
        Assert.False(result.Saved);
        Assert.NotNull(result.Error);
        Assert.Equal("current-revision", result.Revision);
    }

    [Fact]
    public void ExcessiveStageQuantityRejectedBeforeSql()
    {
        var result = SqlServerStages.SetStageQty("invalid-connection-string",
            10, 12, NumberText.MaxNumber + 1, "expected");
        Assert.False(result.Saved);
    }

    [Theory]
    [InlineData("1000,5", 1000.5)]
    [InlineData("=3*4", 12)]
    [InlineData("0", 0)]
    public void InlineStageNumberUsesPythonParser(string input, double expected)
    {
        Assert.True(StageAllocationPresentation.TryParseNonnegative(input, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Fact]
    public void OverrunIsNotSilentlyAccepted()
    {
        decimal[] assigned = [2m, 4m];
        Assert.True(StageAllocationPresentation.NeedsOverrunConfirmation(10m, assigned, 0, 7m));
        Assert.False(StageAllocationPresentation.NeedsOverrunConfirmation(10m, assigned, 0, 6m));
    }
}
