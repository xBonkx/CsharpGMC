namespace GmcSmeta.Core.Tests;

public sealed class CostSummaryCommandScopeSourceTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Fact]
    public void StandaloneMarkupDoesNotTreatIneligibleSelectionAsAllRows()
    {
        var source = Source("CostSummaryPane.xaml.cs");
        Assert.Contains("var allRows = SummaryGrid.SelectedItems.Count == 0;", source);
        Assert.Contains("MarkupIds(allRows ? _allRows : selected)", source);
        Assert.Contains("if (ids.Count == 0)", source);
        Assert.Contains("!r.IsTransport && !r.IsGroup && r.Id > 0", source);
        Assert.Contains(".Select(r => r.Id).Distinct().ToList()", source);
    }

    [Fact]
    public void StandaloneMarkupRechecksSelectionAndRevisionBeforeWrite()
    {
        var source = Source("CostSummaryPane.xaml.cs");
        var start = source.IndexOf("private async void Markup_Click", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var method = source[start..];
        var modal = method.IndexOf("dialog.ShowDialog()", StringComparison.Ordinal);
        var operation = method.IndexOf("BeginCostOperation()", StringComparison.Ordinal);
        Assert.True(modal >= 0 && operation > modal);
        var check = method[modal..operation];
        Assert.Contains("revision != _revision", check);
        Assert.Contains("attachment != Interlocked.Read", check);
        Assert.Contains("SetEquals(MarkupIds", check);
        Assert.Contains("_readOnly || !CanUseSnapshot", check);
    }

    [Fact]
    public void ExportCapturesDisplayedValuesBeforeModal()
    {
        var source = Source("CostSummaryPane.xaml.cs");
        var start = source.IndexOf("private void Export_Click", StringComparison.Ordinal);
        var end = source.IndexOf("private async void Markup_Click", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = source[start..end];
        var capture = method.IndexOf("var snapshot =", StringComparison.Ordinal);
        var save = method.IndexOf("XlsxExport.Save", StringComparison.Ordinal);
        Assert.True(capture >= 0 && save > capture);
        Assert.Contains("foreach (var values in snapshot)", method[save..]);
        Assert.DoesNotContain("SummaryGrid.Items", method[save..]);
    }

    [Fact]
    public void EmbeddedSummaryRechecksLockAfterTransferConfirmation()
    {
        var source = Source("EstimateView.CostSummary.cs");
        Assert.Contains("if (answer != MessageBoxResult.Yes || !SummarySnapshotReady ||", source);
        Assert.Contains("_estimate?.Locked != false || revision != _summaryRevision) return;", source);
    }
}
