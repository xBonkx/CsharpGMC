namespace GmcSmeta.Core.Tests;

public sealed class SystemCopyTransactionSourceTests
{
    private static string Source(string project, string path)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", project, path)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", project, path)).Replace("\r\n", "\n");
    }

    [Fact]
    public void CopyTransactionChecksSameObjectAndExcludesPurchaseOnlyRows()
    {
        var code = Source("GmcSmeta.Core", "Db/SqlServerSystemCopy.cs");
        Assert.Contains("SqlServerWorkspace.RequirePilotDatabase(connectionString)", code);
        Assert.Contains("source.object_id=target.object_id", code);
        Assert.Contains("target.stage_of IS NULL", code);
        Assert.Contains("!roots[parent].Outside", code);
        Assert.Contains("p.note,p.extra_json,p.source_row,p.[order],0", code);
        Assert.Contains("OUTPUT INSERTED.id\n                  SELECT", code);
        Assert.Contains("WHERE p.id=@src AND p.system_id=@source", code);
        Assert.Contains("SystemCopySource.InsertCopyOrder", code);
    }

    [Fact]
    public void CopyDialogCapturesSelectionAndRechecksDestinationBeforeWrite()
    {
        var code = Source("GmcSmeta.App", "EstimateView.SystemCopy.cs");
        Assert.Contains("SqlServerDatabase.Revision(conn) != displayedRevision", code);
        Assert.Contains("main.CanReceiveSystemCopy(target, _contractId, _objectId)", code);
        Assert.Contains("await _writeLock.WaitAsync()", code);
        Assert.Contains("displayedRevision != _serverRevision", code);
        Assert.Contains("_focusSystemAfterReload = checked((int)newId)", code);
        Assert.Contains("main.RefreshSystemCopyTarget(target.Id)", code);
    }
}
