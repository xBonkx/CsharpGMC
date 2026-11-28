namespace GmcSmeta.Core.Tests;

public sealed class CardImportTargetSourceTests
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
    public void CardPdfAndFileCommandsRejectPreviousPageOrDocument()
    {
        var code = Source("MainWindow.xaml.cs");
        Assert.Contains("!_objectCardTransitionPending && ReferenceEquals(PageHost.Content, ObjectPage)", code);
        Assert.Contains("_selectedObject?.Id == doc.ObjectId", code);
        Assert.Contains("mode == CurrentMode && CardDocumentCurrent(doc)", code);
        Assert.Contains("button.IsLoaded && mode == CurrentMode && CardDocumentCurrent(target)", code);
        Assert.Contains("new MenuItem { Header = \"Добавить / выбрать подписанный PDF…\" }", code);
    }

    [Fact]
    public void BothImportPreviewsUseFreshActiveObjectMetadataAndRejectMissingPreferredTarget()
    {
        var code = Source("MainWindow.ExcelImport.cs");
        Assert.Contains("preferredObjectId is not null && preferred is null", code);
        Assert.Contains("!objects.Any(obj => Equals(obj, preferred))", code);
        Assert.Contains("documents.Where(doc => activeIds.Contains(doc.ObjectId)).ToArray()", code);
        Assert.Contains("preview.ShowDialog() != true || !ContextCurrent() || !FileCurrent()", code);
        var existing = Source("MainWindow.ExcelExistingUpdate.cs");
        Assert.Contains("imported, documents,", existing);
        Assert.Contains("!documents.Any(doc => doc.Id == plan.ContractId", existing);
        Assert.Contains("!contextCurrent()", existing);
    }

    [Fact]
    public void ObjectPropertyDialogUsesCapturedRevisionAndMode()
    {
        var code = Source("MainWindow.SqlServer.cs");
        Assert.Contains("captured?.Revision ?? SqlServerDatabase.Revision(_serverConn)", code);
        Assert.Contains("revision != SqlServerDatabase.Revision(_serverConn)", code);
        Assert.Contains("connection != _sqlConnectionString || mode != CurrentMode", code);
    }
}
