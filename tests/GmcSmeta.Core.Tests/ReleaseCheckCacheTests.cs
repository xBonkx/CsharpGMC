using GmcSmeta.Launcher;

namespace GmcSmeta.Core.Tests;

public sealed class ReleaseCheckCacheTests
{
    private static readonly ReleaseCheckKey Key = new("owner/repo", "1.2.3", "fx",
        "app.exe", "2026-11-28", "1.2.3", "fx", false);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    public void TtlRejectsFutureAndExpiresAtFiveMinutes(int seconds, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(expected, new ReleaseCheckCache(Key, null, now).IsFresh(now.AddSeconds(seconds)));
    }

    [Theory]
    [InlineData("\"abc\"", true)]
    [InlineData("W/\"abc\"", true)]
    [InlineData("*", false)]
    [InlineData("not-an-etag", false)]
    [InlineData(null, false)]
    public void OnlySpecificValidEtagsAreSent(string? value, bool expected)
        => Assert.Equal(expected, ReleaseCheckCache.ParseETag(value) is not null);

    [Fact]
    public void CacheIsScopedToExactInstallationAndPolicy()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var now = DateTimeOffset.UtcNow.AddSeconds(-1);
            ReleaseCheckCache.Save(path, Key, "W/\"revision\"", now);
            var state = ReleaseCheckCache.Load(path, Key);
            Assert.NotNull(state);
            Assert.Equal("W/\"revision\"", state!.ETag);
            Assert.Equal(now, state.CheckedUtc);
            Assert.Null(ReleaseCheckCache.Load(path, Key with { Repository = "other/repo" }));
            Assert.Null(ReleaseCheckCache.Load(path, Key with { Version = "2.0.0" }));
            Assert.Null(ReleaseCheckCache.Load(path, Key with { Flavor = "sc" }));
            Assert.Null(ReleaseCheckCache.Load(path, Key with { Exe = "other.exe" }));
            Assert.Null(ReleaseCheckCache.Load(path, Key with { InstalledUtc = "later" }));
            Assert.Null(ReleaseCheckCache.Load(path, Key with { LauncherVersion = "1.0.0" }));
            Assert.Null(ReleaseCheckCache.Load(path, Key with { PreferredFlavor = "sc" }));
            Assert.Null(ReleaseCheckCache.Load(path, Key with { NoSelfUpdate = true }));
            ReleaseCheckCache.Save(path, Key, "*", now);
            Assert.Null(ReleaseCheckCache.Load(path, Key)!.ETag);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CorruptOversizedAndFutureCacheAreIgnored()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            Assert.Null(ReleaseCheckCache.Load(path, Key));
            File.WriteAllText(path, "{broken");
            Assert.Null(ReleaseCheckCache.Load(path, Key));
            File.WriteAllText(path, new string('x', 17000));
            Assert.Null(ReleaseCheckCache.Load(path, Key));
            ReleaseCheckCache.Save(path, Key, null, DateTimeOffset.UtcNow.AddHours(1));
            Assert.Null(ReleaseCheckCache.Load(path, Key));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnwritableCacheIsNotAnUpdateFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "release.json");
        ReleaseCheckCache.Save(path, Key, null, DateTimeOffset.UtcNow);
        Assert.Null(ReleaseCheckCache.Load(path, Key));
    }

    [Fact]
    public void WorkerUsesTtlAndConditionalGetButExplicitUpdateDoesNot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        const string relative = "tools/launcher/GmcSmeta.Launcher/";
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relative, "Program.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var program = File.ReadAllText(Path.Combine(root!.FullName, relative, "Program.cs"));
        var http = File.ReadAllText(Path.Combine(root.FullName, relative, "GitHubReleases.cs"));
        Assert.Contains("options.BackgroundUpdate && installed is not null", program);
        Assert.Contains("TryGetLatest(cached?.ETag", program);
        Assert.Contains("if (launcherReady && installed is not null)", program);
        Assert.True(program.IndexOf("launcher_release_check_ttl_hit", StringComparison.Ordinal) <
            program.IndexOf("GitHubAuth.Resolve", StringComparison.Ordinal));
        Assert.Contains("request.Headers.IfNoneMatch.Add(conditional)", http);
        Assert.Contains("HttpStatusCode.NotModified", http);
        Assert.Contains("headerTimeout.CancelAfter(TimeSpan.FromSeconds(30))", http);
        Assert.Contains("if (conditional is null) throw", http);
    }
}
