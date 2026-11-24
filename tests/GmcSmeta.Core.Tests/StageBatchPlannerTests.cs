using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class StageBatchPlannerTests
{
    private static StageBatchPlanner.Snapshot Row(long id, decimal full, params decimal[] stages) =>
        new(id, full, full - stages.Sum(), stages);

    [Fact]
    public void PutRemaining_OnlyAddsPositiveRestAndNeverClobbersOtherStages()
    {
        var rows = new[]
        {
            Row(101, 10, 2, 3), // 5 remains
            Row(102, 4, 5, 1), // over-allocated; skip
            Row(103, 3, 0, 3), // already fully allocated; skip
        };
        var changes = StageBatchPlanner.Plan(rows, new long[] { 101, 102, 103 },
            2, StageBatchPlanner.Action.PutRemaining, 1);
        var edit = Assert.Single(changes);
        Assert.Equal(101, edit.PositionId);
        Assert.Equal(1, edit.StageIndex);
        Assert.Equal(3m, edit.Before);
        Assert.Equal(8m, edit.After);
        Assert.Equal(new decimal[] { 2, 3 }, rows[0].PerStage);
        Assert.Equal(-2m, rows[1].Remaining);
    }

    [Fact]
    public void ReturnSelected_ClearsAllStagesButOnlySelectedPositions()
    {
        var rows = new[] { Row(1, 10, 2, 3, 0), Row(2, 4, 1, 3, 0) };
        var changes = StageBatchPlanner.Plan(rows, new long[] { 1 }, 3,
            StageBatchPlanner.Action.ReturnSelected);
        Assert.Equal(2, changes.Count);
        Assert.Equal(new[] { 0, 1 }, changes.Select(x => x.StageIndex));
        Assert.All(changes, x => Assert.Equal(1, x.PositionId));
        Assert.All(changes, x => Assert.Equal(0m, x.After));
    }

    [Fact]
    public void SnapshotCheckRejectsChangedThirdPartyStageAndRemainder()
    {
        var before = new[] { Row(1, 10, 2, 3), Row(2, 5, 0, 0) };
        var change = new[] { Row(1, 10, 2, 4), Row(2, 5, 0, 0) };
        Assert.True(StageBatchPlanner.Matches(before, before, new long[] { 1, 2 }, 2));
        Assert.False(StageBatchPlanner.Matches(before, change, new long[] { 1, 2 }, 2));
        Assert.False(StageBatchPlanner.Matches(before, before, new long[] { 1 }, 2));
        Assert.False(StageBatchPlanner.Matches(before, before, new long[] { 1, 3 }, 2));
        Assert.False(StageBatchPlanner.Matches(before, before, new long[] { 1, 1 }, 2));
    }

    [Fact]
    public void AChangedFullQuantityIsAlsoAConflict()
    {
        var before = new[] { Row(7, 5, 1) };
        var changed = new[] { Row(7, 6, 1) };
        Assert.False(StageBatchPlanner.Matches(before, changed, new long[] { 7 }, 1));
    }

    [Fact]
    public void ZeroStageNoOpDoesNotCreateChanges()
    {
        var rows = new[] { Row(1, 10) };
        Assert.Empty(StageBatchPlanner.Plan(rows, new long[] { 1 }, 0,
            StageBatchPlanner.Action.ReturnSelected));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    public void OutOfRangeTargetIsRejected(int target) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StageBatchPlanner.Plan(new[] { Row(1, 5, 0, 0) },
                new long[] { 1 }, 2, StageBatchPlanner.Action.PutRemaining, target));

    [Fact]
    public void MissingAndDuplicateSelectionIsRejected()
    {
        var rows = new[] { Row(1, 5, 0) };
        Assert.Throws<InvalidOperationException>(() =>
            StageBatchPlanner.Plan(rows, new long[] { 99 }, 1,
                StageBatchPlanner.Action.PutRemaining, 0));
        Assert.Throws<ArgumentException>(() =>
            StageBatchPlanner.Plan(rows, new long[] { 1, 1 }, 1,
                StageBatchPlanner.Action.PutRemaining, 0));
        Assert.Throws<ArgumentException>(() =>
            StageBatchPlanner.Plan(rows, Array.Empty<long>(), 1,
                StageBatchPlanner.Action.PutRemaining, 0));
    }

    [Fact]
    public void AStageWithoutOverrunCanAllocateManyRowsByTheirOwnRemain()
    {
        var rows = Enumerable.Range(1, 2501).Select(i =>
            Row(i, 10, 3, 2)).ToArray();
        var selected = rows.Select(x => x.PositionId).ToArray();
        var edits = StageBatchPlanner.Plan(rows, selected, 2,
            StageBatchPlanner.Action.PutRemaining, 1);
        Assert.Equal(2501, edits.Count);
        Assert.All(edits, x => Assert.Equal(7m, x.After));
        Assert.Equal(2, rows[0].PerStage.Count);
    }
}
