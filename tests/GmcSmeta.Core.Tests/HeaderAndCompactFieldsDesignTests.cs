using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class HeaderAndCompactFieldsDesignTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    private static XElement Named(XDocument doc, string name) =>
        Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));

    [Fact]
    public void HeaderUsesPythonCrumbAndTitleSpacing()
    {
        var crumb = Named(Load("EstimateView.xaml"), "SubtitleText");
        Assert.Equal("{StaticResource UiBodySize}", (string?)crumb.Attribute("FontSize"));
        Assert.Equal("300", (string?)crumb.Attribute("MaxWidth"));
        Assert.NotNull(crumb.Attribute("ToolTip"));
        Assert.Equal("0,2,0,0", Load("Theme/LayoutMetrics.xaml").Descendants()
            .Single(e => (string?)e.Attribute(X + "Key") == "LayoutHeaderTitleGap").Value);
    }

    [Theory]
    [InlineData("LockChip", "Lock", "Подписан")]
    [InlineData("SourceCheckChip", "X", "Не сходится с файлом")]
    public void StatusUsesVectorAndPlainText(string name, string glyph, string label)
    {
        var chip = Named(Load("EstimateView.xaml"), name);
        Assert.Equal("Collapsed", (string?)chip.Attribute("Visibility"));
        var path = Assert.Single(chip.Descendants(W + "Path"));
        Assert.Equal("{StaticResource Glyph" + glyph + "}", (string?)path.Attribute("Data"));
        Assert.Equal(label, (string?)Assert.Single(chip.Descendants(W + "TextBlock")).Attribute("Text"));
    }

    [Theory]
    [InlineData("Materials")]
    [InlineData("Truck")]
    public void NewCaptionIconsUseSharedViewboxAndStroke(string glyph)
    {
        var doc = Load("Theme/ToolbarIcons.xaml");
        Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Key") == "Glyph" + glyph));
        var template = Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Key") == "IconLabel" + glyph));
        var view = Assert.Single(template.Descendants(W + "Viewbox"));
        Assert.Equal("18", (string?)view.Attribute("Width"));
        Assert.Equal("False", (string?)view.Attribute("IsHitTestVisible"));
        Assert.Equal("24", (string?)Assert.Single(view.Descendants(W + "Canvas")).Attribute("Width"));
        Assert.Equal("{StaticResource Glyph" + glyph + "}", (string?)Assert.Single(view.Descendants(W + "Path")).Attribute("Data"));
    }

    [Theory]
    [InlineData("Transport_Click", "Truck")]
    [InlineData("SmrModeSwitch_Click", "Refresh")]
    [InlineData("Markup_Click", "List")]
    public void CostButtonsKeepHandlersAndGetIcons(string handler, string glyph)
    {
        var button = Assert.Single(Load("CostSummaryPane.xaml").Descendants(W + "Button")
            .Where(e => (string?)e.Attribute("Click") == handler));
        Assert.Equal("{StaticResource IconLabel" + glyph + "}", (string?)button.Attribute("ContentTemplate"));
    }

    [Theory]
    [InlineData("HistoryWindow.xaml", "SearchBox")]
    [InlineData("LettersWindow.xaml", "SearchBox")]
    [InlineData("LettersWindow.xaml", "StatusFilter")]
    [InlineData("OrdersWindow.xaml", "PersonBox")]
    [InlineData("OrdersWindow.xaml", "DayPicker")]
    [InlineData("OrdersWindow.xaml", "SiteBox")]
    [InlineData("PassportsWindow.xaml", "CatalogueBox")]
    [InlineData("PassportsWindow.xaml", "SectionCombo")]
    public void FontSensitiveFieldsCanGrow(string file, string name) =>
        Assert.Null(Named(Load(file), name).Attribute("Height"));

    [Fact]
    public void ActSearchHostCanGrowAndOrderMultilineFieldCanScroll()
    {
        var search = Named(Load("AllContractActsWindow.xaml"), "ActsSearch");
        Assert.Null(search.Parent!.Attribute("Height"));
        Assert.Equal("{StaticResource LayoutFieldMinHeight}", (string?)search.Parent.Attribute("MinHeight"));
        var site = Named(Load("OrdersWindow.xaml"), "SiteBox");
        Assert.Equal("Auto", (string?)site.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("True", (string?)site.Attribute("AcceptsReturn"));
    }
}
