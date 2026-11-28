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
        Assert.Contains("ExportButton.IsEnabled = !_exportBusy;", source);
        Assert.Contains("if (!_initializing && _result is not null) ShowResult();", source);
        Assert.Contains("CompareDisplayProjection.Build(result, token)", source);
        Assert.Contains("_display = null;", source);
        Assert.Contains("display.AllRows : display.ChangedRows", source);
        Assert.DoesNotContain("new ObservableCollection<ComparePreviewRow>", source);
    }

    [Fact]
    public void ComparisonReadsShareRevisionFenceAndOnlyOneWorkerRuns()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerCompare.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var sql = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerCompare.cs"));
        Assert.Contains("var revision = Revision();", sql);
        Assert.Contains("if (revision != Revision())", sql);
        Assert.Contains("SqlServerDatabase.RevisionAsync(conn, token)", sql);
        var ui = File.ReadAllText(Path.Combine(root.FullName, "src", "GmcSmeta.App", "CompareDialog.xaml.cs"));
        Assert.Contains("await _readGate.WaitAsync(token)", ui);
        Assert.Contains("finally { _readGate.Release(); }", ui);
    }

    [Fact]
    public void ExportStagesOnWorkerAndRejectsSupersededSnapshotBeforePublishing()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "CompareDialog.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var ui = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "CompareDialog.xaml.cs"));
        var stage = ui.IndexOf("await Task.Run(() => ExportFilePublication.Stage", StringComparison.Ordinal);
        Assert.True(stage >= 0);
        var guard = ui.IndexOf("!ReferenceEquals(res, _result)) return;", stage, StringComparison.Ordinal);
        var publish = ui.IndexOf("prepared.Publish();", stage, StringComparison.Ordinal);
        Assert.True(guard > stage && publish > guard);
        Assert.Contains("_exportCancellation?.Cancel();", ui);
        Assert.Contains("sheet.Range(5, 6, row - 1, 10)", ui);
        Assert.Contains("if (!_closed) ExportButton.IsEnabled = _result is not null;", ui);
    }
}
