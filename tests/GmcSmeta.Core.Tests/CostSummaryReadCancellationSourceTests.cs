namespace GmcSmeta.Core.Tests;

public sealed class CostSummaryReadCancellationSourceTests
{
    [Fact]
    public void DetachedOrSupersededSummaryCannotPublishAndCancellationDoesNotTouchWrites()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "CostSummaryPane.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "CostSummaryPane.xaml.cs"));
        Assert.Equal(2, code.Split("_costReadCancellation?.Cancel();", StringSplitOptions.None).Length - 1);
        Assert.Contains("ReadCostSnapshot(_connectionString, _contractId, materialOptions,", code);
        var read = code.IndexOf("private static CostLoadSnapshot ReadCostSnapshot", StringComparison.Ordinal);
        var load = code.IndexOf("private async Task ReloadAsync(bool allowReuse = false, CostDeltaRequest? delta = null)", StringComparison.Ordinal);
        Assert.True(read >= 0 && load > read);
        Assert.True(code[read..load].Split("token.ThrowIfCancellationRequested();", StringSplitOptions.None).Length >= 7);
        Assert.Contains("if (token.IsCancellationRequested || !_attached || generation != _loadGeneration) return;", code);
        Assert.Contains("if (ReferenceEquals(_costReadCancellation, cancellation)) _costReadCancellation = null;", code);
        Assert.Contains("owner.ReloadAsync(); // includes failed/unknown COMMIT; never replay the write", code);
    }

    [Theory]
    [InlineData("CostSummaryPane.xaml.cs", "SqlServerCostSummary.ReadChangedCostRow(conn, contractId")]
    [InlineData("CostSummaryPane.xaml.cs", "delta.Revision == before")]
    [InlineData("CostSummaryPane.xaml.cs", "await ReloadAsync(delta: delta)")]
    [InlineData("CostSummaryPane.Materials.cs", "TimeSpan.FromMilliseconds(150)")]
    [InlineData("EstimateView.CostSummary.cs", "if (_summarySearchDirty) SummaryRebuild();")]
    [InlineData("EstimateView.Materials.cs", "if (_materialsSearchDirty) MaterialsApplyViewFilter();")]
    [InlineData("EstimateView.Materials.cs", "visible.Contains(row)")]
    [InlineData("CostSummaryPane.xaml.cs", "activeTab == 1 && expenses is null")]
    [InlineData("CostSummaryPane.xaml.cs", "activeTab == 3 && totals is null")]
    [InlineData("CostSummaryPane.xaml.cs", "activeTab == 0 && materials is null")]
    [InlineData("CostSummaryPane.xaml.cs", "cached.Culture == CultureInfo.CurrentCulture.Name")]
    [InlineData("CostSummaryPane.xaml.cs", "_publishedCostTab == CostTabs.SelectedIndex")]
    [InlineData("CostSummaryPane.xaml.cs", "CostTabs.SelectedIndex != activeTab")]
    [InlineData("CostSummaryPane.xaml.cs", "grid.Items is System.ComponentModel.IEditableCollectionView { IsEditingItem: true }")]
    [InlineData("CostSummaryPane.xaml.cs", "SqlServerDatabase.OpenAsync(connectionString, token)")]
    [InlineData("CostSummaryPane.xaml.cs", "SqlServerCostTotals.SummaryAndTotals(conn, contractId, token)")]
    [InlineData("CostSummaryPane.xaml.cs", "SqlServerSmrExpenses.List(conn, contractId, token)")]
    [InlineData("CostSummaryPane.xaml.cs", "cmd.ExecuteScalarAsync(token)")]
    [InlineData("CostSummaryPane.xaml.cs", "BuildMaterialRows(summary, rows, materialOptions, token)")]
    [InlineData("CostSummaryPane.xaml.cs", "data.MaterialOptions == CaptureMaterialOptions()")]
    [InlineData("CostSummaryPane.ViewState.cs", "duplicates.Add(key)")]
    [InlineData("CostSummaryPane.ViewState.cs", "group.MemberIds.OrderBy(id => id)")]
    [InlineData("CostSummaryPane.ViewState.cs", "generation != _loadGeneration")]
    [InlineData("CostSummaryPane.ViewState.cs", "ReferenceEquals(grid.ItemsSource, source)")]
    [InlineData("CostSummaryPane.ViewState.cs", "target.ScrollToVerticalOffset(vertical)")]
    [InlineData("CostSummaryPane.Materials.cs", "ReplaceCostRows(MaterialsGrid, result)")]
    [InlineData("CostSummaryPane.Materials.cs", "_materialGroups.Get(_summaryData, options.Mode, options.Sort)")]
    [InlineData("CostSummaryPane.Materials.cs", "ReferenceEquals(MaterialsGrid.ItemsSource, _lastMaterialSource)")]
    [InlineData("CostSummaryPane.xaml.cs", "ResetMaterialProjection();")]
    [InlineData("CostSummaryPane.xaml.cs", "StartupTrace.Measure(\"cost_smr_bind\")")]
    [InlineData("CostSummaryPane.xaml.cs", "StartupTrace.Measure(\"cost_totals_bind\")")]
    public void SummaryUsesCancellableProjectionAndIdentityRestore(string file, string expected)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        Assert.Contains(expected, File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file)));
    }
}
