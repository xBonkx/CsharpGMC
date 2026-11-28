using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class OrderedWriteGateTests
{
    [Fact]
    public async Task ReservedCellBeforeDebounceCannotBeOvertakenByStructure()
    {
        var gate = new OrderedWriteGate();
        using var first = gate.Reserve();
        using var structure = gate.Reserve();
        using var second = gate.Reserve();
        var observed = new List<string>();
        var queue = new ContractCellSaveQueue(async (edit, ticket) =>
        {
            await ticket!.WaitAsync();
            observed.Add("cell-" + edit.PositionId);
            return ContractCellSaveQueue.Receipt.Success("rev-" + edit.PositionId);
        }, TimeSpan.FromMilliseconds(20));
        var structureTurn = structure.WaitAsync();
        Assert.False(structureTurn.IsCompleted);
        var a = queue.Enqueue(new(1, "note", "first"), first);
        var b = queue.Enqueue(new(2, "note", "second"), second);
        await structureTurn.WaitAsync(TimeSpan.FromSeconds(5));
        observed.Add("structure");
        structure.Dispose();
        Assert.True((await a.WaitAsync(TimeSpan.FromSeconds(5))).Saved);
        Assert.True((await b.WaitAsync(TimeSpan.FromSeconds(5))).Saved);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "cell-1", "structure", "cell-2" }, observed);
    }

    [Fact]
    public async Task RejectedBurstDiscardsReservationsWithoutOvertakingInterveningOwner()
    {
        var gate = new OrderedWriteGate();
        using var first = gate.Reserve();
        using var structure = gate.Reserve();
        using var skipped = gate.Reserve();
        using var later = gate.Reserve();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var queue = new ContractCellSaveQueue(async (edit, ticket) =>
        {
            await ticket!.WaitAsync();
            calls++;
            await release.Task;
            return ContractCellSaveQueue.Receipt.Reject("conflict", "foreign");
        }, TimeSpan.Zero);
        var a = queue.Enqueue(new(1, "note", "first"), first);
        var b = queue.Enqueue(new(2, "note", "second"), skipped);
        var structureTurn = structure.WaitAsync();
        var laterTurn = later.WaitAsync();
        release.SetResult(true);
        Assert.False((await a.WaitAsync(TimeSpan.FromSeconds(5))).Saved);
        Assert.False((await b.WaitAsync(TimeSpan.FromSeconds(5))).Saved);
        await queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await structureTurn.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(laterTurn.IsCompleted);
        structure.Dispose();
        await laterTurn.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DiscardedTicketDoesNotOpenNextUntilPredecessorEnds()
    {
        var gate = new OrderedWriteGate();
        using var first = gate.Reserve();
        using var discarded = gate.Reserve();
        using var third = gate.Reserve();
        await first.WaitAsync();
        discarded.Dispose();
        Assert.Throws<InvalidOperationException>(() => discarded.WaitAsync());
        var next = third.WaitAsync();
        Assert.False(next.IsCompleted);
        first.Dispose();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
