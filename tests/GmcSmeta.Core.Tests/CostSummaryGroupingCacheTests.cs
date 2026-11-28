using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class CostSummaryGroupingCacheTests
{
    private static CostSummaryData Data(string label) => new(
        new[] { (1L, label, false) }, Array.Empty<CostSummaryRow>(),
        Array.Empty<(string, decimal, decimal, string)>(), Array.Empty<string>(), 0m);

    [Fact]
    public void ReusesOnlySameSnapshotModeSortAndClears()
    {
        var cache = new CostSummaryGroupingCache();
        var data = Data("A");
        var first = cache.Get(data, "kp", "kp");
        Assert.Same(first, cache.Get(data, "kp", "kp"));
        var changedSort = cache.Get(data, "kp", "name");
        Assert.NotSame(first, changedSort);
        Assert.NotSame(changedSort, cache.Get(data, "same", "name"));
        Assert.NotSame(first, cache.Get(data, "kp", "kp")); // one entry, not 28 retained
        var next = Data("B");
        var nextGroups = cache.Get(next, "kp", "kp");
        Assert.Equal("B", Assert.Single(nextGroups).Label);
        Assert.NotSame(nextGroups, cache.Get(next with { }, "kp", "kp"));
        cache.Clear();
        Assert.NotSame(nextGroups, cache.Get(next, "kp", "kp"));
    }
}
