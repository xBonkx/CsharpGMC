using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class SharedGlyphCatalogSourceTests
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

    [Fact]
    public void SharedCatalogUsesSingleGeometrySourceAndCorrectRoles()
    {
        var doc = Load("Theme/ToolbarIcons.xaml");
        var paths = doc.Descendants(W + "PathGeometry").ToDictionary(e => (string)e.Attribute(X + "Key")!);
        foreach (var geometry in paths.Values)
            Assert.StartsWith("M", (string)geometry.Attribute("Figures")!);
        var templates = doc.Descendants(W + "DataTemplate").ToArray();
        Assert.True(templates.Length >= 37);
        foreach (var template in templates)
        {
            var key = (string)template.Attribute(X + "Key")!;
            var view = Assert.Single(template.Descendants(W + "Viewbox"));
            var size = key switch { "IconLabelMenu" => "20", "IconOnlyX" => "14", "IconOnlyChevronDown" => "12", _ => "18" };
            Assert.Equal(size, (string?)view.Attribute("Width"));
            Assert.Equal(size, (string?)view.Attribute("Height"));
            Assert.Equal("Uniform", (string?)view.Attribute("Stretch"));
            Assert.Equal("False", (string?)view.Attribute("IsHitTestVisible"));
            var canvas = Assert.Single(view.Descendants(W + "Canvas"));
            Assert.Equal("24", (string?)canvas.Attribute("Width"));
            Assert.Equal("24", (string?)canvas.Attribute("Height"));
            if (key.StartsWith("IconLabel", StringComparison.Ordinal))
                Assert.Equal("0,0,6,0", (string?)view.Attribute("Margin"));
            foreach (var path in canvas.Elements(W + "Path"))
            {
                Assert.Equal("{StaticResource SvgGlyphPath}", (string?)path.Attribute("Style"));
                var data = (string)path.Attribute("Data")!;
                Assert.StartsWith("{StaticResource Glyph", data);
                Assert.True(paths.ContainsKey(data["{StaticResource ".Length..^1]));
            }
            foreach (var shape in canvas.Elements())
                Assert.Contains("Foreground", (string?)shape.Attribute("Stroke"));
        }
    }

    [Fact]
    public void EstimateHeaderActionsShareHeightAndSpacing()
    {
        var doc = Load("EstimateView.xaml");
        var buttons = doc.Descendants(W + "Button").Where(e => new[] { "Undo_Click", "Redo_Click",
            "Help_Click", "MoreActions_Click", "ExportExcelPdf_Click" }.Contains((string?)e.Attribute("Click"))).ToArray();
        Assert.Equal(5, buttons.Length);
        foreach (var button in buttons)
        {
            Assert.Equal("{StaticResource LayoutHeaderActionMinHeight}", (string?)button.Attribute("MinHeight"));
            Assert.Equal("Center", (string?)button.Attribute("VerticalAlignment"));
        }
    }

    [Fact]
    public void SharedLayoutAndDiagnosticContractsRemainAvailable()
    {
        var doc = Load("Theme/LayoutMetrics.xaml");
        foreach (var key in new[] { "LayoutPageMargin", "LayoutDialogMargin", "LayoutCardPadding",
            "LayoutToolbarPadding", "LayoutFormFieldMargin", "LayoutCellPadding", "LayoutHeaderActionGap" })
            Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Key") == key));
        var app = Load("App.xaml");
        Assert.Contains(app.Descendants(W + "ResourceDictionary"), e => (string?)e.Attribute("Source") == "Theme/LayoutMetrics.xaml");
    }
}
