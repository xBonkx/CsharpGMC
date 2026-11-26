using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateTypographyAndToolsSourceTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    private static XElement Key(XDocument doc, string key) =>
        Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Key") == key));

    [Fact]
    public void PythonFontAndEstimateZoomRatioAreShared()
    {
        Assert.Equal("Segoe UI", Key(XDocument.Parse(Source("App.xaml")), "UiFontFamily").Value);
        Assert.Equal("0.95", Key(XDocument.Parse(Source("Theme/TypographyMetrics.xaml")), "UiEstimateHeaderZoomRatio").Value);
        var code = Source("EstimateView.TemporaryView.cs");
        Assert.Contains("EstimateGrid.FontSize = baseSize * zoom", code);
        Assert.Contains("baseSize * headerRatio * zoom", code);
        Assert.Contains("EstimateColumnsButton.ToolTip", code);
        Assert.Contains("Ctrl+0", code);
    }

    [Theory]
    [InlineData("Layers", "M12 3 3 8l9 5 9-5z M3 13l9 5 9-5")]
    [InlineData("Sort", "M4 5h12M4 10h9M4 15h6M19 4v16M16 17l3 3 3-3")]
    [InlineData("Download", "M12 4v11M7.5 10.5 12 15l4.5-4.5 M4 15v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3")]
    public void PythonToolbarGlyphsRetainOriginalGeometry(string name, string geometry)
    {
        var doc = XDocument.Parse(Source("Theme/ToolbarIcons.xaml"));
        Assert.Equal(geometry, (string?)Key(doc, "Glyph" + name).Attribute("Figures"));
        var template = Key(doc, "IconLabel" + name);
        var view = Assert.Single(template.Descendants(W + "Viewbox"));
        Assert.Equal("18", (string?)view.Attribute("Width"));
        Assert.Equal("18", (string?)view.Attribute("Height"));
        var canvas = Assert.Single(view.Descendants(W + "Canvas"));
        Assert.Equal("24", (string?)canvas.Attribute("Width"));
        Assert.Equal("24", (string?)canvas.Attribute("Height"));
        var path = Assert.Single(canvas.Descendants(W + "Path"));
        Assert.Equal("{StaticResource Glyph" + name + "}", (string?)path.Attribute("Data"));
        Assert.Contains("Foreground", (string?)path.Attribute("Stroke"));
        Assert.Contains(Key(doc, "SvgGlyphPath").Elements(W + "Setter"), s =>
            (string?)s.Attribute("Property") == "StrokeThickness" && (string?)s.Attribute("Value") == "1.9");
    }

    [Fact]
    public void GroupingAndSortingKeepStableWeightAndExactGap()
    {
        var doc = XDocument.Parse(Source("EstimateView.xaml"));
        var group = Assert.Single(doc.Descendants(W + "Button").Where(e => (string?)e.Attribute(X + "Name") == "GroupButton"));
        Assert.Equal("0,0,6,4", (string?)group.Attribute("Margin"));
        Assert.Contains("button.FontWeight = FontWeights.Normal", Source("EstimateView.TemporaryView.cs"));
    }
}
