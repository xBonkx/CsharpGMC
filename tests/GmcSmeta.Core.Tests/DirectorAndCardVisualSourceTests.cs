using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class DirectorAndCardVisualSourceTests
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

    private static XElement Style(XDocument doc, string key) =>
        Assert.Single(doc.Descendants(W + "Style").Where(s => (string?)s.Attribute(X + "Key") == key));

    [Fact]
    public void DirectorObjectHeaderAlignsTextLeftButNumericHeadersStayCentered()
    {
        var doc = XDocument.Parse(Source("DirectorView.xaml"));
        Assert.Equal("Left", (string?)Style(doc, "DirectorObjectHeader").Descendants(W + "TextBlock").Single().Attribute("TextAlignment"));
        Assert.Equal("Center", (string?)Style(doc, "DirectorHeader").Descendants(W + "TextBlock").Single().Attribute("TextAlignment"));
    }

    [Fact]
    public void DirectorWarningUsesPythonLiteralAndRowDoesNotUseWarningFill()
    {
        var doc = XDocument.Parse(Source("DirectorView.xaml"));
        var warning = Assert.Single(doc.Descendants(W + "TextBlock").Where(t => (string?)t.Attribute("Text") == "⚠"));
        Assert.Equal("{StaticResource DirectorObjectText}", (string?)warning.Attribute("Style"));
        Assert.Contains("Incomplete", (string?)warning.Attribute("Visibility"));
        Assert.DoesNotContain("Incomplete", Style(doc, "DirectorRow").ToString());
    }

    [Fact]
    public void DirectorTilesAdaptToWidthAndKeepEqualGaps()
    {
        var doc = XDocument.Parse(Source("DirectorView.xaml"));
        var tiles = Assert.Single(doc.Descendants(W + "UniformGrid").Where(e => (string?)e.Attribute(X + "Name") == "DirectorTiles"));
        Assert.Equal("DirectorTiles_SizeChanged", (string?)tiles.Attribute("SizeChanged"));
        Assert.Equal(4, tiles.Elements(W + "Border").Count());
        Assert.All(tiles.Elements(W + "Border"), b =>
            Assert.Equal("{StaticResource LayoutDirectorTileInset}", (string?)b.Attribute("Margin")));
        var cs = Source("DirectorView.xaml.cs");
        Assert.Contains("width < 920 ? 2 : count", cs);
        Assert.Contains("UpdateDirectorTileColumns();", cs);
    }

    [Fact]
    public void DocumentCardsKeepMetadataTooltipAndPythonLegendSpacing()
    {
        var doc = XDocument.Parse(Source("MainWindow.xaml"));
        var card = Assert.Single(doc.Descendants(W + "DataTemplate").Where(e => (string?)e.Attribute(X + "Key") == "DocumentCardTemplate"));
        var meta = Assert.Single(card.Descendants(W + "TextBlock").Where(e => (string?)e.Attribute("Text") == "{Binding Meta}"));
        Assert.Equal("CharacterEllipsis", (string?)meta.Attribute("TextTrimming"));
        Assert.Equal("{Binding Meta}", (string?)meta.Attribute("ToolTip"));
        Assert.Equal(2, card.Descendants(W + "StackPanel").Count(e => (string?)e.Attribute("Margin") == "{StaticResource LayoutCardLegendGap}"));
    }
}
