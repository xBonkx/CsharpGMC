using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

/// <summary>Pure deterministic fixtures: no SQL connections and no real user data.</summary>
public sealed class ContractCellSaveQueueTests
{
    private static ContractCellSaveQueue.Edit Edit(long id) => new(id, "note", "synthetic-" + id);

    [Fact]
    public async Task BurstWrites_AreFifo_AndOnlyOneWriterRuns()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<long>();
        var active = 0;
        var peak = 0;
        var queue = new ContractCellSaveQueue(async edit =>
        {
            var now = Interlocked.Increment(ref active);
            peak = Math.Max(peak, now);
            order.Add(edit.PositionId);
            if (edit.PositionId == 1)
            {
                started.TrySetResult(true);
                await release.Task;
            }
            Interlocked.Decrement(ref active);
            return ContractCellSaveQueue.Receipt.Success("rev-" + edit.PositionId);
        }, TimeSpan.Zero);

        var a = queue.Enqueue(Edit(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = queue.Enqueue(Edit(2));
        var c = queue.Enqueue(Edit(3));
        Assert.Equal(3, queue.PendingCount);
        Assert.Equal(new long[] { 1 }, order);
        release.TrySetResult(true);
        var receipts = await Task.WhenAll(a, b, c).WaitAsync(TimeSpan.FromSeconds(5));
        await queue.WhenIdleAsync();

        Assert.Equal(new long[] { 1, 2, 3 }, order);
        Assert.Equal(1, peak);
        Assert.All(receipts, receipt => Assert.True(receipt.Saved));
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task Rejection_StopsQueue_WithoutSendingLaterCells()
    {
        var attempted = new List<long>();
        var queue = new ContractCellSaveQueue(edit =>
        {
            attempted.Add(edit.PositionId);
            return Task.FromResult(edit.PositionId == 1
                ? ContractCellSaveQueue.Receipt.Reject("stale meta.rev", "foreign")
                : ContractCellSaveQueue.Receipt.Success("unexpected"));
        }, TimeSpan.FromMilliseconds(30));

        var first = queue.Enqueue(Edit(1));
        var second = queue.Enqueue(Edit(2));
        Assert.False((await first).Saved);
        Assert.False((await second).Saved);
        await queue.WhenIdleAsync();
        Assert.Equal(new long[] { 1 }, attempted);
        Assert.True(queue.Blocked);
        Assert.False((await queue.Enqueue(Edit(3))).Saved);
        Assert.Equal(1, attempted.Count);
        queue.ResumeAfterReadback();
        Assert.False(queue.Blocked);
    }

    [Fact]
    public async Task NetworkError_IsUnknownCommit_NoBlindRetry()
    {
        var attempts = 0;
        var queue = new ContractCellSaveQueue(_ =>
        {
            attempts++;
            throw new IOException("lost COMMIT acknowledgement");
        }, TimeSpan.Zero);

        var first = await queue.Enqueue(Edit(1));
        Assert.False(first.Saved);
        Assert.True(first.UnknownCommit);
        Assert.Contains("lost COMMIT", first.Error);
        Assert.Equal(1, attempts);
        Assert.False((await queue.Enqueue(Edit(2))).Saved);
        Assert.Equal(1, attempts);
        await queue.WhenIdleAsync();
    }

    [Fact]
    public async Task CannotResumeBeforeSqlWriteFinishes()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new ContractCellSaveQueue(async _ =>
        {
            started.TrySetResult(true);
            await release.Task;
            return ContractCellSaveQueue.Receipt.Success("rev");
        }, TimeSpan.Zero);
        var pending = queue.Enqueue(Edit(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => queue.ResumeAfterReadback());
        release.TrySetResult(true);
        Assert.True((await pending).Saved);
        await queue.WhenIdleAsync();
        queue.ResumeAfterReadback();
    }

    [Fact]
    public async Task LargeBurst_WhenOneCellFails_DropsRemainingEditsWithoutBlindRetry()
    {
        const int size = 2500;
        const int firstFailed = 37;
        var attempted = new List<long>();
        var queue = new ContractCellSaveQueue(edit =>
        {
            attempted.Add(edit.PositionId);
            return Task.FromResult(edit.PositionId == firstFailed
                ? ContractCellSaveQueue.Receipt.Reject("conflicting meta.rev", "remote-rev")
                : ContractCellSaveQueue.Receipt.Success("rev-" + edit.PositionId));
        }, TimeSpan.FromMilliseconds(30));

        var receipts = await Task.WhenAll(Enumerable.Range(1, size)
            .Select(i => queue.Enqueue(Edit(i)))).WaitAsync(TimeSpan.FromSeconds(10));
        await queue.WhenIdleAsync();

        Assert.Equal(firstFailed, attempted.Count);
        Assert.Equal(Enumerable.Range(1, firstFailed).Select(i => (long)i), attempted);
        Assert.Equal(firstFailed - 1, receipts.Count(r => r.Saved));
        Assert.All(receipts.Skip(firstFailed - 1), r => Assert.False(r.Saved));
        Assert.Equal(0, queue.PendingCount);
        Assert.True(queue.Blocked);
        Assert.False((await queue.Enqueue(Edit(size + 1))).Saved);
        Assert.Equal(firstFailed, attempted.Count);

        queue.ResumeAfterReadback();
        Assert.True((await queue.Enqueue(Edit(size + 2))).Saved);
        await queue.WhenIdleAsync();
    }

    [Fact]
    public void SameServerDatabaseContract_UsesSameGate()
    {
        var first = ContractSqlWriteLock.For(
            @"Server=.\\SQLEXPRESS;Database=GmcSmetaPilot_Test;Trusted_Connection=True", 42);
        var second = ContractSqlWriteLock.For(
            @"Server=.\\SQLEXPRESS;Database=gmcsmetapilot_test;Trusted_Connection=True", 42);
        var different = ContractSqlWriteLock.For(
            @"Server=.\\SQLEXPRESS;Database=GmcSmetaPilot_Test;Trusted_Connection=True", 43);
        Assert.Same(first, second);
        Assert.NotSame(first, different);
        Assert.Throws<InvalidOperationException>(() =>
            ContractSqlWriteLock.For(@"Server=.;Trusted_Connection=True", 1));
    }

    [Fact]
    public async Task EmptyQueue_IsImmediatelyIdle()
    {
        var queue = new ContractCellSaveQueue(_ =>
            Task.FromResult(ContractCellSaveQueue.Receipt.Success("synthetic")));
        await queue.WhenIdleAsync();
        Assert.False(queue.Blocked);
        Assert.Equal(0, queue.PendingCount);
    }
}
