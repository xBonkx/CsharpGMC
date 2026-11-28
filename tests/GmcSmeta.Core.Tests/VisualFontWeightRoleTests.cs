using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class VisualFontWeightRoleTests
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

    private static XElement Style(string file, string key) =>
        Assert.Single(Load(file).Descendants(W + "Style").Where(s => (string?)s.Attribute(X + "Key") == key));

    private static string? Weight(XElement e) => (string?)Assert.Single(e.Elements(W + "Setter")
        .Where(s => (string?)s.Attribute("Property") == "FontWeight")).Attribute("Value");

    [Theory]
    [InlineData("Theme/TypographyMetrics.xaml", "UiPageHeading", "Bold")]
    [InlineData("Theme/TypographyMetrics.xaml", "UiHeadingText", "SemiBold")]
    [InlineData("App.xaml", "UiCaption", "SemiBold")]
    [InlineData("App.xaml", "PythonTileLabel", "SemiBold")]
    [InlineData("App.xaml", "PythonTileValue", "Bold")]
    [InlineData("App.xaml", "UiMoneyTileEditor", "SemiBold")]
    [InlineData("App.xaml", "PrimaryButton", "SemiBold")]
    [InlineData("App.xaml", "PythonToolbarPrimary", "SemiBold")]
    [InlineData("App.xaml", "ObjectTab", "SemiBold")]
    [InlineData("App.xaml", "ReportGridHeader", "SemiBold")]
    [InlineData("App.xaml", "Segment", "Normal")]
    [InlineData("App.xaml", "PythonSecondaryTab", "Normal")]
    [InlineData("EstimateSummaryPanel.xaml", "SummaryTileNumber", "Bold")]
    public void SharedRolesMatchFinalQtWeights(string file, string key, string weight) =>
        Assert.Equal(weight, Weight(Style(file, key)));

    [Fact]
    public void MainAndNestedSystemAndTemporaryGroupHaveDistinctWeights()
    {
        var row = Style("EstimateView.xaml", "EstimateRow");
        XElement Trigger(string property) => Assert.Single(row.Descendants(W + "DataTrigger").Where(e =>
            (string?)e.Attribute("Binding") == "{Binding " + property + "}" &&
            (string?)e.Attribute("Value") == "True"));
        Assert.Equal("Bold", Weight(Trigger("IsSystem")));
        Assert.Equal("SemiBold", Weight(Trigger("IsGroup")));
        var nested = Assert.Single(row.Descendants(W + "MultiDataTrigger").Where(e =>
            e.Descendants(W + "Condition").Any(c => (string?)c.Attribute("Binding") == "{Binding Level}" &&
                (string?)c.Attribute("Value") == "1")));
        Assert.Equal("SemiBold", Weight(nested));
    }

    [Fact]
    public void SummaryAndTotalsUseBoldForSystemAndTotalRowsButNormalForExtraRows()
    {
        var grid = Assert.Single(Load("EstimateSummaryPanel.xaml").Descendants(W + "DataGrid")
            .Where(e => (string?)e.Attribute(X + "Name") == "SummaryGrid"));
        var row = Assert.Single(grid.Element(W + "DataGrid.RowStyle")!.Elements(W + "Style"));
        foreach (var (property, expected) in new[] { ("IsSystemHeader", "Bold"), ("IsTotalRow", "Bold"), ("IsExtraRow", "Normal") })
            Assert.Equal(expected, Weight(Assert.Single(row.Descendants(W + "DataTrigger").Where(e =>
                (string?)e.Attribute("Binding") == "{Binding " + property + "}" &&
                (string?)e.Attribute("Value") == "True"))));
        var totalRow = Style("App.xaml", "TotalsArticleRow");
        Assert.Equal("Bold", Weight(Assert.Single(totalRow.Descendants(W + "DataTrigger").Where(e =>
            (string?)e.Attribute("Binding") == "{Binding IsBold}"))));
    }

    [Fact]
    public void DirectorAndShellUseTheSamePageHeading()
    {
        var cases = new[] { ("MainWindow.xaml", "ObjectTitle"), ("MainWindow.xaml", "EmptyNavigationHeading"),
            ("DirectorView.xaml", "HeadingText") };
        foreach (var (file, name) in cases)
        {
            var heading = Assert.Single(Load(file).Descendants(W + "TextBlock")
                .Where(e => (string?)e.Attribute(X + "Name") == name));
            Assert.Equal("{StaticResource UiPageHeading}", (string?)heading.Attribute("Style"));
            Assert.Equal("{StaticResource UiH1Size}", (string?)heading.Attribute("FontSize"));
            Assert.Null(heading.Attribute("FontWeight"));
        }
    }
}
