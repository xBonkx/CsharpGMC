namespace GmcSmeta.Core.Tests;

public sealed class PrimaryWorkspacePolishSourceTests
{
    [Theory]
    [InlineData("AgreementCardView.xaml", "Content=\"К карточке договора\"")]
    [InlineData("AllContractActsWindow.xaml", "Padding=\"32,5,32,5\"")]
    [InlineData("ActsEditorView.xaml", "x:Name=\"ActCaption\" Margin=\"10,0,0,0\" VerticalAlignment=\"Center\" MaxWidth=\"420\" TextWrapping=\"Wrap\"")]
    [InlineData("ContractActsWorkspace.xaml", "Text=\"Всего с учётом вне сметы\" TextWrapping=\"Wrap\"")]
    [InlineData("ContractDialog.xaml", "ContentTemplate=\"{StaticResource IconLabelExcel}\" Click=\"ImportExcel_Click\"")]
    [InlineData("CostSummaryPane.xaml", "Content=\"Экспорт СМР Excel\" ContentTemplate=\"{StaticResource IconLabelDownload}\"")]
    [InlineData("EstimateMaterialsPanel.xaml", "Data=\"{StaticResource GlyphAlert}\" Stroke=\"#2563EB\"")]
    [InlineData("PeopleWindow.xaml", "Content=\"Удалить…\" ContentTemplate=\"{StaticResource IconLabelTrash}\" Click=\"PersonDelete_Click\"")]
    [InlineData("ShipmentsPane.xaml", "MaxWidth=\"320\" TextWrapping=\"Wrap\" VerticalAlignment=\"Center\"")]
    [InlineData("StagesDialog.xaml", "MinRowHeight=\"{StaticResource LayoutDocumentRowMinHeight}\" CanUserResizeRows=\"False\"")]
    public void VisualChangesArePresentAndXamlIsWellFormed(string file, string expected)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root!.FullName, "src", "GmcSmeta.App", file);
        _ = System.Xml.Linq.XDocument.Load(path);
        Assert.Contains(expected, File.ReadAllText(path));
    }
}
