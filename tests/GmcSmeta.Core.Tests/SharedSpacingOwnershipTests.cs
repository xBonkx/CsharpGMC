using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class SharedSpacingOwnershipTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file))) root = root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    private static XElement Named(XDocument doc, string name) =>
        Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));

    [Fact]
    public void ShellLeavesPageEdgesToPage()
    {
        var shell = Load("MainWindow.xaml");
        var host = Named(shell, "PageHost");
        Assert.Equal("0", (string?)host.Attribute("Padding"));
        Assert.Null(host.Attribute("Margin"));
        Assert.Equal("Stretch", (string?)host.Attribute("HorizontalContentAlignment"));
        Assert.Equal("{StaticResource LayoutPageMargin}", (string?)Named(shell, "ObjectPage").Attribute("Margin"));
    }

    [Theory]
    [InlineData("ContractCostsPanel")]
    [InlineData("ContractProcurementPanel")]
    [InlineData("ContractShipmentsPanel")]
    [InlineData("ContractActsPanel")]
    [InlineData("TotalsPanel")]
    public void EmbeddedHostsDoNotRepeatPageInsets(string name)
    {
        var host = Named(Load("EstimateView.xaml"), name);
        Assert.Null(host.Attribute("Margin"));
        Assert.True(host.Attribute("Padding") is null || (string?)host.Attribute("Padding") == "0");
    }

    [Fact]
    public void CostTabsSharePageEdgesAndHeaderTemplate()
    {
        var doc = Load("CostSummaryPane.xaml");
        var tabs = Named(doc, "CostTabs");
        Assert.Equal("{StaticResource PythonTabbedPane}", (string?)tabs.Attribute("Style"));
        Assert.Equal("0", (string?)tabs.Attribute("Padding"));
        Assert.Equal(4, tabs.Elements(W + "TabItem").Count());
        foreach (var item in tabs.Elements(W + "TabItem"))
            Assert.Equal("{StaticResource LayoutCostTabBodyMargin}", (string?)Assert.Single(item.Elements(W + "Grid")).Attribute("Margin"));
        var metric = Assert.Single(Load("Theme/LayoutMetrics.xaml").Descendants().Where(e =>
            (string?)e.Attribute(X + "Key") == "LayoutCostTabBodyMargin"));
        Assert.Equal("0,8,0,6", metric.Value);
        Assert.DoesNotContain(doc.Descendants(W + "ComboBox"), e => (string?)e.Attribute("Margin") == "6,0,6,0");
    }

    [Fact]
    public void RequisitesOwnOneScrollInsetAndSharedFormSpacing()
    {
        var doc = Load("KsRequisitesWindow.xaml");
        var fields = Named(doc, "RequisitesFields");
        var scroll = Assert.Single(fields.Ancestors(W + "ScrollViewer"));
        Assert.Equal("0", (string?)scroll.Attribute("Padding"));
        Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal("{StaticResource LayoutFormScrollInset}", (string?)fields.Attribute("Margin"));
        foreach (var field in new[] { "CustomerFullBox", "SiteFullBox", "SitePartBox", "SecondRoleBox", "WorkTitleBox", "WorksStartBox" })
            Assert.Equal("{StaticResource LayoutFormStackedFieldGap}", (string?)Named(doc, field).Attribute("Margin"));
        Assert.All(fields.Descendants(W + "Separator"), e =>
            Assert.Equal("{StaticResource LayoutFormGroupSeparatorGap}", (string?)e.Attribute("Margin")));
        Assert.Empty(Named(doc, "SaveButton").Ancestors(W + "ScrollViewer"));
    }
}
