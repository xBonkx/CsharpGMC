namespace GmcSmeta.Core.Tests;

public sealed class DocumentFilePermissionSourceTests
{
    private static string Read(string project, string path)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", project, path)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", project, path));
    }

    [Fact]
    public void ArchivedAttachmentsRemainReadableButRejectWritesAndPreCopy()
    {
        var sql = Read("GmcSmeta.Core", "Db/SqlServerAttachments.cs");
        Assert.Contains("Archived = context.Archived", sql);
        Assert.Contains("if (context.Archived)", sql);
        Assert.Contains("var revision = SqlServerDatabase.LockRevision(conn, tx)", sql);
        Assert.Equal(3, sql.Split("Check(conn, tx, contractId, expectedRevision)").Length - 1);
        var app = Read("GmcSmeta.App", "AttachmentWindow.cs");
        Assert.Contains("!_archived && SqlSchemaCompatibility", app);
        Assert.Contains("_archived = snapshot.Archived", app);
        Assert.Contains("confirmed.Archived || confirmed.Revision != revision", app);
        Assert.True(app.IndexOf("confirmed.Archived", StringComparison.Ordinal) <
            app.IndexOf("Directory.CreateDirectory(folder)", StringComparison.Ordinal));
    }

    [Fact]
    public void DocumentLinkChecksOriginalTargetBeforeAndAfterSaveDialog()
    {
        var code = Read("GmcSmeta.App", "MainWindow.DocumentLinks.cs");
        Assert.Contains("ReferenceEquals(conn, _serverConn) && mode == CurrentMode", code);
        Assert.Contains("DocumentMatchesServer(doc, revision)", code);
        Assert.Equal(2, code.Split("if (!CurrentTarget())").Length - 1);
        Assert.Contains("GmcsDocumentLink.Serialize(doc, obj.Name", code);
        Assert.DoesNotContain("_sqlConnectionString", code);
    }
}
