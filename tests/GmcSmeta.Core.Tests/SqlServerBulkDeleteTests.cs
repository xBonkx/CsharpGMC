using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

/// <summary>Offline guards: invalid batches must be rejected before opening a SQL connection.</summary>
public sealed class SqlServerBulkDeleteTests
{
    [Fact]
    public void DeletePositions_RejectsMissingContract()
    {
        var result = SqlServerDatabase.DeletePositions(
            "not-a-connection", 0, new long[] { 10, 11 }, "rev1");
        Assert.False(result.Saved);
        Assert.Equal("rev1", result.Revision);
    }

    [Fact]
    public void DeletePositions_RejectsEmptySelection()
    {
        var result = SqlServerDatabase.DeletePositions(
            "not-a-connection", 5, Array.Empty<long>(), "rev1");
        Assert.False(result.Saved);
        Assert.Contains("Выбери", result.Error);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-3L)]
    public void DeletePositions_RejectsInvalidPositionId(long positionId)
    {
        var result = SqlServerDatabase.DeletePositions(
            "not-a-connection", 5, new[] { 12L, positionId }, "rev1");
        Assert.False(result.Saved);
    }

    [Fact]
    public void DeletePositions_RejectsExcessiveSelection()
    {
        var ids = Enumerable.Range(1, 10001).Select(x => (long)x).ToArray();
        var result = SqlServerDatabase.DeletePositions("not-a-connection", 5, ids, "rev1");
        Assert.False(result.Saved);
        Assert.Contains("10 000", result.Error);
    }
}
