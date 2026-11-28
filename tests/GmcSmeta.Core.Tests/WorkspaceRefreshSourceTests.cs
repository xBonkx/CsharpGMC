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
        Assert.Contains("SqlServerDatabase.OpenAsync(connection, token)", refresh);
        Assert.Contains("Workspace.ReadConsistentNavigation(read, token)", refresh);
        Assert.Contains("navigationGeneration == _estimateOpenGeneration", refresh);
        Assert.Contains("dataGeneration == _navigationDataGeneration", refresh);
        Assert.Contains("!_startupClosed", refresh);
        Assert.True(refresh.IndexOf("if (!IsCurrent())", StringComparison.Ordinal) <
            refresh.IndexOf("ApplyNavigationData(loaded.Objects", StringComparison.Ordinal));
        Assert.Contains("_navigationRefreshInProgress = false;", refresh);
        Assert.Contains("ReferenceEquals(_navigationRefreshCancellation, cancellation)", refresh);
        Assert.Contains("catch (Exception) when (token.IsCancellationRequested)", refresh);
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
        var workbook = Source("KsProgressWorkbook.cs");
        Assert.Contains("KsWorkspaceProjection.ExportSummaryGroups(summary)", workbook);
        Assert.Contains("foreach (var (actId, quantity) in row.PerAct)", workbook);
        Assert.Contains("estimateSum += amount;", workbook);
        Assert.Contains("doneSum += row.DoneSumRaw;", workbook);
        Assert.Contains("leftSum += row.LeftSumRaw;", workbook);
        Assert.DoesNotContain("doneSumByAct", workbook);
    }

    [Fact]
    public void KsProgressExportStagesOffUiAndRejectsSupersededSummary()
    {
        var workspace = Source("ContractActsWorkspace.xaml.cs");
        var export = Source("ContractActsWorkspace.Export.cs");
        var workbook = Source("KsProgressWorkbook.cs");
        Assert.Contains("_summaryExportCancellation?.Cancel();", workspace);
        Assert.Contains("if (_summaryExportCancellation is not null || _summaryCancellation is not null ||", export);
        Assert.Contains("var acts = _summaryActs.OrderBy", export);
        Assert.Contains("version == _summaryVersion", export);
        Assert.Contains("ReferenceEquals(summary, _summary)", export);
        Assert.Contains("await Task.Run(() => ExportFilePublication.Stage", export);
        var stage = export.IndexOf("await Task.Run(() => ExportFilePublication.Stage", StringComparison.Ordinal);
        var publish = export.IndexOf("await Task.Run(prepared.Publish)", StringComparison.Ordinal);
        Assert.True(stage >= 0 && publish > stage);
        Assert.Contains("if (!IsCurrent()) return;", export[stage..publish]);
        Assert.Contains("await Task.Run(prepared.Dispose)", export);
        Assert.Contains("token.ThrowIfCancellationRequested();", workbook);
        Assert.DoesNotContain("SummaryGrid", workbook);
        Assert.DoesNotContain("SqlServerDatabase.Open", workbook);
    }


    [Fact]
    public void AllActsOverviewPreparesSearchOffUiAndDoesNotRebindEquivalentResults()
    {
        var code = Source("AllContractActsWindow.xaml.cs");
        Assert.Contains("if (!_initialized || _closed || _loading) return;", code);
        Assert.Contains("new KsOverviewSearchIndex(grouped)", code);
        Assert.Contains("if (token.IsCancellationRequested || _closed || version != _requestVersion) return;", code);
        Assert.Contains("_visibleGroups = null;", code);
        Assert.Contains("SameVisibleGroups(_visibleGroups, groups)", code);
        Assert.Contains("_visibleRows.ContainsKey(_selectedActId)", code);
        Assert.Contains("SetSelectedAct(visible?.Info)", code);
        Assert.Contains("_rowViews.Clear();", code);
    }

}
