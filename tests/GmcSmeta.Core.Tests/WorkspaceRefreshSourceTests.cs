namespace GmcSmeta.Core.Tests;

public sealed class WorkspaceRefreshSourceTests
{
    private static string Source(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", name));
    }

    [Fact]
    public void F5UsesAnIndependentReaderAndRejectsLateResponses()
    {
        var code = Source("MainWindow.Navigation.cs");
        var start = code.IndexOf("private async void RefreshWorkspaceNavigation()", StringComparison.Ordinal);
        var end = code.IndexOf("private void ReloadAndSelect(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var refresh = code[start..end];
        Assert.Contains("_navigationRefreshInProgress", refresh);
        Assert.Contains("await Task.Run", refresh);
        Assert.Contains("using var read = SqlServerDatabase.Open(connection)", refresh);
        Assert.Contains("Workspace.ReadConsistentNavigation(read)", refresh);
        Assert.Contains("navigationGeneration == _estimateOpenGeneration", refresh);
        Assert.Contains("dataGeneration == _navigationDataGeneration", refresh);
        Assert.Contains("!_startupClosed", refresh);
        Assert.True(refresh.IndexOf("if (!IsCurrent())", StringComparison.Ordinal) <
            refresh.IndexOf("ApplyNavigationData(loaded.Objects", StringComparison.Ordinal));
        Assert.Contains("finally { _navigationRefreshInProgress = false; }", refresh);
    }

    [Fact]
    public void RefreshRetainsSearchAndDoesNotRebuildUnchangedTree()
    {
        var code = Source("MainWindow.Navigation.cs");
        Assert.Contains("_objects.SequenceEqual(objects) && _docs.SequenceEqual(documents)", code);
        Assert.Contains("if (changed) SynchronizeNavigation(doc, rebuildTree: true)", code);
        Assert.Contains("estimate.RequestRefresh()", code);
        Assert.Contains("!editing.CanTransferDocument", code);
        Assert.Contains("preserveSearch: true, rebuildTree: changed", code);
        Assert.Contains("if (!preserveSearch) ObjectSearch.Clear()", code);
        Assert.Contains("if (rebuildTree) RefreshNavigation()", code);
        Assert.Contains("_directorView.RequestRefresh()", code);
        Assert.Contains("++_navigationDataGeneration", code);
        Assert.Contains("Workspace.ReadNavigation(reader)", code);
    }

    [Fact]
    public void KsSummaryReusesViewsAndColumnsAndExportsTotalsInOnePass()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ContractActsWorkspace.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "ContractActsWorkspace.xaml.cs"));
        Assert.Contains("_summaryAllRows = null;", code);
        Assert.Contains("_summaryRestRows = null;", code);
        Assert.Contains("_summaryPositionViews.Clear();", code);
        Assert.Contains("_summaryRestRows ??= BuildSummaryRows(true)", code);
        Assert.Contains("_summaryAllRows ??= BuildSummaryRows(false)", code);
        Assert.Contains("_summaryColumnKeys.SequenceEqual(keys)", code);
        Assert.Contains("KsWorkspaceProjection.ExportSummaryGroups(summary)", code);
        Assert.Contains("foreach (var (actId, quantity) in row.PerAct)", code);
        Assert.Contains("estimateSum += amount;", code);
        Assert.Contains("doneSum += row.DoneSumRaw;", code);
        Assert.Contains("leftSum += row.LeftSumRaw;", code);
        Assert.DoesNotContain("doneSumByAct", code);
    }

}
