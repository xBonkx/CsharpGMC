namespace GmcSmeta.Core.Tests;

public sealed class StagesDialogLifecycleTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", file));
    }

    [Fact]
    public void InitialAndPostWriteReadbacksUseWorkerAndPublishOnlyConfirmedRevision()
    {
        var code = Source("GmcSmeta.App/StagesDialog.xaml.cs");
        Assert.Contains("Loaded += InitialLoadAsync;", code);
        Assert.Contains("var data = await Task.Run", code);
        Assert.Contains("revision != SqlServerDatabase.Revision(conn)", code);
        Assert.Contains("if (_closed) return;", code);
        Assert.Contains("Table: BuildTable(stages, rows)", code);
        Assert.Contains("PublishSnapshot(data.Stages, data.Remaining, data.Table, data.Revision)", code);
        Assert.Contains("if (_quantityWriteBusy) e.Cancel = true;", code);
        Assert.Contains("await ReloadAsync();", code);
        Assert.DoesNotContain("private void Reload()", code);
        var inline = Source("GmcSmeta.App/EstimateView.InlineStages.cs");
        Assert.Contains("dialog.InitialSnapshotReady += () =>", inline);
        Assert.Contains("dialog.InvokeInlineMenuAction(id, action, snapshot.Revision)", inline);
    }

    [Fact]
    public void SearchUsesLocalViewAndRetainsExactValues()
    {
        var code = Source("GmcSmeta.App/StagesDialog.xaml.cs");
        Assert.Contains("TimeSpan.FromMilliseconds(150)", code);
        Assert.Contains("_table.DefaultView.RowFilter = expression;", code);
        Assert.Contains("CollectionViewSource.GetDefaultView(StagesGrid.ItemsSource)", code);
        Assert.Contains("IEditableCollectionView { IsEditingItem: true }", code);
        Assert.Contains("record[\"__QtyExact\"] = row.Qty;", code);
        Assert.Contains("record[$\"__S{i}\"] = row.PerStage[i];", code);
        Assert.Contains("record[\"__RestExact\"] = row.Rest;", code);
    }

    [Fact]
    public void DistributionReadBatchesAllocationsButMutationPathKeepsTransactionQueries()
    {
        var sql = Source("GmcSmeta.Core/Db/SqlServerStages.cs");
        Assert.Contains("ReadStageTableRows(conn, fullId, stages, tx, batchRead: false)", sql);
        Assert.Contains("ReadStageTableRows(conn, fullId, stages, null, batchRead: true)", sql);
        Assert.Contains("WHERE c.stage_of=@full AND ISNULL(c.stage_rest,0)=0", sql);
        Assert.Contains("!map.TryAdd(srcId, qty)", sql);
        Assert.Contains("p.parent_id IS NULL AND p.out_of_kp=0", sql);
    }
}
