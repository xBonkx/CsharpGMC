namespace GmcSmeta.Core.Tests;

public sealed class AgreementCreationSourceTests
{
    [Fact]
    public void DefaultsAreReadFromFreshMetadataAndOnlyAppliedToNewAnnex()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ContractDialog.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var flow = File.ReadAllText(Path.Combine(app, "MainWindow.SqlServer.cs"));
        var dialog = File.ReadAllText(Path.Combine(app, "ContractDialog.xaml.cs"));
        Assert.Contains("Workspace.ReadObjectDocumentMetadata(_serverConn, objectId)", flow);
        Assert.Contains("baseId is null && copyFrom is null ? AgreementPresentation.Resolve(metadata) : null", flow);
        Assert.Contains("revision != SqlServerDatabase.Revision(_serverConn)", flow);
        Assert.Contains("if (!_isNew || SelectedKind() != \"main\" || _agreement is not { } agreement) return;", dialog);
        Assert.Contains("NumberBox.Text = agreement.Number;", dialog);
        Assert.Contains("DateBox.Text = agreement.Date;", dialog);
        Assert.Contains("CompanyCombo.SelectedItem = company;", dialog);
    }
}
