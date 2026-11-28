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

    private static XElement Style(XDocument doc, string key) =>
        Assert.Single(doc.Descendants(W + "Style").Where(e => (string?)e.Attribute(X + "Key") == key));

    private static string? Value(XElement element, string property) =>
        (string?)Assert.Single(element.Elements(W + "Setter")
            .Where(e => (string?)e.Attribute("Property") == property)).Attribute("Value");

    [Fact]
    public void TotalsFrameClipsRoundedCornersAndKeepsWhiteBottomInset()
    {
        var doc = XDocument.Parse(Source("EstimateTotalsPanel.xaml"));
        var table = Named(doc, "TotalsGrid");
        XNamespace local = "clr-namespace:GmcSmeta.App";
        var frame = table.Parent!;
        Assert.Equal(local + "SummaryTableFrame", frame.Name);
        Assert.Equal("White", (string?)frame.Attribute("Background"));
        Assert.Equal("12", (string?)frame.Attribute("CornerRadius"));
        Assert.Equal("1", (string?)frame.Attribute("BorderThickness"));
        Assert.Equal("#E4E8EF", (string?)frame.Attribute("BorderBrush"));
        Assert.Equal("{StaticResource LayoutTotalsTablePadding}", (string?)frame.Attribute("Padding"));
        Assert.Equal(W + "StackPanel", frame.Parent!.Name);
        var metrics = XDocument.Parse(Source("Theme/LayoutMetrics.xaml"));
        string Metric(string key) => metrics.Descendants().Single(e => (string?)e.Attribute(X + "Key") == key).Value;
        Assert.Equal("0,0,0,4", Metric("LayoutTotalsTablePadding"));
        Assert.Equal("1060", Metric("LayoutTotalsMaxWidth"));
        Assert.Equal("0,0,0,12", Metric("LayoutTotalsTileSectionGap"));
        Assert.Equal("34", Metric("LayoutTotalsRowHeight"));
        var page = frame.Parent;
        Assert.Equal("Left", (string?)page.Attribute("HorizontalAlignment"));
        Assert.Equal("{Binding ViewportWidth, ElementName=TotalsScroll}", (string?)page.Attribute("Width"));
        var tiles = Assert.Single(page.Elements(W + "Grid"));
        Assert.Equal("{StaticResource LayoutTotalsTileSectionGap}", (string?)tiles.Attribute("Margin"));
        Assert.All(tiles.Elements(W + "Border"), b => Assert.Equal("0", (string?)b.Attribute("MinWidth")));
        var navigation = Named(XDocument.Parse(Source("EstimateView.xaml")), "ContractCostNavigation");
        Assert.Equal("{StaticResource LayoutDocumentToolbarMargin}", (string?)navigation.Attribute("Padding"));
        Assert.Equal(Metric("LayoutDocumentToolbarMargin").Split(',')[0], Metric("LayoutEmbeddedPageMargin").Split(',')[0]);
        Assert.Contains("Clip = geometry;", Source("SummaryTableFrame.cs"));
    }

    [Fact]
    public void TotalsTableKeepsCenteredHeadersReadOnlyColumnsAndStableEditorInsets()
    {
        var doc = XDocument.Parse(Source("EstimateTotalsPanel.xaml"));
        var shared = XDocument.Parse(Source("App.xaml"));
        var table = Named(doc, "TotalsGrid");
        Assert.Equal("False", (string?)table.Attribute("CanUserResizeRows"));
        Assert.Equal("True", (string?)table.Attribute("CanUserResizeColumns"));
        Assert.Equal("{StaticResource UiTotalsTableSize}", (string?)table.Attribute("FontSize"));
        var columns = table.Element(W + "DataGrid.Columns")!.Elements().ToArray();
        Assert.Equal(new[] { "name", "cost", "sell", "margin", "pct" },
            columns.Select(e => (string?)e.Attribute("SortMemberPath")).ToArray());
        Assert.Equal("190", (string?)columns[0].Attribute("MinWidth"));
        Assert.All(columns, col => Assert.Equal("{StaticResource TotalsArticleHeader}", (string?)col.Attribute("HeaderStyle")));
        foreach (var col in new[] { columns[0], columns[3], columns[4] })
            Assert.Equal("True", (string?)col.Attribute("IsReadOnly"));
        var header = Style(shared, "TotalsArticleHeader");
        Assert.Equal("Center", Value(header, "HorizontalContentAlignment"));
        Assert.Equal("36", Value(header, "MinHeight"));
        Assert.Equal("Center", (string?)Assert.Single(header.Descendants(W + "TextBlock")).Attribute("TextAlignment"));
        Assert.Equal("8,2", Value(Style(shared, "TotalsArticleCell"), "Padding"));
        Assert.Equal("Right", Value(Style(shared, "TotalsArticleMoney"), "TextAlignment"));
        var editor = Style(shared, "TotalsArticleEditor");
        Assert.Equal("0", Value(editor, "Margin"));
        Assert.Equal("0", Value(editor, "Padding"));
        Assert.Equal("Right", Value(editor, "TextAlignment"));
        var localEditor = Style(doc, "TotalsPageEditor");
        Assert.Equal("{StaticResource TotalsArticleEditor}", (string?)localEditor.Attribute("BasedOn"));
        foreach (var property in new[] { "FontWeight", "FontStyle" })
            Assert.Equal("{Binding " + property + ", RelativeSource={RelativeSource AncestorType=DataGridCell}}", Value(localEditor, property));
        var row = Style(doc, "TotalsPageRow");
        Assert.Equal("{StaticResource TotalsArticleRow}", (string?)row.Attribute("BasedOn"));
        Assert.Equal("{Binding MinRowHeight, RelativeSource={RelativeSource AncestorType=DataGrid}}", Value(row, "MinHeight"));
        Assert.Equal("{StaticResource TotalsPageRow}", (string?)table.Attribute("RowStyle"));
        Assert.Equal("{StaticResource TotalsPageEditor}", (string?)Assert.Single(table.Element(W + "DataGrid.Resources")!.Elements(W + "Style")).Attribute("BasedOn"));
    }

    [Fact]
    public void TotalsColorsAndTileHierarchyMatchTheReference()
    {
        var doc = XDocument.Parse(Source("EstimateTotalsPanel.xaml"));
        var shared = XDocument.Parse(Source("App.xaml"));
        var table = Named(doc, "TotalsGrid");
        var row = Style(shared, "TotalsArticleRow");
        var alternate = Assert.Single(row.Descendants(W + "Trigger").Where(e => (string?)e.Attribute("Property") == "AlternationIndex"));
        Assert.Equal("#F9FAFC", Value(alternate, "Background"));
        var subtotal = Assert.Single(row.Descendants(W + "DataTrigger").Where(e => (string?)e.Attribute("Binding") == "{Binding IsSubtotal}"));
        Assert.Equal("#F1F4F9", Value(subtotal, "Background"));
        var bold = Assert.Single(row.Descendants(W + "DataTrigger").Where(e => (string?)e.Attribute("Binding") == "{Binding IsBold}"));
        Assert.Equal("Bold", Value(bold, "FontWeight"));
        Assert.DoesNotContain(row.Descendants(W + "Setter"), e => (string?)e.Attribute("Value") == "#FFF7E6");
        foreach (var key in new[] { "Cost", "Sell" })
        {
            var cell = Style(shared, "Totals" + key + "Cell");
            var editable = Assert.Single(cell.Descendants(W + "MultiDataTrigger"));
            var conditions = editable.Descendants(W + "Condition").ToArray();
            Assert.Contains(conditions, e => (string?)e.Attribute("Binding") == "{Binding " + key + "Editable}" && (string?)e.Attribute("Value") == "True");
            Assert.Contains(conditions, e => (string?)e.Attribute("Binding") == "{Binding Kind}" && (string?)e.Attribute("Value") == "line");
            Assert.Contains(conditions, e => ((string?)e.Attribute("Binding") ?? "").Contains("AncestorType=DataGrid") && (string?)e.Attribute("Value") == "True");
            Assert.Equal("#FFF7E6", Value(editable, "Background"));
        }
        var margin = Style(shared, "TotalsArticleMargin");
        var negative = Assert.Single(margin.Descendants(W + "DataTrigger").Where(e => (string?)e.Attribute("Binding") == "{Binding MarginNeg}"));
        Assert.Equal("#DC2626", Value(negative, "Foreground"));
        var columns = table.Element(W + "DataGrid.Columns")!.Elements().ToArray();
        Assert.Equal("{StaticResource TotalsArticleMargin}", (string?)Assert.Single(columns[3].Descendants(W + "Style")).Attribute("BasedOn"));
        Assert.Equal("{StaticResource TotalsArticleMargin}", (string?)columns[4].Attribute("ElementStyle"));
        var tile = Named(doc, "TotalsTileSellValue").Ancestors(W + "Border").First();
        Assert.Equal("{StaticResource PythonTileAccent}", (string?)tile.Attribute("Style"));
        Assert.Equal(new[] { "#2563EB", "#4F46E5" }, Style(shared, "PythonTileAccent")
            .Descendants(W + "GradientStop").Select(e => (string?)e.Attribute("Color")).ToArray());
        Assert.Equal("White", (string?)Named(doc, "TotalsTileSellValue").Attribute("Foreground"));
        var code = Source("EstimateView.CostTotals.cs");
        Assert.Contains("public bool IsSubtotal => Kind == \"total\";", code);
        Assert.Contains("public bool IsBold => Kind is \"total\" or \"grand\";", code);
        Assert.Contains("$\"{Money.Format(marg)}  ·  {pct}\"", code);
    }
}
