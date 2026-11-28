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
    [Theory]
    [InlineData("UiBodySize", 10)]
    [InlineData("UiCaptionSize", 8.5)]
    [InlineData("UiH1Size", 18)]
    [InlineData("UiH2Size", 12)]
    [InlineData("UiValueSize", 15)]
    [InlineData("UiAppTitleSize", 13)]
    public void QtPointRolesConvertOnce(string key, double points)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var value = XDocument.Parse(Source("App.xaml")).Descendants().Single(e => (string?)e.Attribute(x + "Key") == key).Value;
        Assert.Equal(points * 96 / 72, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture), 8);
    }

    [Theory]
    [InlineData("EstimateView.xaml", "PythonMoneyEdit")]
    [InlineData("ActsEditorView.xaml", "KsTileInput")]
    public void MoneyEditorsInheritSharedRole(string file, string key)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var style = XDocument.Parse(Source(file)).Descendants().Single(e => (string?)e.Attribute(x + "Key") == key);
        Assert.Equal("{StaticResource UiMoneyTileEditor}", (string?)style.Attribute("BasedOn"));
    }

    [Theory]
    [InlineData("WorkspaceQueryWindows.cs")]
    [InlineData("ProcurementDialogChrome.cs")]
    public void DynamicDialogChromeUsesSemanticTextAndRendering(string file)
    {
        var source = Source(file);
        Assert.Contains("UiHeadingText", source);
        Assert.Contains("UiSecondaryText", source);
        Assert.Contains("SetTextRenderingMode", source);
    }

    [Fact]
    public void ObjectHeadingUsesNaturalWrappingAndNoLocalNavigationFontSizes()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = Source("MainWindow.xaml");
        var title = XDocument.Parse(source).Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ObjectTitle");
        Assert.Equal("{StaticResource UiWrappedHeading}", (string?)title.Attribute("Style"));
        Assert.Equal("None", (string?)title.Attribute("TextTrimming"));
        Assert.DoesNotContain("FontSize=\"11.733333333333333\"", source);
    }

}
