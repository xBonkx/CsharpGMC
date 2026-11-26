using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ResponsiveWorkspaceLayoutSourceTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    private static XElement Named(XDocument doc, string name) =>
        Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));

    [Fact]
    public void EstimateToolbarUsesTwoWrappingGroups()
    {
        var doc = Load("EstimateView.xaml");
        var toolbar = Named(doc, "EstimateToolsBar");
        Assert.Equal("ResponsiveToolbarPanel", toolbar.Name.LocalName);
        var groups = toolbar.Elements(W + "WrapPanel").ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Contains(Named(doc, "AddRowButton"), groups[0].Descendants());
        Assert.Contains(Named(doc, "EstimateSearch"), groups[1].Descendants());
        Assert.Contains(Named(doc, "PurchaseToggle"), groups[1].Descendants());
        Assert.Equal("Right", (string?)groups[1].Attribute("HorizontalAlignment"));
    }

    [Fact]
    public void DocumentActionsWrapAndCompanyNameIsBounded()
    {
        var doc = Load("EstimateView.xaml");
        var actions = Named(doc, "ExportButton").Parent!;
        Assert.Equal(W + "WrapPanel", actions.Name);
        Assert.Contains(Named(doc, "UndoButton"), actions.Elements());
        var company = Named(doc, "CompanyText");
        Assert.Equal("CharacterEllipsis", (string?)company.Attribute("TextTrimming"));
        Assert.NotNull(company.Attribute("MaxWidth"));
        Assert.NotNull(company.Attribute("ToolTip"));
    }

    [Fact]
    public void SidebarUsesSharedMetricsAndReopenButtonCanGrow()
    {
        var doc = Load("MainWindow.xaml");
        Assert.Equal("{StaticResource LayoutSidebarPadding}", (string?)Named(doc, "Sidebar").Attribute("Padding"));
        var show = Named(doc, "ShowSidebarButton");
        Assert.Equal("Collapsed", (string?)show.Attribute("Visibility"));
        Assert.Equal("{StaticResource LayoutShowSidebarMargin}", (string?)show.Attribute("Margin"));
        Assert.Null(show.Attribute("Height"));
        Assert.Equal("30", (string?)show.Attribute("MinHeight"));
    }
}
