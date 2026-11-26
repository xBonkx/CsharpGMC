using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateCachePolicyTests
{
    [Fact]
    public void ThreeSmallViewsRemainCached()
    {
        Assert.Empty(EstimateCachePolicy.SelectEvictions(new EstimateCachePolicy.Entry[]
        {
            new(1, 2000, true), new(2, 3000, true), new(3, 5000, false)
        }));
    }

    [Fact]
    public void FourthViewEvictsLeastRecentlyUsed()
    {
        Assert.Equal(new[] { 1 }, EstimateCachePolicy.SelectEvictions(new EstimateCachePolicy.Entry[]
        {
            new(1, 100, true), new(2, 100, true), new(3, 100, true), new(4, 100, false)
        }));
    }

    [Fact]
    public void TwoLargeViewsCanExceedRowBudget()
    {
        Assert.Equal(new[] { 1 }, EstimateCachePolicy.SelectEvictions(new EstimateCachePolicy.Entry[]
        {
            new(1, 15000, true), new(2, 15000, false)
        }));
    }

    [Fact]
    public void HugeActiveViewIsNeverRemoved()
    {
        Assert.Equal(new[] { 1, 2 }, EstimateCachePolicy.SelectEvictions(new EstimateCachePolicy.Entry[]
        {
            new(1, 10, true), new(2, 10, true), new(3, 100000, false)
        }));
        Assert.False(EstimateCachePolicy.IsOverBudget(1, 100000));
    }

    [Fact]
    public void DirtyOrActiveViewsPinCacheEvenOverBudget()
    {
        Assert.Equal(new[] { 2 }, EstimateCachePolicy.SelectEvictions(new EstimateCachePolicy.Entry[]
        {
            new(1, 15000, false), new(2, 10000, true), new(3, 15000, false), new(4, 15000, false)
        }));
        Assert.Empty(EstimateCachePolicy.SelectEvictions(new EstimateCachePolicy.Entry[]
        {
            new(1, 15000, false), new(3, 15000, false), new(4, 15000, false)
        }));
    }

    [Fact]
    public void UiPreservesExistingReleaseAndWriteGuards()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "MainWindow.EstimateCache.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var cache = File.ReadAllText(Path.Combine(app, "MainWindow.EstimateCache.cs"));
        var view = File.ReadAllText(Path.Combine(app, "EstimateView.xaml.cs"));
        Assert.Contains("!ReferenceEquals(PageHost.Content, view)", cache);
        Assert.Contains("!view.HasUnsettledCacheWrites && view.CanReleaseCachedView", cache);
        Assert.Contains("id != protectedId", cache);
        Assert.Contains("EstimateCachePolicy.SelectEvictions", cache);
        Assert.Contains("!HasNestedCacheActivity;", view);
        Assert.Contains("main.ScheduleEstimateCacheTrim();", view);
    }
}
