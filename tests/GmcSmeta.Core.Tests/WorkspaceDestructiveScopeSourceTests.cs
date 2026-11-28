namespace GmcSmeta.Core.Tests;

public sealed class WorkspaceDestructiveScopeSourceTests
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
    public void MainWindowDeletionChecksSamePageAndEveryAffectedEditor()
    {
        var ui = Source("GmcSmeta.App/MainWindow.Deletion.cs");
        Assert.Contains("editor.CanTransferDocument", ui);
        Assert.Contains("navigation == _estimateOpenGeneration && data == _navigationDataGeneration", ui);
        Assert.Contains("ReferenceEquals(page, PageHost.Content)", ui);
        Assert.Contains("!Current() || !DeletionDocumentsSettled(preview.DocumentIds)", ui);
        Assert.Contains("Closing += preventClose", ui);
        Assert.Contains("Closing -= preventClose", ui);
        Assert.Contains("return await Task.Run(operation)", ui);
        Assert.Contains("SqlServerWorkspace.DeleteObject(target.Connection, obj.Id, target.Revision, preview.DocumentIds)", ui);
        Assert.Contains("SqlServerWorkspace.DeleteContract(captured.Connection, doc.Id, captured.Revision, doc.ObjectId)", ui);
        Assert.Contains("_workspaceDeletionUncertain = true", ui);
        Assert.Contains("Undo?.Forget(key)", ui);
        Assert.Contains("MessageBoxImage.Question, MessageBoxResult.No", ui);
    }

    [Fact]
    public void PreviewAndDeleteShareRestorableSnapshotAndScopeGuards()
    {
        var sql = Source("GmcSmeta.Core/Db/SqlServerWorkspace.DeletePreview.cs");
        Assert.Contains("SqlServerTrash.ValidateObjectDeletion(conn, tx, id)", sql);
        Assert.Contains("SqlServerTrash.ValidateContractDeletion(conn, tx, id, objectId)", sql);
        Assert.Contains("CheckRevision(conn, tx, revision)", sql);
        Assert.Contains("c.locked=0 AND c.stage_of IS NULL", sql);
        Assert.Contains("a.contract_id NOT IN", sql);
        Assert.Contains("stage_sys_src IN", sql);
        Assert.Contains("stage_src IN", sql);
        Assert.Contains("throw new TrashCommitUnknownException(ex)", sql);
        var trash = Source("GmcSmeta.Core/Db/SqlServerTrash.cs");
        Assert.Contains("BuildObjectDump(conn, tx, objectId)", trash);
        Assert.Contains("BuildContractDump(conn, tx, contractId, allowStage, expectedObjectId)", trash);
        Assert.Contains("TrashRestoreSafety.ValidateSnapshot", trash);
        var write = Source("GmcSmeta.Core/Db/SqlServerWorkspace.cs");
        Assert.Contains("!ReadDeletionDocumentIds(conn, tx, objectId).SequenceEqual(expectedDocuments.OrderBy(id => id))", write);
        Assert.Contains("SqlServerTrash.KeepContract(conn, tx, contractId, expectedObjectId: expectedObjectId)", write);
        Assert.Contains("RequireSingleDeletion(conn, tx, \"objects\", objectId)", write);
        Assert.Contains("RequireSingleDeletion(conn, tx, \"contracts\", contractId)", write);
    }

    [Fact]
    public void ConfirmAndTrashNeverDefaultToDestructiveAction()
    {
        var dialog = Source("GmcSmeta.App/ConfirmDeleteDialog.xaml");
        Assert.Contains("IsCancel=\"True\"", dialog);
        Assert.DoesNotContain("IsDefault=\"True\"", dialog);
        var code = Source("GmcSmeta.App/ConfirmDeleteDialog.xaml.cs");
        Assert.Contains("if (AnswerConfirmed && DeleteButton.IsEnabled) DialogResult = true;", code);
        var trash = Source("GmcSmeta.App/TrashWindow.xaml.cs");
        Assert.Contains("if (!_commitUncertain && _canWrite)", trash);
        Assert.Contains("MessageBoxImage.Warning, MessageBoxResult.No", trash);
        Assert.Contains("!ReferenceEquals(TrashGrid.SelectedItem, row)", trash);
        var restore = Source("GmcSmeta.Core/Db/SqlServerTrashRestore.cs");
        Assert.Contains("WHERE id=@id AND archived=0", restore);
    }
}
