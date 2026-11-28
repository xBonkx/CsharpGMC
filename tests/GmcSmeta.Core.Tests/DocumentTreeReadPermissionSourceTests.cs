namespace GmcSmeta.Core.Tests;

public sealed class DocumentTreeReadPermissionSourceTests
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
    public void ReadCommandsDoNotRequirePilotButWriteCommandsDo()
    {
        var code = Source("MainWindow.xaml.cs");
        var start = code.IndexOf("private void NavigationTree_ContextMenuOpening", StringComparison.Ordinal);
        var end = code.IndexOf("private TreeViewItem? NavigationNodeUnderMouse", start, StringComparison.Ordinal);
        var menu = code[start..end];
        Assert.Contains("bool CanWriteMenu() => MenuCurrent() && SqlWritesEnabled", menu);
        Assert.Contains("if (CanWriteMenu()) RunDocumentCommand(action)", menu);
        Assert.Contains("RunDocumentCommand(() => ExportEstimate(doc))", menu);
        Assert.Contains("RunDocumentCommand(() => OpenHistory(doc.Id))", menu);
        Assert.Contains("RunDocumentWrite(() => OpenActs(doc))", menu);
        Assert.Contains("activeWrite && (doc.Kind is \"main\" or \"dop\")", menu);
    }

    [Fact]
    public void StageRouteValidatesFullParentAndDoesNotFallbackToUnrelatedDocument()
    {
        var code = Source("MainWindow.SqlServer.cs");
        Assert.Contains("fullId != (doc.StageOf ?? doc.Id)", code);
        Assert.Contains("!DocumentMatchesServer(full, captured.Revision)", code);
        Assert.Contains("!CanStartDocumentMutation(full)", code);
        Assert.Equal(3, code.Split("if (!CanOpenContractWorkspace(doc)) return;").Length - 1);
    }

    [Fact]
    public void ExportsRejectChangedMetadataAndPendingWrites()
    {
        var export = Source("MainWindow.ContractExport.cs");
        Assert.Contains("!CanStartDocumentMutation(doc) || !DocumentLocallyCurrent(doc)", export);
        Assert.Contains("Equals(DocumentMetadata(expected), DocumentMetadata(snapshot.Header))", export);
        var simple = Source("MainWindow.SqlServer.cs");
        Assert.Contains("!DocumentMatchesServer(doc, revision)", simple);
        Assert.Contains("Данные изменились во время чтения сметы", simple);
    }
}
