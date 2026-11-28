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
        Assert.Contains("revision != await SqlServerDatabase.RevisionAsync(conn, token)", code);
        Assert.Contains("if (_closed) return;", code);
        Assert.Contains("Table: BuildTable(stages, rows, token)", code);
        Assert.Contains("PublishSnapshot(data.Stages, data.Remaining, data.Table, data.Revision)", code);
        Assert.Contains("if (_quantityWriteBusy) e.Cancel = true;", code);
        Assert.Contains("await ReloadAsync();", code);
        Assert.DoesNotContain("private void Reload()", code);
        var inline = Source("GmcSmeta.App/EstimateView.InlineStages.cs");
        Assert.DoesNotContain("new StagesDialog(", inline);
        var commands = Source("GmcSmeta.App/EstimateView.InlineStages.Commands.cs");
        Assert.Contains("ReferenceEquals(snapshot, _inlineStagesData)", commands);
        Assert.Contains("snapshot.Revision != _serverRevision", commands);
        Assert.Contains("await Task.Run(write)", commands);
        Assert.Contains("_inlineStagesData = null", commands);
    }

    [Fact]
    public void StageEntryPointsStayInsideEstimateAndUseDirectCommands()
    {
        var xaml = Source("GmcSmeta.App/EstimateView.xaml");
        Assert.Contains("Click=\"InlineNewStage_Click\"", xaml);
        Assert.Contains("Click=\"InlineCreateRest_Click\"", xaml);
        Assert.DoesNotContain("OpenStagesDialog_Click", xaml);
        var main = Source("GmcSmeta.App/MainWindow.SqlServer.cs");
        Assert.Contains("showStages: true", main);
        Assert.DoesNotContain("new StagesDialog(", main);
        var commands = Source("GmcSmeta.App/EstimateView.InlineStages.Commands.cs");
        Assert.DoesNotContain("new StagesDialog(", commands);
        foreach (var method in new[] { "CreateStage", "CreateRest", "RenameStage", "SaveStageMoney",
            "SaveStageSystems", "PutRest", "ClearStage", "DeleteStage" })
            Assert.Contains("SqlServerStages." + method + "(", commands);
    }

    [Fact]
    public void InlineColumnsAreVisibleAndAppendedAfterCoherentDocumentLoad()
    {
        var code = Source("GmcSmeta.App/EstimateView.InlineStages.cs");
        Assert.Contains("if (!_isKp || _inlineStagesOn || _estimate is null || _detached) return;", code);
        Assert.Contains("data.Revision != _serverRevision", code);
        Assert.Contains("i <= data.Stages.Count", code);
        Assert.Contains("Visibility = Visibility.Visible", code);
        Assert.Contains("EstimateGrid.Columns.Add(column);", code);
        Assert.Contains("column.DisplayIndex = EstimateGrid.Columns.Count - 1;", code);
        Assert.Contains("IsReadOnly = rest || data.Stages[i].Locked", code);
        Assert.Contains("new DataGridTemplateColumn", code);
        Assert.Contains("CellEditingTemplate = new DataTemplate", code);
        Assert.DoesNotContain("new DataGridTextColumn", code);
        var cells = Source("GmcSmeta.App/EstimateView.Cells.cs");
        Assert.Contains("SaveInlineStageCell(row, e.Column, stageEditor.Text)", cells);
        Assert.Contains("RevealInlineStageColumns(data)", code);
        Assert.Contains("scroll.ScrollToHorizontalOffset", code);
        Assert.Contains("EstimateGrid.Columns.Remove(column)", code);
        Assert.DoesNotContain("if (!loaded)", code);
        var xaml = Source("GmcSmeta.App/EstimateView.xaml");
        Assert.DoesNotContain("Loaded=\"EstimateView_Loaded\"", xaml);
        var estimate = Source("GmcSmeta.App/EstimateView.xaml.cs");
        Assert.Contains("ApplyRequestedInlineStages();", estimate);
    }

    [Fact]
    public void StageColumnsParticipateInVisibilityMenuWithoutChangingBasePreferences()
    {
        var code = Source("GmcSmeta.App/EstimateView.InlineStages.cs");
        Assert.Contains("_inlineStageIds.Add(column, id)", code);
        Assert.Contains("_hiddenInlineStageIds.Contains(id)", code);
        Assert.Contains("_hiddenInlineStageIds.Clear()", code);
        Assert.Contains("FirstOrDefault(c => c.Visibility == Visibility.Visible)", code);
        var menu = Source("GmcSmeta.App/EstimateView.xaml.cs");
        Assert.Contains("_inlineStageIds.ContainsKey(c)", menu);
        Assert.Contains("SetInlineStageColumnVisibility(column, checkItem.IsChecked)", menu);
        Assert.Contains("SetInlineStageColumnVisibility(column, true)", menu);
        Assert.Contains("EstimateGrid.Columns.Where(c => !_inlineStageColumns.Contains(c))", menu);
    }

    [Fact]
    public void InlineStagesUseCompactCardsAndSharedZoomedHeaders()
    {
        var visual = Source("GmcSmeta.App/EstimateView.InlineStages.Visual.cs");
        Assert.Contains("BasedOn = EstimateGrid.ColumnHeaderStyle", visual);
        Assert.Contains("TextWrapping.Wrap", visual);
        Assert.Contains("TextAlignment.Center", visual);
        Assert.Contains("Width = 218", visual);
        Assert.Contains("PythonToolbarButton", visual);
        var zoom = Source("GmcSmeta.App/EstimateView.TemporaryView.cs");
        Assert.Contains("column.HeaderStyle = CreateInlineStageHeaderStyle(stageIndex", zoom);
        var batch = Source("GmcSmeta.App/EstimateView.InlineStages.Batch.cs");
        Assert.Contains("if (ids.Count == 0)", batch);
        Assert.Contains("if (stage.Locked) continue;", batch);
        var xaml = Source("GmcSmeta.App/EstimateView.xaml");
        Assert.Contains("MaxHeight=\"144\"", xaml);
        Assert.Contains("x:Name=\"InlineStagesSelectionButtons\"", xaml);
    }

    [Fact]
    public void NavigationSelectionDoesNotChangeFontWeightOrSize()
    {
        var xaml = Source("GmcSmeta.App/MainWindow.xaml");
        var start = xaml.IndexOf("<Style TargetType=\"TreeViewItem\">", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var selected = xaml.IndexOf("<Trigger Property=\"IsSelected\" Value=\"True\">", start, StringComparison.Ordinal);
        Assert.True(selected >= 0);
        var end = xaml.IndexOf("</Trigger>", selected, StringComparison.Ordinal);
        Assert.True(end > selected);
        var trigger = xaml[selected..end];
        Assert.Contains("HeaderBorder", trigger);
        Assert.Contains("Foreground", trigger);
        Assert.DoesNotContain("FontWeight", trigger);
        Assert.DoesNotContain("FontSize", trigger);
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
        Assert.Contains("record[exactColumns[i]] = quantity;", code);
        Assert.Contains("record[\"__RestExact\"] = row.Rest;", code);
    }

    [Fact]
    public void DistributionReadBatchesAllocationsButMutationPathKeepsTransactionQueries()
    {
        var sql = Source("GmcSmeta.Core/Db/SqlServerStages.cs");
        Assert.Contains("ReadStageTableRows(conn, fullId, stages, tx, batchRead: false)", sql);
        Assert.Contains("ReadStageTableRows(conn, fullId, stages, null, batchRead: true, token: token)", sql);
        Assert.Contains("WHERE c.stage_of=@full AND ISNULL(c.stage_rest,0)=0", sql);
        Assert.Contains("!map.TryAdd(srcId, qty)", sql);
        Assert.Contains("p.parent_id IS NULL AND p.out_of_kp=0", sql);
    }

    [Fact]
    public void InlineTotalsAreLimitedToStagesAndRemainderWithinRevisionFence()
    {
        var code = Source("GmcSmeta.App/EstimateView.InlineStages.cs");
        Assert.Contains("Totals.ReadSelected(conn, stageIds, token)", code);
        Assert.Contains("stageIds.Append(checked((int)rest.Id))", code);
        Assert.DoesNotContain("Totals.ReadAll(conn)", code);
        Assert.Contains("revisionBefore != await SqlServerDatabase.RevisionAsync(conn, token)", code);
        var sql = Source("GmcSmeta.Core/Domain/Totals.cs");
        Assert.Contains("ids.Chunk(ContractReadScope.BatchSize)", sql);
        Assert.Contains("ReadSqlBatch(conn, new ContractReadScope(batch), out _, token: token)", sql);
    }

    [Fact]
    public void StageMoneyReadsReuseRevisionFenceConnection()
    {
        var inline = Source("GmcSmeta.App/EstimateView.InlineStages.Commands.cs");
        Assert.Contains("SqlServerStages.ReadStageMoney(conn, id)", inline);
        Assert.Contains("SqlServerStages.ReadStageSystems(conn, id)", inline);
        Assert.DoesNotContain("ReadStageMoney(_sqlConnectionString", inline);
        var dialog = Source("GmcSmeta.App/StagesDialog.xaml.cs");
        Assert.Contains("SqlServerStages.ReadStageMoney(snapshotConnection, stageId)", dialog);
        Assert.Contains("SqlServerStages.ReadStageSystems(snapshotConnection, stageId)", dialog);
        var sql = Source("GmcSmeta.Core/Db/SqlServerStages.Money.cs");
        Assert.Contains("return ReadStageMoney(conn, stageId);", sql);
        Assert.Contains("return ReadStageSystems(conn, stageId);", sql);
        Assert.Contains("conn.BeginTransaction(IsolationLevel.ReadCommitted)", sql);
    }

    [Fact]
    public void StageFormulaArrayUsesSameStageOrderAsQuantities()
    {
        var sql = Source("GmcSmeta.Core/Db/SqlServerStages.cs");
        Assert.Contains("var stageFormulas = new string[stages.Count];", sql);
        Assert.Contains("stageFormulas[i] = formulas[stages[i].Id].GetValueOrDefault(srcId, \"\");", sql);
        Assert.Contains("StageFormulas = stageFormulas", sql);
    }

    [Fact]
    public void InlineStageReadCancelsSupersededWorkAndSerializesSqlPhases()
    {
        var code = Source("GmcSmeta.App/EstimateView.InlineStages.cs");
        Assert.Contains("_inlineStagesReadCancellation?.Cancel();", code);
        Assert.Contains("await _inlineStagesReadGate.WaitAsync(token)", code);
        Assert.Contains("finally { _inlineStagesReadGate.Release(); }", code);
        Assert.Contains("token.IsCancellationRequested || version != _inlineStagesVersion", code);
        Assert.Contains("ReferenceEquals(_inlineStagesReadCancellation, cancellation)", code);
        Assert.Contains("var metrics = data.Metrics;", code);
        var estimate = Source("GmcSmeta.App/EstimateView.xaml.cs");
        Assert.Contains("_reloadAfterAttach |= _inlineStagesLoading;", estimate);
        Assert.Contains("_inlineStagesReadCancellation?.Cancel();", estimate);
    }

    [Fact]
    public void SelectionReusesIndexedSourcesAndButtonsButCommandsReadLiveSelection()
    {
        var code = Source("GmcSmeta.App/EstimateView.InlineStages.Batch.cs");
        Assert.Contains("ids.UnionWith(data.SourcesBySystem[row.Source.SystemId]);", code);
        Assert.DoesNotContain("foreach (var p in data.Rows.Values)", code);
        Assert.Contains("SelectedStageSourceSet().OrderBy(id => id).ToArray()", code);
        Assert.Contains("ReferenceEquals(_selectionButtonsSnapshot, data)", code);
        Assert.Contains("child.IsEnabled = editable;", code);
        Assert.Contains("var selected = SelectedStageSourceIds();", code);
        Assert.Contains("!SelectedStageSourceIds().SequenceEqual(selected)", code);
        Assert.Contains("_selectionButtonsSnapshot = null;", code);
    }

    [Fact]
    public void DistributionCancellationReachesSqlCommandsAndDialogProjection()
    {
        var sql = Source("GmcSmeta.Core/Db/SqlServerStages.cs");
        Assert.Contains("ReadStagesOf(conn, fullId, true, null, token)", sql);
        Assert.Contains("SqlReadCancellation.Register(cmd, token)", sql);
        Assert.Contains("SqlReadCancellation.ExecuteReader(cmd, token)", sql);
        Assert.Contains("SqlReadCancellation.Register(read, token)", sql);
        Assert.Contains("SqlReadCancellation.ExecuteReader(read, token)", sql);
        Assert.Contains("await cmd.ExecuteScalarAsync(token)", sql);
        var inline = Source("GmcSmeta.App/EstimateView.InlineStages.cs");
        Assert.Contains("SqlServerDatabase.OpenAsync(_sqlConnectionString, token)", inline);
        Assert.Contains("SqlServerStages.FullOfAsync(conn, _contractId, token)", inline);
        Assert.Contains("ReadStageDistribution(conn, full, token)", inline);
        var dialog = Source("GmcSmeta.App/StagesDialog.xaml.cs");
        Assert.Contains("ReadStageDistribution(conn, _fullId, token)", dialog);
        Assert.Contains("_distributionCancellation?.Cancel();", dialog);
        Assert.Contains("record[displayColumns[i]]", dialog);
        Assert.Contains("record[exactColumns[i]]", dialog);
        Assert.Contains("if (_closed || token.IsCancellationRequested) return;", dialog);
    }
}
