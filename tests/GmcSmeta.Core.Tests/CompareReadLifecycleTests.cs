using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class CompareReadLifecycleTests
{
    [Fact]
    public void CancelledComparisonDoesNotOpenSql()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SqlServerCompare.Compare("invalid connection", 1, 2, cancellation.Token));
    }

    [Fact]
    public void ComparisonRejectsStaleResultsAndFiltersLocally()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "CompareDialog.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "CompareDialog.xaml.cs"));
        Assert.Contains("Loaded += async", source);
        Assert.Contains("_loadCancellation?.Cancel()", source);
        Assert.Contains("await Task.Run", source);
        Assert.Contains("SqlServerCompare.Compare(_connectionString, a.Id, b.Id, token)", source);
        Assert.Contains("!ReferenceEquals(_loadCancellation, cancellation)", source);
        Assert.Contains("_result = null;", source);
        Assert.Contains("ExportButton.IsEnabled = false;", source);
        Assert.Contains("ExportButton.IsEnabled = true;", source);
        Assert.Contains("if (!_initializing && _result is not null) ShowResult();", source);
    }
}
