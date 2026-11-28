using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class WorkspaceNavigationPolicyTests
{
    [Theory]
    [InlineData(true, "kp", NavigationMode.Archive)]
    [InlineData(false, "kp", NavigationMode.Kp)]
    [InlineData(false, "main", NavigationMode.Contracts)]
    [InlineData(false, "dop", NavigationMode.Contracts)]
    public void DocumentRoutePreservesArchiveAndKind(bool archived, string kind, NavigationMode expected) =>
        Assert.Equal(expected, WorkspaceNavigationPolicy.ForDocument(archived, kind));

    [Theory]
    [InlineData(false, NavigationMode.Contracts, "main,kp", NavigationMode.Contracts)]
    [InlineData(false, NavigationMode.Kp, "main,kp", NavigationMode.Kp)]
    [InlineData(false, NavigationMode.Archive, "main,kp", NavigationMode.Kp)]
    [InlineData(false, NavigationMode.Contracts, "kp", NavigationMode.Kp)]
    [InlineData(false, NavigationMode.Kp, "dop", NavigationMode.Contracts)]
    [InlineData(false, NavigationMode.Contracts, "", NavigationMode.Kp)]
    [InlineData(true, NavigationMode.Kp, "main,kp", NavigationMode.Archive)]
    public void ObjectRouteRetainsCurrentAvailableMode(bool archived, NavigationMode current,
        string kinds, NavigationMode expected) =>
        Assert.Equal(expected, WorkspaceNavigationPolicy.ForObject(archived, current,
            kinds.Split(',', StringSplitOptions.RemoveEmptyEntries)));

    [Theory]
    [InlineData(1, 1, "a", "a", false, true)]
    [InlineData(1, 2, "a", "a", false, false)]
    [InlineData(1, 1, "a", "b", false, false)]
    [InlineData(1, 1, "a", null, false, false)]
    [InlineData(1, 1, "a", "a", true, false)]
    public void OldNavigationReadCannotPublish(long captured, long current, string connection,
        string? active, bool closed, bool expected) =>
        Assert.Equal(expected, WorkspaceNavigationPolicy.CanPublishData(captured, current, connection, active, closed));

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void CachedEstimateReplacementRequiresSettledEditor(bool transfer, bool unsettled, bool busy, bool expected) =>
        Assert.Equal(expected, WorkspaceNavigationPolicy.CanReplaceCachedEstimate(transfer, unsettled, busy));

    [Fact]
    public void PostWriteRefreshFencesPublicationAndCacheRechecksCurrentMetadata()
    {
        var nav = Source("MainWindow.Navigation.cs");
        var start = nav.IndexOf("private async Task ReloadNavigationDataAsync()", StringComparison.Ordinal);
        var end = nav.IndexOf("private async Task ReloadNavigationAfterWriteAsync", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var read = nav[start..end];
        var worker = read.IndexOf("await Task.Run", StringComparison.Ordinal);
        var guard = read.IndexOf("WorkspaceNavigationPolicy.CanPublishData", StringComparison.Ordinal);
        var publish = read.IndexOf("ApplyNavigationData(loaded.Objects, loaded.Documents)", StringComparison.Ordinal);
        Assert.True(worker >= 0 && guard > worker && publish > guard);

        var main = Source("MainWindow.xaml.cs");
        var probe = main.IndexOf("StartupTrace.Mark(\"estimate_cache_validation_end\")", StringComparison.Ordinal);
        var latest = main.IndexOf("var latest = _docs.FirstOrDefault", probe, StringComparison.Ordinal);
        var reuse = main.IndexOf("cached.MatchesDocumentMetadata(doc)", latest, StringComparison.Ordinal);
        var replace = main.IndexOf("WorkspaceNavigationPolicy.CanReplaceCachedEstimate", reuse, StringComparison.Ordinal);
        var remove = main.IndexOf("_estimateCache.Remove(contractId)", replace, StringComparison.Ordinal);
        Assert.True(probe >= 0 && latest > probe && reuse > latest && replace > reuse && remove > replace);
    }

    private static string Source(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", name)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", name));
    }
}
