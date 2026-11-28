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
        Assert.Contains("if (SelectedKind() != \"main\" || _agreement is not { } agreement) return;", dialog);
        Assert.Contains("NumberBox.Text = agreement.Number;", dialog);
        Assert.Contains("DateBox.Text = agreement.Date;", dialog);
        Assert.Contains("CompanyCombo.SelectedItem = company;", dialog);
        Assert.Contains("sourceIdForCompany = baseId ?? copyFrom", flow);
        Assert.Contains("makeContract && info.Kind == \"kp\" ? AgreementPresentation.Resolve(metadata) : null", flow);
        Assert.Contains("if (_isNew) ApplyCompanyVat();", dialog);
    }

    [Fact]
    public void ExcelDsRequiresExplicitBaseAndEditorKeepsCurrentSnapshotTitle()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ExcelImportPreviewWindow.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var preview = File.ReadAllText(Path.Combine(app, "ExcelImportPreviewWindow.cs"));
        Assert.Contains("new(\"main\", \"Приложение\")", preview);
        Assert.Contains("_baseDocs.SelectedIndex = -1;", preview);
        Assert.DoesNotContain("_baseDocs.SelectedIndex = 0", preview);
        Assert.Contains("c.Kind == \"dop\" && c.BaseContractId is null", preview);
        Assert.Contains("SelectedBaseCompany()", preview);
        Assert.Contains("ApplyAgreementDefaults()", preview);
        Assert.Contains("_annexCompany.SelectedIndex = -1;", preview);
        var editor = File.ReadAllText(Path.Combine(app, "EstimateView.xaml.cs"));
        Assert.Contains("EstimateVisualPolicy.HeaderText(est.Title, est.AnnexDisplayTitle,", editor);
        Assert.Contains("TitleText.Text = header.Title;", editor);
        Assert.Contains("TitleText.ToolTip = $\"{header.FullTitle}", editor);
        var ks = File.ReadAllText(Path.Combine(app, "ContractActsWorkspace.xaml.cs"));
        Assert.Contains("SumTotalCaption.Text = \"Сдано по смете\";", ks);
        Assert.Contains("Totals.ReadOneWithRules(conn, contractId, token)", ks);
    }
}
