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
        Assert.Throws<OperationCanceledException>(() =>
            SqlServerHistory.ReadSnapshot("invalid connection", 1, token: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() =>
            SqlServerHistory.ReadSnapshot("invalid connection", token: cancellation.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3001)]
    public void SnapshotRejectsInvalidLimitsBeforeOpeningSql(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlServerHistory.ReadSnapshot("invalid connection", 1, limit));
    }

    [Fact]
    public void ScopedHistoryReadsUseOneConnectionAndRejectMixedRevisions()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerHistory.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerHistory.cs"));
        var start = source.IndexOf("public static HistorySnapshot ReadSnapshot(", StringComparison.Ordinal);
        var end = source.IndexOf("private static SqlConnection OpenRead(", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var snapshot = source[start..end];
        Assert.Equal(1, snapshot.Split("OpenRead(connectionString, token)", StringSplitOptions.None).Length - 1);
        Assert.Contains("if (contractId is not long id)", snapshot);
        Assert.Contains("List(conn, null, limit, token)", snapshot);
        Assert.Contains("for (var attempt = 0; attempt < 2; attempt++)", snapshot);
        var before = snapshot.IndexOf("var before = Revision();", StringComparison.Ordinal);
        var scope = snapshot.IndexOf("ContractScope(conn, id, token)", StringComparison.Ordinal);
        var rows = snapshot.IndexOf("List(conn, id, limit, token)", StringComparison.Ordinal);
        var after = snapshot.IndexOf("var after = Revision();", StringComparison.Ordinal);
        var guard = snapshot.IndexOf("!string.IsNullOrWhiteSpace(before) && before == after", StringComparison.Ordinal);
        Assert.True(before >= 0 && before < scope && scope < rows && rows < after && after < guard);
        Assert.Contains("scope is null", snapshot);
        Assert.Contains("Array.Empty<HistoryEntry>()", snapshot);
        Assert.Contains("throw new InvalidOperationException", snapshot);
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
        Assert.Contains("Closed += (_, _) => { _loadCancellation?.Cancel(); _searchTimer.Stop(); }", source);
        Assert.Contains("await Task.Run", source);
        Assert.Contains("SqlServerHistory.ReadSnapshot(connectionString, contractId, token: token)", source);
        Assert.Contains("var scope = data.Scope;", source);
        Assert.Contains("var entries = data.Entries;", source);
        Assert.DoesNotContain("SqlServerHistory.ContractScope(", source);
        Assert.DoesNotContain("SqlServerHistory.List(", source);
        Assert.Contains("if (token.IsCancellationRequested || !IsLoaded) return;", source);
        Assert.Contains("_all = snapshot.Rows;", source);
        Assert.Contains("var words = HistoryPresentation.SearchWords(SearchBox.Text);", source);
        Assert.Contains("HistoryPresentation.MatchesSearchWords(words, row.SearchLine)", source);
        Assert.Contains("words.Length == 0 ? _all", source);
        Assert.Contains("public string SearchLine { get; }", source);
    }
}
