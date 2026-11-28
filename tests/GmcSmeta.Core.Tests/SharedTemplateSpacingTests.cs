using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class SharedTemplateSpacingTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }
    private static XElement Style(string key) => Assert.Single(XDocument.Parse(Source("App.xaml")).Descendants(W + "Style")
        .Where(e => (string?)e.Attribute(X + "Key") == key));
    private static string? Value(XElement element, string property) =>
        (string?)Assert.Single(element.Elements(W + "Setter").Where(e => (string?)e.Attribute("Property") == property)).Attribute("Value");

    [Theory]
    [InlineData("Segment", "LayoutSegmentPadding")]
    [InlineData("ObjectTab", "LayoutObjectTabPadding")]
    [InlineData("ObjectSubTab", "LayoutObjectSubTabPadding")]
    [InlineData("PythonTabAction", "LayoutObjectTabPadding")]
    [InlineData("PythonFilterChip", "LayoutFilterChipPadding")]
    [InlineData("PythonFilterRadio", "LayoutFilterChipPadding")]
    [InlineData("PythonSecondaryTab", "LayoutAuxTabHeaderPadding")]
    public void TemplateOwnsOnePaddingAndRespectsContentAlignment(string key, string metric)
    {
        var style = Style(key);
        Assert.Equal("{StaticResource " + metric + "}", Value(style, "Padding"));
        var border = Assert.Single(style.Descendants(W + "ControlTemplate").First().Descendants(W + "Border"));
        Assert.Equal("{TemplateBinding Padding}", (string?)border.Attribute("Padding"));
        var presenter = Assert.Single(border.Descendants(W + "ContentPresenter"));
        Assert.Null(presenter.Attribute("Margin"));
        Assert.Equal("{TemplateBinding HorizontalContentAlignment}", (string?)presenter.Attribute("HorizontalAlignment"));
        Assert.Equal("{TemplateBinding VerticalContentAlignment}", (string?)presenter.Attribute("VerticalAlignment"));
    }

    [Theory]
    [InlineData("CompactCostText")]
    [InlineData("TotalsArticleText")]
    [InlineData("ReportGridReviewText")]
    [InlineData("ReportGridMuted")]
    [InlineData("ReportGridCenter")]
    [InlineData("ReportGridRight")]
    [InlineData("ReportGridStrongRight")]
    public void CellContentHasNoExtraInset(string key)
    {
        var style = Style(key);
        Assert.Equal("0", Value(style, "Padding"));
        Assert.Equal("0", Value(style, "Margin"));
        Assert.Equal("Stretch", Value(style, "HorizontalAlignment"));
        Assert.Equal("Center", Value(style, "VerticalAlignment"));
    }

    [Fact]
    public void SidebarSplitterOwnsItsHitTargetAndReopenControlOwnsSeparateRow()
    {
        var doc = XDocument.Parse(Source("MainWindow.xaml"));
        XElement Named(string name) => Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));
        var splitter = Named("SidebarSplitter");
        Assert.Null(splitter.Attribute("Margin"));
        Assert.Equal("{StaticResource LayoutSidebarSplitterWidth}", (string?)splitter.Attribute("Width"));
        Assert.Equal("{Binding Source={StaticResource LayoutSidebarSplitterWidth}}", (string?)Named("SplitterColumn").Attribute("Width"));
        Assert.Equal("0", (string?)Named("ShowSidebarButton").Attribute("Grid.Row"));
        Assert.Equal("1", (string?)Named("PageHost").Attribute("Grid.Row"));
        var code = Source("MainWindow.xaml.cs");
        Assert.Contains("hide ? 0 : (double)FindResource(\"LayoutSidebarSplitterWidth\")", code);
        Assert.Contains("if (hide) ShowSidebarButton.Focus();", code);
        Assert.Contains("else ObjectSearch.Focus();", code);
    }
}
