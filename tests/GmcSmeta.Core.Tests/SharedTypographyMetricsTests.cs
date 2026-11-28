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
        Assert.Equal("{StaticResource UiPageHeading}", (string?)title.Attribute("Style"));
        Assert.Equal("None", (string?)title.Attribute("TextTrimming"));
        Assert.DoesNotContain("FontSize=\"11.733333333333333\"", source);
    }

    [Theory]
    [InlineData("UiMutedTextBrush", "FFFFFF")]
    [InlineData("UiMutedTextBrush", "F4F6FA")]
    [InlineData("UiDisabledTextBrush", "FFFFFF")]
    [InlineData("UiDisabledTextBrush", "F4F6FA")]
    [InlineData("UiDisabledTextBrush", "F7F9FC")]
    [InlineData("UiDisabledTextBrush", "E8F0FE")]
    [InlineData("UiMutedTextBrush", "C9DBFB")]
    public void SemanticTextHasReadableContrastOnLightSurfaces(string key, string background)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var metrics = XDocument.Parse(Source("Theme/TypographyMetrics.xaml"));
        var color = (string)metrics.Descendants().Single(e => (string?)e.Attribute(x + "Key") == key).Attribute("Color")!;
        static double Luminance(string rgb)
        {
            rgb = rgb.TrimStart('#');
            double Channel(int offset)
            {
                var value = Convert.ToInt32(rgb.Substring(offset, 2), 16) / 255d;
                return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }
            return .2126 * Channel(0) + .7152 * Channel(2) + .0722 * Channel(4);
        }
        var a = Luminance(color);
        var b = Luminance(background);
        Assert.True((Math.Max(a, b) + .05) / (Math.Min(a, b) + .05) >= 4.5);
    }

    [Theory]
    [InlineData("ReportGridReviewText")]
    [InlineData("ReportGridMuted")]
    [InlineData("ReportGridStrongRight")]
    [InlineData("ReportGridCenter")]
    [InlineData("ReportGridRight")]
    public void ReportTextExposesCompleteValue(string key)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var style = XDocument.Parse(Source("App.xaml")).Descendants(w + "Style")
            .Single(e => (string?)e.Attribute(x + "Key") == key);
        Assert.Contains(style.Elements(w + "Setter"), s =>
            (string?)s.Attribute("Property") == "ToolTip" &&
            (string?)s.Attribute("Value") == "{Binding Text, RelativeSource={RelativeSource Self}}");
        if (key is "ReportGridReviewText" or "ReportGridMuted")
            Assert.Contains(style.Descendants(w + "Setter"), s =>
                (string?)s.Attribute("Property") == "Foreground" &&
                (string?)s.Attribute("Value") == "{StaticResource GridSelectionTextBrush}");
    }

}
