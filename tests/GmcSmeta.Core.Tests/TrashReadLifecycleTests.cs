using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class TrashReadLifecycleTests
{
    [Fact]
    public void CancelledSnapshotDoesNotOpenSql()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SqlServerTrash.ReadSnapshot("invalid connection", cancellation.Token));
    }

    [Fact]
    public void ReloadInvalidatesRevisionUntilAuthoritativeReadback()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "TrashWindow.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "TrashWindow.xaml.cs"));
        Assert.Contains("Loaded += async", source);
        Assert.Contains("Closed += (_, _) => _loadCancellation?.Cancel()", source);
        Assert.Contains("await Task.Run", source);
        Assert.Contains("SqlServerTrash.ReadSnapshot(_connectionString, token)", source);
        Assert.Contains("if (token.IsCancellationRequested || !IsLoaded) return;", source);
        Assert.Contains("if (!_commitUncertain)", source);
        Assert.Contains("_revision = snapshot.Revision;", source);
        Assert.Contains("_commitUncertain || string.IsNullOrWhiteSpace(_revision)", source);
        Assert.Contains("SqlServerTrash.Restore(_connectionString, id, revision)", source);
        Assert.Contains("SqlServerTrash.Forget(_connectionString, id, revision)", source);
        Assert.Contains("BlockUncertainCommit()", source);
    }
}
