using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class WorkPriceBatchPolicyTests
{
    [Fact]
    public void AllowsRealRootsInPositionSmrMode()
    {
        WorkPriceBatchPolicy.ValidateSelection(5, new long[] { 12, 13 }, 100.25m);
        WorkPriceBatchPolicy.EnsureWritable(5, 5, true, false, "pos");
    }

    [Fact]
    public void InvalidBatchIsRejectedWithoutConnectingToServer()
    {
        Assert.Throws<ArgumentException>(() =>
            WorkPriceBatchPolicy.ValidateSelection(0, new long[] { 12 }, 1m));
        Assert.Throws<ArgumentException>(() =>
            WorkPriceBatchPolicy.ValidateSelection(5, Array.Empty<long>(), 1m));
        Assert.Throws<ArgumentException>(() =>
            WorkPriceBatchPolicy.ValidateSelection(5, new long[] { 12, 12 }, 1m));
        Assert.Throws<ArgumentException>(() =>
            WorkPriceBatchPolicy.ValidateSelection(5, new long[] { -1 }, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WorkPriceBatchPolicy.ValidateSelection(5, new long[] { 12 }, -1m));
    }

    [Fact]
    public void ForeignOrProtectedPositionAbortsEntireBatch()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WorkPriceBatchPolicy.EnsureWritable(5, 6, true, false, "pos"));
        Assert.Throws<InvalidOperationException>(() =>
            WorkPriceBatchPolicy.EnsureWritable(5, 5, false, false, "pos"));
        Assert.Throws<InvalidOperationException>(() =>
            WorkPriceBatchPolicy.EnsureWritable(5, 5, true, true, "pos"));
        Assert.Throws<InvalidOperationException>(() =>
            WorkPriceBatchPolicy.EnsureWritable(5, 5, true, false, "sum"));
    }
}
