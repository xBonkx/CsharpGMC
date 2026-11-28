using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class DirectorAndCardVisualSourceTests
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

    private static XElement Style(XDocument doc, string key) =>
        Assert.Single(doc.Descendants(W + "Style").Where(s => (string?)s.Attribute(X + "Key") == key));

    [Fact]
    public void DirectorObjectHeaderAlignsTextLeftButNumericHeadersStayCentered()
    {
        var doc = XDocument.Parse(Source("DirectorView.xaml"));
        Assert.Equal("Left", (string?)Style(doc, "DirectorObjectHeader").Descendants(W + "TextBlock").Single().Attribute("TextAlignment"));
        Assert.Equal("Center", (string?)Style(doc, "DirectorHeader").Descendants(W + "TextBlock").Single().Attribute("TextAlignment"));
    }

    [Fact]
    public void DirectorWarningUsesPythonLiteralAndRowDoesNotUseWarningFill()
    {
        var doc = XDocument.Parse(Source("DirectorView.xaml"));
        var warning = Assert.Single(doc.Descendants(W + "TextBlock").Where(t => (string?)t.Attribute("Text") == "⚠"));
        Assert.Equal("{StaticResource DirectorObjectText}", (string?)warning.Attribute("Style"));
        Assert.Contains("Incomplete", (string?)warning.Attribute("Visibility"));
        Assert.DoesNotContain("Incomplete", Style(doc, "DirectorRow").ToString());
    }

    [Fact]
    public void DirectorTilesAdaptToWidthAndKeepEqualGaps()
    {
        var doc = XDocument.Parse(Source("DirectorView.xaml"));
        var tiles = Assert.Single(doc.Descendants(W + "UniformGrid").Where(e => (string?)e.Attribute(X + "Name") == "DirectorTiles"));
        Assert.Equal("DirectorTiles_SizeChanged", (string?)tiles.Attribute("SizeChanged"));
        Assert.Equal(4, tiles.Elements(W + "Border").Count());
        Assert.All(tiles.Elements(W + "Border"), b =>
            Assert.Equal("{StaticResource LayoutDirectorTileInset}", (string?)b.Attribute("Margin")));
        var cs = Source("DirectorView.xaml.cs");
        Assert.Contains("width < 920 ? 2 : count", cs);
        Assert.Contains("UpdateDirectorTileColumns();", cs);
    }

    [Fact]
    public void DocumentCardsKeepMetadataTooltipAndPythonLegendSpacing()
    {
        var doc = XDocument.Parse(Source("MainWindow.xaml"));
        var card = Assert.Single(doc.Descendants(W + "DataTemplate").Where(e => (string?)e.Attribute(X + "Key") == "DocumentCardTemplate"));
        var meta = Assert.Single(card.Descendants(W + "TextBlock").Where(e => (string?)e.Attribute("Text") == "{Binding Meta}"));
        Assert.Equal("CharacterEllipsis", (string?)meta.Attribute("TextTrimming"));
        Assert.Equal("{Binding Meta}", (string?)meta.Attribute("ToolTip"));
        Assert.Equal(2, card.Descendants(W + "StackPanel").Count(e => (string?)e.Attribute("Margin") == "{StaticResource LayoutCardLegendGap}"));
    }

    private static XElement Named(XDocument doc, string name) =>
        Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));

    private static string? Value(XElement element, string property) =>
        (string?)Assert.Single(element.Elements(W + "Setter")
            .Where(e => (string?)e.Attribute("Property") == property)).Attribute("Value");

    [Fact]
    public void DirectorTableKeepsAdaptiveHeaderAndRoundedContentWithoutDoubleBorder()
    {
        var doc = XDocument.Parse(Source("DirectorView.xaml"));
        var grid = Named(doc, "DirectorGrid");
        var header = Style(doc, "DirectorHeader");
        Assert.Equal("48", Value(header, "MinHeight"));
        Assert.DoesNotContain(header.Elements(W + "Setter"), e => (string?)e.Attribute("Property") == "Height");
        Assert.Equal("Wrap", (string?)Assert.Single(header.Descendants(W + "TextBlock")).Attribute("TextWrapping"));
        Assert.Equal("{StaticResource ReportGridHeader}", (string?)header.Attribute("BasedOn"));
        Assert.Equal("40", (string?)grid.Attribute("MinRowHeight"));
        Assert.Equal("{Binding MinRowHeight, RelativeSource={RelativeSource AncestorType=DataGrid}}",
            Value(Style(doc, "DirectorRow"), "MinHeight"));
        Assert.Equal("False", (string?)grid.Attribute("CanUserResizeRows"));
        Assert.Equal("True", (string?)grid.Attribute("CanUserResizeColumns"));
        Assert.Equal("True", (string?)grid.Attribute("CanUserSortColumns"));
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.Equal("True", (string?)grid.Attribute("EnableColumnVirtualization"));
        Assert.Equal("Auto", (string?)grid.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        XNamespace local = "clr-namespace:GmcSmeta.App";
        var clip = grid.Parent!;
        Assert.Equal(local + "SummaryTableFrame", clip.Name);
        Assert.Equal("White", (string?)clip.Attribute("Background"));
        Assert.Equal("11", (string?)clip.Attribute("CornerRadius"));
        Assert.Equal("0", (string?)clip.Attribute("BorderThickness"));
        Assert.Equal("0", (string?)grid.Attribute("BorderThickness"));
        Assert.Equal("{StaticResource DirectorTableFrame}", (string?)clip.Parent!.Attribute("Style"));
        var outer = Style(doc, "DirectorTableFrame");
        Assert.Equal("{StaticResource PythonTile}", (string?)outer.Attribute("BasedOn"));
        Assert.Equal("0", Value(outer, "Padding"));
        Assert.Equal("#E4E8EF", Value(outer, "BorderBrush"));
        var shadow = Assert.Single(outer.Descendants(W + "DropShadowEffect"));
        Assert.Equal("20", (string?)shadow.Attribute("BlurRadius"));
        Assert.Equal("2", (string?)shadow.Attribute("ShadowDepth"));
        var shared = XDocument.Parse(Source("App.xaml"));
        Assert.Equal("12", Value(Style(shared, "PythonTile"), "CornerRadius"));
        Assert.Equal("1", Value(Style(shared, "PythonTile"), "BorderThickness"));
        Assert.Contains("Clip = geometry;", Source("SummaryTableFrame.cs"));
    }

    [Fact]
    public void DirectorColumnsKeepPythonAlignmentWeightsAndProgressRoles()
    {
        var doc = XDocument.Parse(Source("DirectorView.xaml"));
        var grid = Named(doc, "DirectorGrid");
        var columns = grid.Element(W + "DataGrid.Columns")!.Elements().ToArray();
        Assert.Equal(new[] { "3*", "1.3*", "1.5*", "1.6*", "1.0*", "1.2*", "1.3*", "1.3*", "1.2*", "1.1*" },
            columns.Select(e => (string?)e.Attribute("Width")).ToArray());
        Assert.Equal("Right", Value(Style(doc, "DirectorMoney"), "TextAlignment"));
        Assert.Equal("Center", Value(Style(doc, "DirectorCenter"), "TextAlignment"));
        foreach (var (index, key) in new[] { (1, "DirectorMoney"), (3, "DirectorMoney"), (4, "DirectorCoverage"),
                     (5, "DirectorMoney"), (6, "DirectorExtra"), (7, "DirectorEarned"), (9, "DirectorLate") })
            Assert.Equal("{StaticResource " + key + "}", (string?)columns[index].Attribute("ElementStyle"));
        Assert.Equal("{StaticResource DirectorMoney}", (string?)Assert.Single(columns[2].Descendants(W + "TextBlock")).Attribute("Style"));
        Assert.Equal("{StaticResource DirectorCenter}", (string?)Assert.Single(columns[8].Descendants(W + "TextBlock")).Attribute("Style"));
        var progress = Style(doc, "DirectorProgressTrack");
        Assert.Equal("4", Value(progress, "Height"));
        Assert.Equal("#2563EB", Value(progress, "Foreground"));
        Assert.Equal("#F1F4F9", Value(progress, "Background"));
        Assert.Equal("False", Value(progress, "IsHitTestVisible"));
        Assert.All(progress.Descendants(W + "Border"), b => Assert.Equal("2", (string?)b.Attribute("CornerRadius")));
        foreach (var (index, prefix) in new[] { (2, "Closed"), (8, "Issued") })
        {
            var bar = Assert.Single(columns[index].Descendants(W + "ProgressBar"));
            Assert.Equal("{Binding " + prefix + "BarValue, Mode=OneWay}", (string?)bar.Attribute("Value"));
            Assert.Contains("Has" + prefix + "Progress", (string?)bar.Attribute("Visibility"));
            var complete = Assert.Single(bar.Descendants(W + "DataTrigger"));
            Assert.Equal("{Binding " + prefix + "Complete}", (string?)complete.Attribute("Binding"));
            Assert.Equal("#15803D", Value(complete, "Foreground"));
        }
        var code = Source("DirectorView.xaml.cs");
        Assert.Contains("ClosedPercent >= 99.5m", code);
        Assert.Contains("ProgressValue >= 99.5m", code);
        Assert.Contains("Math.Clamp(ClosedPercent ?? 0m, 0m, 100m)", code);
        Assert.Contains("Math.Clamp(ProgressValue ?? 0m, 0m, 100m)", code);
    }

    [Fact]
    public void DirectorPageRetainsNeutralRowsWarningTextAndPageSpacing()
    {
        var doc = XDocument.Parse(Source("DirectorView.xaml"));
        var row = Style(doc, "DirectorRow");
        var alternate = Assert.Single(row.Descendants(W + "Trigger").Where(e => (string?)e.Attribute("Property") == "AlternationIndex"));
        Assert.Equal("#F9FAFC", Value(alternate, "Background"));
        Assert.DoesNotContain("Incomplete", row.ToString());
        foreach (var (key, binding) in new[] { ("DirectorObjectText", "Incomplete"), ("DirectorCoverage", "IncompleteCoverage") })
        {
            var warning = Assert.Single(Style(doc, key).Descendants(W + "DataTrigger")
                .Where(e => (string?)e.Attribute("Binding") == "{Binding " + binding + "}"));
            Assert.Equal("#B45309", Value(warning, "Foreground"));
        }
        Assert.Equal("Bold", Value(Style(doc, "DirectorEarned"), "FontWeight"));
        var late = Assert.Single(Style(doc, "DirectorLate").Descendants(W + "DataTrigger")
            .Where(e => (string?)e.Attribute("Binding") == "{Binding HasLate}"));
        Assert.Contains("Просрочено", Value(late, "ToolTip"));
        var export = Style(doc, "DirectorExportButton");
        Assert.Equal("Transparent", Value(export, "Background"));
        Assert.Equal("Transparent", Value(export, "BorderBrush"));
        Assert.Equal("{StaticResource IconLabelDownload}", (string?)Named(doc, "ExportButton").Attribute("ContentTemplate"));
        var mode = Named(doc, "ModeCombo");
        Assert.Equal("Stretch", (string?)mode.Attribute("HorizontalAlignment"));
        Assert.Equal("{StaticResource LayoutDirectorSectionGap}", (string?)mode.Attribute("Margin"));
        var metrics = XDocument.Parse(Source("Theme/LayoutMetrics.xaml"));
        string Metric(string key) => metrics.Descendants().Single(e => (string?)e.Attribute(X + "Key") == key).Value;
        Assert.Equal("24,18,24,14", Metric("LayoutPageMargin"));
        Assert.Equal("0,12,0,0", Metric("LayoutDirectorSectionGap"));
        Assert.Equal("5,5", Metric("LayoutDirectorTileInset"));
        Assert.Equal("-5,7,-5,-5", Metric("LayoutDirectorTilesInset"));
        var footer = Named(doc, "LoadStatusPanel").Parent!;
        Assert.Equal("{StaticResource LayoutDirectorSectionGap}", (string?)footer.Attribute("Margin"));
        Assert.Contains(footer.Elements(W + "TextBlock"), e => ((string?)e.Attribute("Text"))?.Contains("Суммы с НДС.") == true);
    }

    [Fact]
    public void DirectorHeadingFilterAndCardsUseSharedMetrics()
    {
        var doc = XDocument.Parse(Source("DirectorView.xaml"));
        var shared = XDocument.Parse(Source("App.xaml"));
        var metrics = XDocument.Parse(Source("Theme/LayoutMetrics.xaml"));
        string Metric(string key) => metrics.Descendants().Single(e => (string?)e.Attribute(X + "Key") == key).Value;
        var crumb = Named(doc, "DirectorCrumb");
        var heading = Named(doc, "HeadingText");
        Assert.Same(crumb.Parent, heading.Parent);
        Assert.Equal("Top", (string?)heading.Parent!.Attribute("VerticalAlignment"));
        Assert.Equal("Главная", (string?)crumb.Attribute("Text"));
        Assert.Equal("0,0,0,2", (string?)crumb.Attribute("Margin"));
        Assert.Equal("{StaticResource UiMutedTextBrush}", (string?)crumb.Attribute("Foreground"));
        Assert.Equal("{StaticResource UiH1Size}", (string?)heading.Attribute("FontSize"));
        Assert.Equal("24", shared.Descendants().Single(e => (string?)e.Attribute(X + "Key") == "UiH1Size").Value);
        Assert.Equal("{StaticResource UiWrappedHeading}", (string?)heading.Attribute("Style"));
        Assert.Equal("Bottom", (string?)Named(doc, "ExportButton").Attribute("VerticalAlignment"));
        Assert.Equal("24,18,24,14", Metric("LayoutPageMargin"));
        var icons = XDocument.Parse(Source("Theme/ToolbarIcons.xaml"));
        var download = icons.Descendants(W + "DataTemplate").Single(e => (string?)e.Attribute(X + "Key") == "IconLabelDownload");
        var glyph = Assert.Single(download.Descendants(W + "Viewbox"));
        Assert.Equal("18", (string?)glyph.Attribute("Width"));
        Assert.Equal("18", (string?)glyph.Attribute("Height"));
        Assert.Equal("24", (string?)Assert.Single(glyph.Descendants(W + "Canvas")).Attribute("Width"));
        Assert.Equal("{StaticResource GlyphDownload}", (string?)Assert.Single(glyph.Descendants(W + "Path")).Attribute("Data"));
        Assert.Equal("M12 4v11M7.5 10.5 12 15l4.5-4.5 M4 15v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3",
            (string?)icons.Descendants(W + "PathGeometry").Single(e => (string?)e.Attribute(X + "Key") == "GlyphDownload").Attribute("Figures"));
        var combo = shared.Descendants(W + "Style").Single(e => (string?)e.Attribute("TargetType") == "ComboBox" && e.Attribute(X + "Key") is null);
        Assert.Equal("{StaticResource LayoutFieldMinHeight}", Value(combo, "MinHeight"));
        Assert.Equal("{StaticResource LayoutFieldPadding}", Value(combo, "Padding"));
        Assert.Equal("32", Metric("LayoutFieldMinHeight"));
        Assert.Equal("10,6", Metric("LayoutFieldPadding"));
        Assert.Equal("22", Metric("LayoutComboArrowWidth"));
        var comboChrome = combo.Descendants(W + "Border").Single(e => (string?)e.Attribute(X + "Name") == "ComboChrome");
        Assert.Equal("8", (string?)comboChrome.Attribute("CornerRadius"));
        var arrow = combo.Descendants(W + "Path").Single(e => (string?)e.Attribute(X + "Name") == "DropArrow");
        Assert.Equal("M5,8 L10,13 L15,8", (string?)arrow.Attribute("Data"));
        Assert.Equal("False", (string?)arrow.Attribute("IsHitTestVisible"));
        var cards = Named(doc, "DirectorTiles").Elements(W + "Border").ToArray();
        Assert.Equal(4, cards.Length);
        Assert.All(cards, card => Assert.Equal("0", (string?)card.Attribute("MinWidth")));
        var tile = Style(shared, "PythonTile");
        Assert.Equal("70", Value(tile, "MinHeight"));
        Assert.Equal("12", Value(tile, "CornerRadius"));
        Assert.Equal("{StaticResource LayoutTilePadding}", Value(tile, "Padding"));
        Assert.Equal("16,11,16,11", Metric("LayoutTilePadding"));
        var shadow = Assert.Single(tile.Descendants(W + "DropShadowEffect"));
        Assert.Equal("18", (string?)shadow.Attribute("BlurRadius"));
        Assert.Equal("2", (string?)shadow.Attribute("ShadowDepth"));
        Assert.Equal("Wrap", Value(Style(shared, "PythonTileValue"), "TextWrapping"));
    }

    [Fact]
    public void DirectorLongHeadersAndMoneyAreNotTrimmedAndSortIndicatorRemainsSeparate()
    {
        var doc = XDocument.Parse(Source("DirectorView.xaml"));
        var header = Style(doc, "DirectorHeader");
        Assert.Equal("{StaticResource TableHeaderSurface}", Value(header, "Background"));
        Assert.Equal("Stretch", Value(header, "HorizontalContentAlignment"));
        Assert.Equal("{StaticResource DirectorHeader}", (string?)Style(doc, "DirectorObjectHeader").Attribute("BasedOn"));
        foreach (var key in new[] { "DirectorHeader", "DirectorObjectHeader" })
        {
            var text = Assert.Single(Style(doc, key).Descendants(W + "TextBlock"));
            Assert.Equal("Wrap", (string?)text.Attribute("TextWrapping"));
            Assert.Equal("None", (string?)text.Attribute("TextTrimming"));
            Assert.Equal("MaxHeight", (string?)text.Attribute("LineStackingStrategy"));
        }
        foreach (var (name, width) in new[] { ("ExtraColumn", "120"), ("IssuedColumn", "108"), ("LateColumn", "118") })
        {
            var column = Named(doc, name);
            Assert.Equal(width, (string?)column.Attribute("MinWidth"));
            Assert.Contains("\n", (string?)column.Attribute("Header"));
        }
        var body = Style(doc, "DirectorText");
        Assert.Equal("Wrap", Value(body, "TextWrapping"));
        Assert.Equal("None", Value(body, "TextTrimming"));
        Assert.Equal("MaxHeight", Value(body, "LineStackingStrategy"));
        Assert.Equal("{StaticResource DirectorText}", (string?)Style(doc, "DirectorMoney").Attribute("BasedOn"));
        var shared = XDocument.Parse(Source("App.xaml"));
        var report = Style(shared, "ReportGridHeader");
        var sortArrow = report.Descendants(W + "Path").Single(e => (string?)e.Attribute(X + "Name") == "SortArrow");
        Assert.Equal("Right", (string?)sortArrow.Attribute("DockPanel.Dock"));
        Assert.Equal("Collapsed", (string?)sortArrow.Attribute("Visibility"));
        Assert.Equal("False", (string?)sortArrow.Attribute("IsHitTestVisible"));
        foreach (var direction in new[] { "Ascending", "Descending" })
        {
            var trigger = report.Descendants(W + "Trigger").Single(e => (string?)e.Attribute("Property") == "SortDirection" &&
                (string?)e.Attribute("Value") == direction);
            Assert.Contains(trigger.Elements(W + "Setter"), e => (string?)e.Attribute("TargetName") == "SortArrow" &&
                (string?)e.Attribute("Value") == "Visible");
        }
        var code = Source("DirectorView.xaml.cs");
        Assert.Contains("v is null ? \"—\" : $\"{v.Value:0}%\"", code);
        Assert.Contains("d.MarginPct is null ? \"—\"", code);
        Assert.Contains("d.SmrMargin is null ? (d.Smr != 0m ? \"не внесено\" : \"—\")", code);
        Assert.Contains("d.OutCost != 0m ? Money.Format(d.OutCost) : \"—\"", code);
        Assert.Contains("d.Late > 0 ? d.Late.ToString() : \"—\"", code);
        Assert.Contains("data.Out != 0m ? Money.Format(data.Out) : \"нет\"", code);
    }

    [Fact]
    public void SignedPdfMenuUsesSmallCanvasGlyphAndRetainsItsAction()
    {
        var doc = XDocument.Parse(Source("MainWindow.xaml"));
        var menu = doc.Descendants(W + "Button").Single(e => (string?)e.Attribute("Click") == "CardSignedPdfMenu_Click");
        var icon = Assert.Single(menu.Descendants(W + "Viewbox"));
        Assert.Equal("{StaticResource LayoutSmallAffordanceIcon}", (string?)icon.Attribute("Style"));
        Assert.Equal("24", (string?)Assert.Single(icon.Descendants(W + "Canvas")).Attribute("Width"));
        var path = Assert.Single(icon.Descendants(W + "Path"));
        Assert.Equal("{StaticResource GlyphChevronDown}", (string?)path.Attribute("Data"));
        Assert.Equal("{Binding Foreground, RelativeSource={RelativeSource AncestorType=Button}}", (string?)path.Attribute("Stroke"));
        Assert.Null(path.Attribute("Stretch"));
    }
}
