using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateTableFrameSourceTests
{
    [Fact]
    public void EstimateTableUsesSharedRoundedClipWithoutExtraScrollHost()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace local = "clr-namespace:GmcSmeta.App";
        var view = XDocument.Load(Path.Combine(app, "EstimateView.xaml"));
        var frame = Assert.Single(view.Descendants().Where(e => (string?)e.Attribute(xaml + "Name") == "EstimateTablePanel"));
        Assert.Equal(local + "SummaryTableFrame", frame.Name);
        Assert.Equal("12", (string?)frame.Attribute("CornerRadius"));
        Assert.Equal("1", (string?)frame.Attribute("BorderThickness"));

        var grid = Assert.Single(frame.Descendants(presentation + "DataGrid")
            .Where(e => (string?)e.Attribute(xaml + "Name") == "EstimateGrid"));
        Assert.Equal(presentation + "DockPanel", grid.Parent!.Name);
        Assert.Same(frame, grid.Parent.Parent);
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.Equal("True", (string?)grid.Attribute("EnableColumnVirtualization"));
        Assert.Equal("0", (string?)grid.Attribute("BorderThickness"));
    }
}
