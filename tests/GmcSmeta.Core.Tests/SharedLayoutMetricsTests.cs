using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class SharedLayoutMetricsTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Theory]
    [InlineData("ProcurementPane.xaml")]
    [InlineData("ShipmentsPane.xaml")]
    [InlineData("ContractActsWorkspace.xaml")]
    [InlineData("EstimateView.xaml")]
    public void EmbeddedPagesShareOuterMetrics(string file)
    {
        Assert.Contains("Margin=\"{StaticResource LayoutEmbeddedPageMargin}\"", Source(file));
    }

    [Fact]
    public void StandaloneCostRetainsItsOwnMargin()
    {
        var metrics = XDocument.Parse(Source("Theme/LayoutMetrics.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Equal("24,6,24,14", metrics.Descendants().Single(e => (string?)e.Attribute(x + "Key") == "LayoutCostPaneMargin").Value);
        Assert.Contains("Margin=\"{DynamicResource LayoutCostPaneMargin}\"", Source("CostSummaryPane.xaml"));
        Assert.Contains("<Thickness x:Key=\"LayoutCostPaneMargin\">22,18,22,16</Thickness>", Source("CostSummaryWindow.xaml"));
    }

    [Fact]
    public void ThemeUsesCommonMetricsWithoutRemovingSummaryInnerMargin()
    {
        var app = Source("App.xaml");
        Assert.Contains("Theme/LayoutMetrics.xaml", app);
        Assert.Contains("{StaticResource LayoutDocumentRowMinHeight}", app);
        Assert.Contains("{StaticResource LayoutCellPadding}", app);
        Assert.Contains("{StaticResource LayoutSummaryPageMargin}", Source("EstimateView.xaml"));
        Assert.Contains("36,14,44,30", Source("Theme/LayoutMetrics.xaml"));
    }
}
