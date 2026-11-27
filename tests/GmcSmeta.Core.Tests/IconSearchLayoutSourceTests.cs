using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class IconSearchLayoutSourceTests
{
    private static XDocument Load(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Fact]
    public void IconSearchReservesSpaceOnceAndCentersUnclippedTextHost()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var style = Load("App.xaml").Descendants(p + "Style")
            .Single(e => (string?)e.Attribute(x + "Key") == "PythonIconSearch");
        Assert.Contains(style.Elements(p + "Setter"), e =>
            (string?)e.Attribute("Property") == "Padding" && (string?)e.Attribute("Value") == "0");
        var host = style.Descendants(p + "ScrollViewer")
            .Single(e => (string?)e.Attribute(x + "Name") == "PART_ContentHost");
        Assert.Equal("30,0,32,0", (string?)host.Attribute("Margin"));
        Assert.Equal("Center", (string?)host.Attribute("VerticalAlignment"));
        Assert.Null(host.Attribute("Height"));
        Assert.Null(host.Parent!.Attribute("Padding"));
    }

    [Theory]
    [InlineData("EstimateView.xaml", "EstimateSearch")]
    [InlineData("EstimateSummaryPanel.xaml", "SummarySearch")]
    [InlineData("EstimateMaterialsPanel.xaml", "MaterialsSearch")]
    [InlineData("ProcurementPane.xaml", "ProcurementSearchBox")]
    [InlineData("ShipmentsPane.xaml", "RowSearch")]
    public void DocumentSearchUsesAdaptiveIconTemplate(string file, string name)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var input = Load(file).Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
        Assert.Equal("{StaticResource PythonIconSearch}", (string?)input.Attribute("Style"));
        Assert.Null(input.Attribute("Padding"));
        Assert.Null(input.Attribute("Height"));
        Assert.Null(input.Parent!.Attribute("Height"));
    }
}
