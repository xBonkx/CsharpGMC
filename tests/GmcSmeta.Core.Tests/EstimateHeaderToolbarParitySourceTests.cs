using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateHeaderToolbarParitySourceTests
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
    public void ActualHeaderKeepsSingleMetadataRowAndPythonGeometry()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Parse(Source("EstimateView.xaml"));
        XElement Named(string name) => Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(x + "Name") == name));
        var row = Named("DocumentCrumbRow");
        Assert.Equal(new[] { "SubtitleText", "DocumentKindChip", "CompanyChip", "LockChip", "SourceCheckChip" },
            row.Elements().Select(e => (string?)e.Attribute(x + "Name")).ToArray());
        var header = Named("DocumentHeaderLayout");
        Assert.Equal("420", (string?)header.Attribute("MinimumLeftWidth"));
        Assert.Equal("{StaticResource LayoutPageHeaderPadding}", (string?)header.Parent!.Attribute("Padding"));
        Assert.Equal("{StaticResource LayoutHeaderTitleGap}", (string?)Named("TitleText").Parent!.Attribute("Margin"));
        Assert.Equal("{StaticResource UiValueSize}", (string?)Named("TitleText").Attribute("FontSize"));
        var metrics = XDocument.Parse(Source("Theme/LayoutMetrics.xaml"));
        XElement Key(string name) => Assert.Single(metrics.Descendants().Where(e => (string?)e.Attribute(x + "Key") == name));
        Assert.Equal("24,18,24,0", Key("LayoutPageHeaderPadding").Value);
        Assert.Equal("0,2,0,0", Key("LayoutHeaderTitleGap").Value);
    }

    [Fact]
    public void CatalogAndPurchaseUseTheSameReadbackPolicyAsToolbar()
    {
        var code = Source("EstimateView.xaml.cs");
        Assert.Contains("EstimateVisualPolicy.ShowAddActions(snapshot.Locked, _doc.StageRest, editable)", code);
        Assert.Contains("main.CanEditEstimateHeader(_contractId, _objectId)", code);
        Assert.Contains("EstimateVisualPolicy.ShowWorkColumns(est.SmrMode)", code);
        Assert.Contains("EstimateVisualPolicy.ShowPurchase(_uiSettings.PurchaseVisible", code);
        Assert.Contains("_applyingPurchaseVisibility || !_uiSettingsReady", code);
        Assert.Contains("public bool? PurchaseVisible", Source("EstimateUiSettings.cs"));
        Assert.Contains("CanAppendImport(revision)", Source("EstimateView.Sections.cs"));
    }

    [Fact]
    public void EditableTableUsesTheGridFontAndSharedNumericDisplay()
    {
        var app = Source("App.xaml");
        Assert.Contains("FontFamily, RelativeSource={RelativeSource AncestorType=DataGrid}", app);
        Assert.Contains("FontSize, RelativeSource={RelativeSource AncestorType=DataGrid}", app);
        Assert.Contains("EstimateVisualPolicy.Money(DisplayWorkAmount)", Source("EditableEstimateRow.cs"));
        Assert.Contains("{Binding DisplayWorkAmountText}", Source("EstimateView.xaml"));
    }
}
