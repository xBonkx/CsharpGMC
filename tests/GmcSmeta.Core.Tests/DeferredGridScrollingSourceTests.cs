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

    [Theory]
    [InlineData("CostSummaryPane.xaml", "MaterialsGrid")]
    [InlineData("CostSummaryPane.xaml", "SmrWorkGrid")]
    [InlineData("AgreementCardView.xaml", "SourceGrid")]
    public void AdditionalBoundedGridsUseRecyclingAndContentScrolling(string file, string name)
    {
        var document = XDocument.Load(Path.Combine(Root(), "src", "GmcSmeta.App", file));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = Assert.Single(document.Descendants(p + "DataGrid")
            .Where(e => (string?)e.Attribute(x + "Name") == name));
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.Equal("True", (string?)grid.Attribute("EnableColumnVirtualization"));
        Assert.Equal("True", (string?)grid.Attribute("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", (string?)grid.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.Equal("True", (string?)grid.Attribute("ScrollViewer.CanContentScroll"));
        if (name == "SourceGrid")
            Assert.Equal("240", (string?)grid.Attribute("MaxHeight"));
    }

    [Fact]
    public void SharedDocumentTableKeepsPixelRecyclingAndLiveContentScrolling()
    {
        var root = Root();
        var document = XDocument.Load(Path.Combine(root, "src", "GmcSmeta.App", "App.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = Assert.Single(document.Descendants(p + "Style")
            .Where(s => (string?)s.Attribute(x + "Key") == "DocumentTable"));
        var setters = style.Elements(p + "Setter").ToDictionary(
            s => (string)s.Attribute("Property")!, s => (string?)s.Attribute("Value"));
        Assert.Equal("True", setters["EnableRowVirtualization"]);
        Assert.Equal("True", setters["EnableColumnVirtualization"]);
        Assert.Equal("True", setters["VirtualizingPanel.IsVirtualizing"]);
        Assert.Equal("Recycling", setters["VirtualizingPanel.VirtualizationMode"]);
        Assert.Equal("Pixel", setters["VirtualizingPanel.ScrollUnit"]);
        Assert.Equal("True", setters["ScrollViewer.CanContentScroll"]);
        Assert.Equal("False", setters["ScrollViewer.IsDeferredScrollingEnabled"]);
    }

    [Fact]
    public void LayoutPerformanceRecorderUsesRealPanelsAndSyntheticData()
    {
        var root = Root();
        var source = File.ReadAllText(Path.Combine(root, "tests", "GmcSmeta.App.Checks", "LayoutPerformanceChecks.cs"));
        Assert.Contains("new EstimateMaterialsPanel()", source);
        Assert.Contains("new EstimateSummaryPanel()", source);
        Assert.Contains("new MainWindow", source);
        Assert.Contains("300, 2500, 10000, 25000", source);
        Assert.Contains("10, 50, 200", source);
        Assert.Contains("VisualTreeHelper.GetChildrenCount", source);
        Assert.Contains("layout-performance.csv", source);
        Assert.Contains("grid.ScrollIntoView(grid.Items[count - 1]", source);
        Assert.DoesNotContain("InitializeWorkspaceAsync(", source);
        Assert.DoesNotContain("SqlServerDatabase.Open(", source);
    }

    [Fact]
    public void StagePaletteUsesFrozenSharedBrushes()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "GmcSmeta.App", "EstimateView.InlineStages.cs"));
        Assert.Contains("StageBrushes.GetOrAdd", source);
        Assert.Contains("brush.Freeze();", source);
    }

    private static string Root()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        return root!.FullName;
    }
}
