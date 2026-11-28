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
        var main = Source("GmcSmeta.App", "MainWindow.xaml.cs");
        Assert.Contains("!DocumentMatchesServer(doc, confirmedRevision)", main);
        Assert.Contains("captured.Connection, doc.Id, confirmedRevision", main);
        Assert.Contains("!doc.IsStage && !doc.StageRest", main);
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
        Assert.Contains("_allowZero, revision)", code);
        var sql = Source("GmcSmeta.Core", "Db/SqlServerRateTransfer.cs");
        Assert.Contains("SqlServerWorkspace.RequirePilotDatabase(connectionString)", sql);
        Assert.Contains("ISNULL(o.archived,0)=0", sql);
        Assert.Contains("WHERE contract_id=@target", sql);
    }
}
