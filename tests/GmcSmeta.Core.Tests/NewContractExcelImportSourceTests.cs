namespace GmcSmeta.Core.Tests;

public sealed class NewContractExcelImportSourceTests
{
    [Fact]
    public void NewKpButtonUsesExistingNewDocumentImporterWithoutCreatingEmptyKp()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ContractDialog.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        string Read(string name) => File.ReadAllText(Path.Combine(app, name));
        var xaml = Read("ContractDialog.xaml");
        var dialog = Read("ContractDialog.xaml.cs");
        var flow = Read("MainWindow.SqlServer.cs");
        var import = Read("MainWindow.ExcelImport.cs");
        Assert.Contains("x:Name=\"ImportExcelButton\"", xaml);
        Assert.Contains("Click=\"ImportExcel_Click\"", xaml);
        Assert.Contains("MinWidth=\"110\" Visibility=\"Collapsed\"", xaml);
        Assert.Contains("allowExcelImport ? Visibility.Visible : Visibility.Collapsed", dialog);
        Assert.Contains("if (!_isNew || ImportExcelButton.Visibility != Visibility.Visible) return;", dialog);
        Assert.Contains("ImportExcelRequested = true;", dialog);
        Assert.Contains("allowExcelImport: kind == \"kp\" && baseId is null && copyFrom is null", flow);
        var begin = flow.IndexOf("if (dialog.ImportExcelRequested)", StringComparison.Ordinal);
        var create = flow.IndexOf("SqlServerWorkspace.CreateEmptyContract", begin, StringComparison.Ordinal);
        Assert.True(begin >= 0 && create > begin);
        var branch = flow[begin..create];
        Assert.Contains("await ImportExcelForObjectAsync(objectId, newDocumentsOnly: true);", branch);
        Assert.Contains("return;", branch);
        Assert.Contains("bool newDocumentsOnly = false", import);
        Assert.Contains("if (!newDocumentsOnly)", import);
        Assert.Contains("preferredObjectId, existing", import);
        Assert.Contains("SqlServerExcelImport.Import(", import);
    }
}
