using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class AuxiliaryTabLayoutSourceTests
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
    public void TabPaneOverlapsHeaderByOneDipWithoutHidingIndicator()
    {
        var metrics = Load("Theme/LayoutMetrics.xaml");
        Assert.Equal("0,-1,0,0", metrics.Descendants().Single(e =>
            (string?)e.Attribute(X + "Key") == "LayoutAuxTabPaneOverlap").Value);
        var app = Load("App.xaml");
        var style = Assert.Single(app.Descendants(W + "Style").Where(s =>
            (string?)s.Attribute(X + "Key") == "PythonTabbedPane"));
        var tabs = Assert.Single(style.Descendants(W + "TabPanel"));
        Assert.Equal("1", (string?)tabs.Attribute("Panel.ZIndex"));
        Assert.Contains(style.Descendants(W + "Border"), b =>
            (string?)b.Attribute("Margin") == "{StaticResource LayoutAuxTabPaneOverlap}");
        Assert.NotNull(Named(app, "PART_SelectedContentHost"));
    }

    [Theory]
    [InlineData("PlaceGrid", "Наличие")]
    [InlineData("MaterialObjectsGrid", "По объектам, м²")]
    public void MetalDetailsUseFullWidthOfSharedTabPane(string name, string header)
    {
        var doc = Load("MetalWarehouseWindow.xaml");
        var grid = Named(doc, name);
        var tab = grid.Ancestors(W + "TabItem").First();
        Assert.Equal(header, (string?)tab.Attribute("Header"));
        Assert.Equal("{StaticResource PythonTabbedPane}", (string?)tab.Parent!.Attribute("Style"));
        Assert.Null(grid.Attribute("Grid.Column"));
        Assert.Empty(grid.Ancestors(W + "ScrollViewer"));
    }

    [Theory]
    [InlineData("LetterEditorWindow.xaml")]
    [InlineData("PtoEditorWindow.xaml")]
    public void AuxiliaryEditorsUseSharedSpacingAndKeepFooterOutsideTabs(string file)
    {
        var doc = Load(file);
        var tab = Assert.Single(doc.Descendants(W + "TabControl"));
        Assert.Equal("{StaticResource PythonTabbedPane}", (string?)tab.Attribute("Style"));
        Assert.Contains(doc.Descendants(W + "WrapPanel"), p =>
            (string?)p.Attribute("DockPanel.Dock") == "Bottom" &&
            !p.Ancestors(W + "TabControl").Any() &&
            p.Descendants(W + "Button").Any(b =>
                (string?)b.Attribute("Margin") == "{StaticResource LayoutAuxActionGap}"));
    }

    [Fact]
    public void PeopleSplitterKeepsBothResizablePanes()
    {
        var doc = Load("PeopleWindow.xaml");
        var splitter = Assert.Single(doc.Descendants(W + "GridSplitter"));
        Assert.Equal("PreviousAndNext", (string?)splitter.Attribute("ResizeBehavior"));
        Assert.Equal("Rows", (string?)splitter.Attribute("ResizeDirection"));
        Assert.Equal("#E4E8EF", (string?)splitter.Attribute("Background"));
    }
}
