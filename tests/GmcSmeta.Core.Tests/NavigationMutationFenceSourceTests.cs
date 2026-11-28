namespace GmcSmeta.Core.Tests;

public sealed class NavigationMutationFenceSourceTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Fact]
    public void ObjectDialogsKeepPreConfirmationRevisionAndSafeDefault()
    {
        var code = Source("MainWindow.xaml.cs");
        Assert.Contains("Workspace.Objects(_serverConn).FirstOrDefault(row => row.Id == obj.Id)", code);
        Assert.Contains("revision != SqlServerDatabase.Revision(_serverConn)", code);
        Assert.Contains("target.Connection, obj.Id, !obj.Archived, target.Revision", code);
        Assert.Contains("target.Connection, obj.Id, dialog.Year, target.Revision", code);
        // 32324fdf: удаление карточки переехало в MainWindow.Deletion.cs с pre-confirmation ревизией.
        var deletion = Source("MainWindow.Deletion.cs");
        Assert.Contains("SqlServerWorkspace.DeleteObject(target.Connection, obj.Id, target.Revision, preview.DocumentIds)", deletion);
        Assert.Contains("MessageBoxImage.Question, MessageBoxResult.No", deletion);
    }

    [Fact]
    public void RenameKeepsOriginalObjectAndRevisionAcrossFocusAndModalChanges()
    {
        var code = Source("MainWindow.xaml.cs");
        Assert.Contains("_pageRenameTarget = (obj, target.Connection, target.Revision, target.Mode)", code);
        Assert.Contains("_selectedObject?.Id != captured.Object.Id", code);
        Assert.Contains("SaveObjectRename(captured.Object.Id, name, captured.Revision)", code);
        Assert.Contains("SaveObjectRename(obj.Id, dialog.Value.Trim(), captured.Revision)", code);
        var sql = Source("MainWindow.SqlServer.cs");
        Assert.Contains("current with { Name = name }, expectedRevision", sql);
    }

    [Fact]
    public void TreeMenuRechecksConnectionAndParentAndDisablesLockedRateTransfer()
    {
        var code = Source("MainWindow.xaml.cs");
        Assert.Contains("menuConnection == _sqlConnectionString", code);
        Assert.Contains("_objects.Any(row => Equals(row, parent))", code);
        Assert.Contains("!doc.Locked && !doc.IsStage && !doc.StageRest", code);
        Assert.Contains("ContextMenuIcons.Create(\"Скопировать расценки…\", \"Copy\", editable)", code);
    }
}
