using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class DeferredGridScrollingSourceTests
{
    [Theory]
    [InlineData("EstimateView.xaml", "EstimateGrid", false)]
    [InlineData("EstimateMaterialsPanel.xaml", "MaterialsGrid", false)]
    [InlineData("EstimateSummaryPanel.xaml", "SummaryGrid", false)]
    [InlineData("ProcurementPane.xaml", "BuyGrid", true)]
    [InlineData("ShipmentsPane.xaml", "LinesGrid", true)]
    [InlineData("ActsEditorView.xaml", "ActGrid", false)]
    public void LargeGridUpdatesDuringThumbDragWithoutDisablingVirtualization(string file, string name, bool grouped)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        var doc = XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = Assert.Single(doc.Descendants(p + "DataGrid")
            .Where(e => (string?)e.Attribute(x + "Name") == name));
        Assert.Equal("False", (string?)grid.Attribute("ScrollViewer.IsDeferredScrollingEnabled"));
        Assert.Equal("True", (string?)grid.Attribute("ScrollViewer.CanContentScroll"));
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.Equal("True", (string?)grid.Attribute("EnableColumnVirtualization"));
        Assert.Equal("Recycling", (string?)grid.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.Equal("Pixel", (string?)grid.Attribute("VirtualizingPanel.ScrollUnit"));
        if (grouped)
            Assert.Equal("True", (string?)grid.Attribute("VirtualizingPanel.IsVirtualizingWhenGrouping"));
    }
}
