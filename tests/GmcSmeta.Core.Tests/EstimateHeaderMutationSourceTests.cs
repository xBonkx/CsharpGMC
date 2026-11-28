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
}
