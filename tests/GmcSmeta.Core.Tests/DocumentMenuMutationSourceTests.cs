namespace GmcSmeta.Core.Tests;

public sealed class DocumentMenuMutationSourceTests
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
    public void DocumentMutationUsesConfirmedMetadataAndPendingEditGuard()
    {
        var helper = Source("GmcSmeta.App", "MainWindow.DocumentMutation.cs");
        Assert.Contains("Workspace.ReadDocumentMetadata(_serverConn, expected.Id)", helper);
        Assert.Contains("cached.CanTransferDocument", helper);
        // 32324fdf: поток удаления вынесен в MainWindow.Deletion.cs, ревизия фиксируется до подтверждения.
        var deletion = Source("GmcSmeta.App", "MainWindow.Deletion.cs");
        Assert.Contains("!DocumentMatchesServer(doc, captured.Revision)", deletion);
        Assert.Contains("captured.Connection, doc.Id, captured.Revision", deletion);
        Assert.Contains("doc.IsStage || doc.StageRest", deletion);
        var sql = Source("GmcSmeta.App", "MainWindow.SqlServer.cs");
        Assert.Contains("!ObjectMutationCurrent(obj, captured)", sql);
        Assert.Contains("!DocumentMatchesServer(doc, revision)", sql);
        Assert.Contains("captured.Connection, sourceId, data, revision", sql);
    }

    [Fact]
    public void RatePreviewDoesNotReadNewRevisionAtApply()
    {
        var code = Source("GmcSmeta.App", "RateTransferDialog.xaml.cs");
        Assert.DoesNotContain("ServerRevision()", code);
        Assert.DoesNotContain("_revision = null", code);
        Assert.Contains("revision != SqlServerDatabase.Revision(conn)", code);
        Assert.Contains("_revision = revision", code);
        Assert.Contains("allowZero, revision)", code);
        Assert.Contains("ResultData = await _execute", code);
        Assert.Contains("Closing += (_, args) => { if (_applying) args.Cancel = true; };", code);
        var sql = Source("GmcSmeta.Core", "Db/SqlServerRateTransfer.cs");
        Assert.Contains("SqlServerWorkspace.RequirePilotDatabase(connectionString)", sql);
        Assert.Contains("ISNULL(o.archived,0)=0", sql);
        Assert.Contains("WHERE contract_id=@target", sql);
        Assert.Contains("var before = SqlServerUndo.Snapshot(conn, tx, data.Target.Id)", sql);
        Assert.Contains("var after = SqlServerUndo.Snapshot(conn, tx, data.Target.Id)", sql);
        Assert.Contains("return new Receipt(count, rows.Count, workTotal, updated, before, after)", sql);
        var main = Source("GmcSmeta.App", "MainWindow.SqlServer.cs");
        Assert.Contains("ContractSqlWriteLock.For(captured.Connection, doc.Id)", main);
        Assert.Contains("Undo?.RecordChange(doc.Id, receipt.Before, receipt.After)", main);
    }
}
