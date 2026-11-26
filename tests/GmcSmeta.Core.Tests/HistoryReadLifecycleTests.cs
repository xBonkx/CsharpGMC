using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class HistoryReadLifecycleTests
{
    [Fact]
    public void CancelledReadsDoNotOpenSql()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SqlServerHistory.ContractScope("invalid connection", 1, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            SqlServerHistory.List("invalid connection", token: cancellation.Token));
    }

    [Fact]
    public void HistoryWindowLoadsOnWorkerAndDoesNotPublishAfterClose()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "HistoryWindow.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "HistoryWindow.xaml.cs"));
        Assert.Contains("Loaded += async", source);
        Assert.Contains("Closed += (_, _) => _loadCancellation?.Cancel()", source);
        Assert.Contains("await Task.Run", source);
        Assert.Contains("SqlServerHistory.ContractScope(connectionString, id, token)", source);
        Assert.Contains("SqlServerHistory.List(connectionString, contractId, token: token)", source);
        Assert.Contains("if (token.IsCancellationRequested || !IsLoaded) return;", source);
        Assert.Contains("_all.AddRange(snapshot.Rows)", source);
        Assert.Contains("public string SearchLine { get; }", source);
    }
}
