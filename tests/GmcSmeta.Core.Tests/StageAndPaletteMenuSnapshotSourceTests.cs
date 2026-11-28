namespace GmcSmeta.Core.Tests;

public sealed class StageAndPaletteMenuSnapshotSourceTests
{
    private static string Read(string project, string path)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", project)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", project, path));
    }

    [Fact]
    public void StageRowsMenuGuardsAllActionsAgainstChangedSelection()
    {
        var code = Read("GmcSmeta.App", "StagesDialog.xaml.cs");
        Assert.Contains("ReferenceEquals(_table, table)", code);
        Assert.Contains("ReferenceEquals(captured.Row, row.Row)", code);
        Assert.Contains("if (CanExecute()) click(sender, args);", code);
        Assert.Contains("!stage.Locked && stage.StageSum is null)) return;", code);
        Assert.Contains("!ReferenceEquals(rest, _restDocument)", code);
    }

    [Fact]
    public void InlineStageNavigationAwaitsRevisionFencedMetadataAndChecksFullIdentity()
    {
        var menu = Read("GmcSmeta.App", "EstimateView.InlineStages.cs");
        Assert.Contains("snapshot.Revision, CanOpen", menu);
        Assert.Contains("ReferenceEquals(snapshot.Rest, stage)", menu);
        Assert.Contains("!_serverConflict", menu);
        var code = Read("GmcSmeta.App", "MainWindow.SqlServer.cs");
        var start = code.IndexOf("internal async void NavigateToStageEstimate", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = code.IndexOf("private void TransferRates", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var method = code[start..end];
        Assert.Contains("var loaded = await Task.Run", method);
        Assert.Contains("SqlServerDatabase.Revision(conn) != expectedRevision", method);
        Assert.Contains("if (!Current()) return;", method);
        Assert.Contains("d.StageOf == fullId", method);
        Assert.Contains("d.StageRest == isRest", method);
        Assert.Contains("d.ObjectId == objectId", method);
        Assert.Contains("ReferenceEquals(PageHost.Content, source)", method);
        Assert.DoesNotContain("ReloadAndSelect(objectId);", method);
        Assert.True(method.IndexOf("ApplyNavigationData", StringComparison.Ordinal) <
            method.IndexOf("OpenEstimate(stageId);", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyStageDialogChecksServerRevisionBeforePublishingOpenRequest()
    {
        var code = Read("GmcSmeta.App", "StagesDialog.xaml.cs");
        Assert.Contains("private async void OpenStage", code);
        Assert.Contains("if (OpenStageRequested is null)", code);
        Assert.Contains("actualRevision != revision", code);
        Assert.Contains("!ReferenceEquals(stage, _restDocument)", code);
    }

    [Fact]
    public void SelectedStageWriterRejectsDuplicateAndForeignIds()
    {
        var code = Read("GmcSmeta.Core", "Db/SqlServerStages.SelectedRows.cs");
        Assert.Contains("ids.Count != sourceIds.Count", code);
        Assert.Contains("selected.Length != ids.Count", code);
        Assert.Contains("IsolationLevel.Serializable", code);
        Assert.Contains("StageWriteProblem(conn, tx, fullId, stageId)", code);
    }

    [Fact]
    public void PaletteUsesCapturedConnectionAndExactSelectionAndReportsActualCount()
    {
        var code = Read("GmcSmeta.App", "EstimateView.Materials.cs");
        Assert.Contains("connection == _sqlConnectionString && revision == _materialsRevision", code);
        Assert.Contains("SequenceEqual(selection, ReferenceEqualityComparer.Instance)", code);
        Assert.Contains("Покрашено строк: {result.Changed}", code);
        var sql = Read("GmcSmeta.Core", "Db/SqlServerCostOperations.cs");
        Assert.Contains("sources.Count != ids.Distinct().Count()", sql);
    }

    [Fact]
    public void KsCopyAndMoveCheckOriginalMenuSnapshotWithoutDisablingSignedCopy()
    {
        var code = Read("GmcSmeta.App", "ActsEditorView.xaml.cs");
        Assert.Contains("bool CanExecute() => CanRead() && !_signed && !_contractLocked;", code);
        Assert.Contains("if (!CanRead()) return;", code);
        Assert.Contains("EditExtraDialog(hit.PositionId, CanExecute)", code);
        var extra = Read("GmcSmeta.App", "ActsEditorView.ExtraMenu.cs");
        Assert.Contains("!menuCurrent() || !CanMoveExtra(row, actId, revision)", extra);
        Assert.Contains("!ActGrid.CommitEdit(DataGridEditingUnit.Row, true)", extra);
    }
}
