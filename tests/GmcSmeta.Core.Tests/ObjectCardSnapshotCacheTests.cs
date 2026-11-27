using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ObjectCardSnapshotCacheTests
{
    private static ObjectDocuments Empty() =>
        new(Array.Empty<ObjectDocumentCard>(), 0m, 0m, 0m, 0m);

    [Fact]
    public void CapacityEvictsOnlyLeastRecentlyAcceptedObject()
    {
        var cache = new ObjectCardSnapshotCache();
        var data = Empty();
        for (var id = 1; id <= 24; id++)
            cache.Store("profile", id, NavigationMode.Kp, "7", data);
        cache.Store("profile", 1, NavigationMode.Kp, "7", data);
        cache.Store("profile", 25, NavigationMode.Kp, "7", data);
        Assert.Null(cache.Find("profile", 2, NavigationMode.Kp));
        Assert.Same(data, cache.Find("profile", 1, NavigationMode.Kp)!.Documents);
        Assert.Same(data, cache.Find("profile", 3, NavigationMode.Kp)!.Documents);
        Assert.Same(data, cache.Find("profile", 25, NavigationMode.Kp)!.Documents);
    }

    [Fact]
    public void RevisionProfileAndModeAreIndependent()
    {
        var cache = new ObjectCardSnapshotCache();
        var kp = Empty();
        var contracts = Empty();
        cache.Store("a", 1, NavigationMode.Kp, "7", kp);
        cache.Store("a", 1, NavigationMode.Contracts, "7", contracts);
        Assert.Same(kp, cache.Find("a", 1, NavigationMode.Kp)!.Documents);
        Assert.Same(contracts, cache.Find("a", 1, NavigationMode.Contracts)!.Documents);
        Assert.Null(cache.Find("b", 1, NavigationMode.Kp));
        cache.Store("a", 2, NavigationMode.Kp, "8", kp);
        Assert.Null(cache.Find("a", 1, NavigationMode.Contracts));
        cache.Store("b", 2, NavigationMode.Kp, "8", contracts);
        Assert.Null(cache.Find("a", 2, NavigationMode.Kp));
        Assert.Same(contracts, cache.Find("b", 2, NavigationMode.Kp)!.Documents);
    }

    [Fact]
    public void EmptyRevisionCannotBeCached()
    {
        var cache = new ObjectCardSnapshotCache();
        Assert.Throws<ArgumentException>(() => cache.Store("a", 1, NavigationMode.Kp, "", Empty()));
    }
}
