using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateTotalsVisualSourceTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    private static XElement Named(XDocument doc, string name) =>
        Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));

    [Fact]
    public void TotalsPageScrollsVerticallyAndTableKeepsHorizontalScrolling()
    {
        var doc = XDocument.Parse(Source("EstimateTotalsPanel.xaml"));
        var scroll = Named(doc, "TotalsScroll");
        var table = Named(doc, "TotalsGrid");
        Assert.Contains(scroll, table.Ancestors());
        Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("Disabled", (string?)scroll.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal("Auto", (string?)table.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.Equal("Disabled", (string?)table.Attribute("ScrollViewer.VerticalScrollBarVisibility"));
        Assert.Equal("{StaticResource LayoutEmbeddedPageMargin}", (string?)scroll.Attribute("Margin"));
        Assert.Equal("{StaticResource LayoutTotalsMaxWidth}", (string?)scroll.Element(W + "StackPanel")!.Attribute("MaxWidth"));
    }

    [Fact]
    public void ThreeEqualTileColumnsHaveSeparateTenDipGaps()
    {
        var doc = XDocument.Parse(Source("EstimateTotalsPanel.xaml"));
        var tile = Named(doc, "TotalsTileCostValue").Ancestors(W + "Border").First();
        var grid = tile.Parent!;
        var widths = grid.Descendants(W + "ColumnDefinition").Select(c => (string?)c.Attribute("Width")).ToArray();
        Assert.Equal(new[] { "*", "{Binding Source={StaticResource LayoutTotalsTileGap}}", "*",
            "{Binding Source={StaticResource LayoutTotalsTileGap}}", "*" }, widths);
        Assert.Equal(new[] { "0", "2", "4" }, grid.Elements(W + "Border").Select(b => (string?)b.Attribute("Grid.Column")).ToArray());
        var metrics = XDocument.Parse(Source("Theme/LayoutMetrics.xaml"));
        Assert.Equal("10", metrics.Descendants().Single(e => (string?)e.Attribute(X + "Key") == "LayoutTotalsTileGap").Value);
    }

    [Fact]
    public void ArticleTableHasVisibleCellBordersAndAdaptiveHeaderHeight()
    {
        var table = Named(XDocument.Parse(Source("EstimateTotalsPanel.xaml")), "TotalsGrid");
        Assert.Equal("All", (string?)table.Attribute("GridLinesVisibility"));
        Assert.Equal("#E4E8EF", (string?)table.Attribute("VerticalGridLinesBrush"));
        Assert.Equal("#E4E8EF", (string?)table.Attribute("HorizontalGridLinesBrush"));
        Assert.Null(table.Attribute("ColumnHeaderHeight"));
        Assert.Equal("{StaticResource LayoutTotalsRowHeight}", (string?)table.Attribute("MinRowHeight"));
        Assert.Equal(new[] { "*", "190", "190", "160", "100" },
            table.Element(W + "DataGrid.Columns")!.Elements().Select(c => (string?)c.Attribute("Width")).ToArray());
        Assert.Equal(2, table.Descendants(W + "DataGridTemplateColumn.CellEditingTemplate").Count());
    }
}
