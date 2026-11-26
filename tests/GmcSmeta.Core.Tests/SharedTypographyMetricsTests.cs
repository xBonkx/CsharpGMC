using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class SharedTypographyMetricsTests
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
    [InlineData("UiSummaryTitleSize", "19")]
    [InlineData("UiSummaryValueSize", "22")]
    [InlineData("UiSummaryCaptionSize", "11")]
    [InlineData("UiMoneyInputSize", "16")]
    [InlineData("UiGrandTotalSize", "24")]
    [InlineData("UiTotalDetailSize", "12")]
    public void PythonRoleOverridesAreNotDoubleScaled(string key, string value)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var metrics = XDocument.Parse(Source("Theme/TypographyMetrics.xaml"));
        Assert.Equal(value, metrics.Descendants().Single(e => (string?)e.Attribute(x + "Key") == key).Value);
    }

    [Fact]
    public void SummaryAndMoneyEditorsUseTheirOwnRoles()
    {
        Assert.Contains("Theme/TypographyMetrics.xaml", Source("App.xaml"));
        Assert.Contains("FontSize=\"{StaticResource UiSummaryTitleSize}\"", Source("EstimateSummaryPanel.xaml"));
        Assert.Contains("{StaticResource UiSummaryValueSize}", Source("EstimateSummaryPanel.xaml"));
        Assert.Contains("{StaticResource UiMoneyInputSize}", Source("EstimateView.xaml"));
        Assert.Contains("{StaticResource UiMoneyInputSize}", Source("ActsEditorView.xaml"));
    }
}
