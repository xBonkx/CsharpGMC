using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class StageParityStressTests
{
    [Theory]
    [InlineData(2500)]
    [InlineData(10000)]
    public void TenThousandRowsCanBePlannedAndReplayedWithoutDoubleAllocation(int count)
    {
        var rows = Enumerable.Range(1, count)
            .Select(i => new StageBatchPlanner.Snapshot(i, 10m, 5m,
                new decimal[] { 2m, 3m, 0m })).ToArray();
        var selected = rows.Select(r => r.PositionId).ToArray();
        Assert.True(StageBatchPlanner.Matches(rows, rows, selected, 3));

        var first = StageBatchPlanner.Plan(rows, selected, 3,
            StageBatchPlanner.Action.PutRemaining, targetIndex: 0);
        Assert.Equal(count, first.Count);
        Assert.All(first, step =>
        {
            Assert.Equal(2m, step.Before);
            Assert.Equal(7m, step.After);
        });

        var after = rows.Select(r => new StageBatchPlanner.Snapshot(r.PositionId,
            r.FullQty, 0m, new decimal[] { 7m, 3m, 0m })).ToArray();
        var replay = StageBatchPlanner.Plan(after, selected, 3,
            StageBatchPlanner.Action.PutRemaining, targetIndex: 0);
        Assert.Empty(replay);
    }

    [Fact]
    public void StaleStageSnapshotAtTheLastRowRejectsEntireBatch()
    {
        var expected = Enumerable.Range(1, 10000)
            .Select(i => new StageBatchPlanner.Snapshot(i, 10m, 5m,
                new decimal[] { 2m, 3m })).ToArray();
        var actual = expected.Select(r => r.PositionId == 10000
            ? r with { Remaining = 4.999m }
            : r).ToArray();
        var ids = expected.Select(x => x.PositionId).ToArray();
        Assert.False(StageBatchPlanner.Matches(expected, actual, ids, 2));
    }

    [Fact]
    public void UnsafeStageMoneyIsRejectedBeforeOpeningAnySqlConnection()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlServerStages.SaveStageMoney("not-a-connection", 1, 2,
                -1m, 100m, null, "rev"));
        Assert.Throws<ArgumentException>(() =>
            SqlServerStages.SaveStageSystems("not-a-connection", 1, 2,
                new Dictionary<long, decimal> { [13] = -1m }, "rev"));
    }

    [Fact]
    public void IndexedSystemSelectionMatchesFullScansAcrossTwentyFiveThousandRows()
    {
        var rows = Enumerable.Range(1, 25000)
            .Select(i => (SystemId: (long)(i % 251), SourceId: (long)i)).ToArray();
        var index = StageAllocationPresentation.IndexSourcesBySystem(rows);
        var selectedSystems = new long[] { 0, 7, 42, 42, 250, 999 };
        var expected = selectedSystems.SelectMany(id => rows.Where(r => r.SystemId == id)
            .Select(r => r.SourceId)).Distinct().OrderBy(id => id);
        var actual = new HashSet<long>();
        foreach (var id in selectedSystems) actual.UnionWith(index[id]);
        Assert.Equal(expected, actual.OrderBy(id => id));
        Assert.Empty(index[999]);
    }
}
