namespace GmcSmeta.Core.Tests;

public sealed class EstimateHeaderMutationSourceTests
{
    private static string Source(string project, string path)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", project, path)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", project, path));
    }

    [Fact]
    public void PhaseMenuUsesOriginalSnapshotAndRevisionAndSharedWriter()
    {
        var code = Source("GmcSmeta.App", "EstimateView.Header.cs");
        Assert.Contains("!_doc.IsStage && !_doc.StageRest", code);
        Assert.Contains("SaveKpPhaseAsync(snapshot, revision, phaseKey)", code);
        Assert.Contains("SaveKpPhaseAsync(snapshot, revision, \"\")", code);
        Assert.Contains("await _writeLock.WaitAsync()", code);
        Assert.Contains("revision != _serverRevision", code);
        Assert.Contains("await Task.Run(writer)", code);
        Assert.Contains("_saveAwaitingReadback = true", code);
        Assert.Contains("await ReloadDocumentAsync()", code);
    }

    [Fact]
    public void LockConfirmationDefaultsNoAndCannotUnlockComputedRest()
    {
        var code = Source("GmcSmeta.App", "EstimateView.Header.cs");
        Assert.Contains("MessageBoxImage.Question, MessageBoxResult.No", code);
        Assert.Contains("SaveHeaderMutationAsync(snapshot, revision, true", code);
        var sql = Source("GmcSmeta.Core", "Db/SqlServerWorkspace.cs");
        Assert.Contains("Расчётный остаток нельзя разблокировать или подписать.", sql);
    }

    [Fact]
    public void ActionsMenuReflectsActualLockAndDocumentKind()
    {
        var code = Source("GmcSmeta.App", "EstimateView.DocumentActions.cs");
        Assert.Contains("snapshot.Locked ? \"Снять блокировку сметы\"", code);
        Assert.Contains("var kp = ordinary && _isKp", code);
        Assert.Contains("var annex = ordinary && _doc.Kind == \"main\"", code);
        Assert.Contains("DeleteContract_Click, \"Trash\", () => ordinary && Write(true)", code);
    }

    [Fact]
    public void DirectHeaderEditsUsePublishedRevisionAndCurrentPage()
    {
        var code = Source("GmcSmeta.App", "EstimateView.Header.cs");
        Assert.Contains("revision == _publishedEstimateRevision && CanTransferDocument", code);
        Assert.Contains("main.IsCurrentEstimatePage(_contractId, _objectId, this)", code);
        Assert.Contains("CanChangeHeader(snapshot, displayedRevision, false)", code);
        Assert.Contains("SaveTotalsFieldAsync(field, value, caption, displayedRevision)", code);
        Assert.Contains("snapshot.MoneyFromStages || !CanChangeHeader(snapshot, revision, false)", code);
        Assert.Contains("SqlServerSmrMode.SetSmrMode(_sqlConnectionString, _contractId, mode, revision)", code);
        Assert.Contains("SaveHeaderMutationAsync(snapshot, revision, false, () =>", code);
        Assert.Contains("CanChangeHeader(snapshot, _serverRevision, false)", code);
    }

    [Fact]
    public void MetadataAndEstimateAreReconciledAtTheSameRevision()
    {
        var view = Source("GmcSmeta.App", "EstimateView.xaml.cs");
        Assert.Contains("Workspace.ReadDocumentMetadata(read, _contractId)", view);
        Assert.Contains("Metadata: (DocRow?)metadata", view);
        Assert.Contains("if (!identityChanged) _doc = metadata;", view);
        Assert.Contains("if (identityChanged) IsEnabled = false;", view);
        Assert.Contains("main.PublishEstimateMetadata(this, metadata);", view);
        var main = Source("GmcSmeta.App", "MainWindow.DocumentMutation.cs");
        Assert.Contains("Materials = previous.Materials", main);
        Assert.Contains("++_navigationDataGeneration;", main);
        Assert.Contains("SynchronizeNavigation(updated, rebuildTree: true);", main);
        Assert.Contains("cached.MatchesDocumentMetadata(doc) && cached.CanReuseCacheRevision(revision)",
            Source("GmcSmeta.App", "MainWindow.xaml.cs"));
        Assert.Contains("if (!estimate.MatchesDocumentIdentity(doc))",
            Source("GmcSmeta.App", "MainWindow.Navigation.cs"));
    }

    [Fact]
    public void TransferAndExportKeepTheRevisionThatOpenedTheCommand()
    {
        var header = Source("GmcSmeta.App", "EstimateView.Header.cs");
        Assert.Contains("revision, () => CanChangeHeader(snapshot, revision, false)", header);
        Assert.Contains("if (!dialog.WriteAttempted) return;", header);
        Assert.Contains("pdf: pdf, expectedRevision: revision", header);
        var dialog = Source("GmcSmeta.App", "RateTransferDialog.xaml.cs");
        Assert.Contains("revision != _expectedRevision || !_canApply()", dialog);
        Assert.Contains("WriteAttempted = true;", dialog);
        Assert.Contains("DialogResult = false;", dialog);
        var export = Source("GmcSmeta.App", "MainWindow.ContractExport.cs");
        Assert.Contains("startRevision != expectedRevision", export);
        Assert.Contains("LoadKpPrintData(connectionString, doc, expectedRevision)", export);
    }
}
