using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateTypographyAndToolsSourceTests
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

    private static XElement Key(XDocument doc, string key) =>
        Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Key") == key));

    [Fact]
    public void PythonFontAndEstimateZoomRatioAreShared()
    {
        Assert.Equal("Segoe UI", Key(XDocument.Parse(Source("App.xaml")), "UiFontFamily").Value);
        Assert.Equal("0.95", Key(XDocument.Parse(Source("Theme/TypographyMetrics.xaml")), "UiEstimateHeaderZoomRatio").Value);
        var code = Source("EstimateView.TemporaryView.cs");
        Assert.Contains("EstimateGrid.FontSize = baseSize * zoom", code);
        Assert.Contains("baseSize * headerRatio * zoom", code);
        Assert.Contains("EstimateColumnsButton.ToolTip", code);
        Assert.Contains("Ctrl+0", code);
    }

    [Theory]
    [InlineData("Layers", "M12 3 3 8l9 5 9-5z M3 13l9 5 9-5")]
    [InlineData("Sort", "M4 5h12M4 10h9M4 15h6M19 4v16M16 17l3 3 3-3")]
    [InlineData("Download", "M12 4v11M7.5 10.5 12 15l4.5-4.5 M4 15v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3")]
    public void PythonToolbarGlyphsRetainOriginalGeometry(string name, string geometry)
    {
        var doc = XDocument.Parse(Source("Theme/ToolbarIcons.xaml"));
        Assert.Equal(geometry, (string?)Key(doc, "Glyph" + name).Attribute("Figures"));
        var template = Key(doc, "IconLabel" + name);
        var view = Assert.Single(template.Descendants(W + "Viewbox"));
        Assert.Equal("18", (string?)view.Attribute("Width"));
        Assert.Equal("18", (string?)view.Attribute("Height"));
        var canvas = Assert.Single(view.Descendants(W + "Canvas"));
        Assert.Equal("24", (string?)canvas.Attribute("Width"));
        Assert.Equal("24", (string?)canvas.Attribute("Height"));
        var path = Assert.Single(canvas.Descendants(W + "Path"));
        Assert.Equal("{StaticResource Glyph" + name + "}", (string?)path.Attribute("Data"));
        Assert.Contains("Foreground", (string?)path.Attribute("Stroke"));
        Assert.Contains(Key(doc, "SvgGlyphPath").Elements(W + "Setter"), s =>
            (string?)s.Attribute("Property") == "StrokeThickness" && (string?)s.Attribute("Value") == "1.9");
    }

    [Fact]
    public void GroupingAndSortingKeepStableWeightAndExactGap()
    {
        var doc = XDocument.Parse(Source("EstimateView.xaml"));
        var group = Assert.Single(doc.Descendants(W + "Button").Where(e => (string?)e.Attribute(X + "Name") == "GroupButton"));
        Assert.Equal("0,0,6,4", (string?)group.Attribute("Margin"));
        var sort = Assert.Single(doc.Descendants(W + "Button").Where(e => (string?)e.Attribute(X + "Name") == "SortButton"));
        Assert.Equal("{StaticResource DocumentFlatTool}", (string?)group.Attribute("Style"));
        Assert.Equal("{StaticResource DocumentFlatTool}", (string?)sort.Attribute("Style"));
        var code = Source("EstimateView.TemporaryView.cs");
        Assert.Contains("button.FontWeight = FontWeights.Normal", code);
        Assert.Contains("button.ClearValue(Control.BackgroundProperty)", code);
        Assert.Contains("button.ClearValue(Control.BorderBrushProperty)", code);
        Assert.DoesNotContain("button.Padding = on", code);
        Assert.DoesNotContain(": Brushes.Transparent", code[code.IndexOf("private void StyleBlue", StringComparison.Ordinal)..code.IndexOf("private void EstimateView_PreviewMouseWheel", StringComparison.Ordinal)]);
    }

    private static XElement Named(XDocument doc, string name) =>
        Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));

    private static string? Value(XElement element, string property) =>
        (string?)Assert.Single(element.Elements(W + "Setter")
            .Where(e => (string?)e.Attribute("Property") == property)).Attribute("Value");

    [Fact]
    public void EstimateToolbarHasSingleSplitStrokeFlatViewButtonsAndSharedToggle()
    {
        var doc = XDocument.Parse(Source("EstimateView.xaml"));
        var app = XDocument.Parse(Source("App.xaml"));
        var left = Key(doc, "DocumentSplitLeft");
        var right = Key(doc, "DocumentSplitRight");
        Assert.Equal("34", Value(left, "MinHeight"));
        Assert.Equal("24", Value(right, "MinWidth"));
        Assert.Equal("{StaticResource DocumentSplitLeft}", (string?)right.Attribute("BasedOn"));
        var leftChrome = Assert.Single(left.Descendants(W + "Border"));
        var rightChrome = Assert.Single(right.Descendants(W + "Border"));
        Assert.Equal("1,1,0,1", (string?)leftChrome.Attribute("BorderThickness"));
        Assert.Equal("1", (string?)rightChrome.Attribute("BorderThickness"));
        Assert.Equal("8,0,0,8", (string?)leftChrome.Attribute("CornerRadius"));
        Assert.Equal("0,8,8,0", (string?)rightChrome.Attribute("CornerRadius"));
        var add = Named(doc, "AddRowButton");
        var menu = Named(doc, "AddOptionsButton");
        Assert.Same(add.Parent, menu.Parent);
        Assert.Equal("AddRow_Click", (string?)add.Attribute("Click"));
        Assert.Equal("AddOptions_Click", (string?)menu.Attribute("Click"));
        Assert.Equal("{Binding IsEnabled, ElementName=AddRowButton}", (string?)menu.Attribute("IsEnabled"));
        Assert.Equal("12", (string?)Assert.Single(menu.Descendants(W + "Viewbox")).Attribute("Width"));
        Assert.Equal("34", Value(Key(doc, "DocumentEstimateTool"), "MinHeight"));
        var flat = Key(doc, "DocumentFlatTool");
        Assert.Equal("{StaticResource DocumentEstimateTool}", (string?)flat.Attribute("BasedOn"));
        Assert.Equal("Transparent", Value(flat, "Background"));
        Assert.Equal("Transparent", Value(flat, "BorderBrush"));
        Assert.Equal("9,6", Value(flat, "Padding"));
        var purchase = Named(doc, "PurchaseToggle");
        Assert.Equal("{StaticResource PythonToolbarToggle}", (string?)purchase.Attribute("Style"));
        var toggle = Key(doc, "PythonToolbarToggle");
        Assert.Equal("{StaticResource PythonToolToggle}", (string?)toggle.Attribute("BasedOn"));
        Assert.Equal("34", Value(toggle, "MinHeight"));
        Assert.Equal("ToggleButton", (string?)Key(app, "PythonToolToggle").Attribute("TargetType"));
        Assert.Equal("PurchaseToggle_Changed", (string?)purchase.Attribute("Checked"));
        Assert.Equal("PurchaseToggle_Changed", (string?)purchase.Attribute("Unchecked"));
        var toolbar = Named(doc, "EstimateToolsBar");
        var separator = Assert.Single(toolbar.Descendants(W + "Border"));
        Assert.Equal("1", (string?)separator.Attribute("Width"));
        Assert.Equal("22", (string?)separator.Attribute("Height"));
        Assert.Equal("#E4E8EF", (string?)separator.Attribute("Background"));
        Assert.Equal("0,0,6,4", (string?)separator.Attribute("Margin"));
        Assert.Contains("private const double Gap = 10;", Source("ResponsiveToolbarPanel.cs"));
    }

    [Fact]
    public void EstimateToolbarSearchHintAndGlyphsKeepTheirExactRoles()
    {
        var doc = XDocument.Parse(Source("EstimateView.xaml"));
        var app = XDocument.Parse(Source("App.xaml"));
        var icons = XDocument.Parse(Source("Theme/ToolbarIcons.xaml"));
        foreach (var (name, glyph) in new[] { ("AddRowButton", "RowPlus"), ("NoPriceFilterButton", "Alert"),
                     ("GroupButton", "Layers"), ("SortButton", "Sort"), ("EstimateColumnsButton", "List") })
        {
            var control = Named(doc, name);
            Assert.Equal("{StaticResource IconLabel" + glyph + "}", (string?)control.Attribute("ContentTemplate"));
            var viewbox = Assert.Single(Key(icons, "IconLabel" + glyph).Descendants(W + "Viewbox"));
            Assert.Equal("18", (string?)viewbox.Attribute("Width"));
            Assert.Equal("18", (string?)viewbox.Attribute("Height"));
            Assert.Equal("0,0,6,0", (string?)viewbox.Attribute("Margin"));
            Assert.Equal("24", (string?)Assert.Single(viewbox.Descendants(W + "Canvas")).Attribute("Width"));
        }
        Assert.Equal("M12 4 2.5 20h19z M12 10v4M12 17h.01", (string?)Key(icons, "GlyphAlert").Attribute("Figures"));
        Assert.Equal("M9 6h11M9 12h11M9 18h11M4 6h.01M4 12h.01M4 18h.01", (string?)Key(icons, "GlyphList").Attribute("Figures"));
        var search = Named(doc, "EstimateSearch");
        Assert.Equal("{StaticResource PythonIconSearch}", (string?)search.Attribute("Style"));
        Assert.Equal("170", (string?)search.Parent!.Attribute("Width"));
        Assert.Equal("34", (string?)search.Parent.Attribute("MinHeight"));
        Assert.Equal("34", Value(Key(app, "PythonIconSearch"), "MinHeight"));
        Assert.Equal("30,0,32,0", (string?)Assert.Single(Key(app, "PythonIconSearch").Descendants(W + "ScrollViewer")).Attribute("Margin"));
        Assert.Contains(search.Parent.Elements(W + "TextBlock"), e => (string?)e.Attribute("Text") == "Найти в смете…");
        Assert.Contains(search.Parent.Elements(W + "Button"), e => (string?)e.Attribute("Click") == "ClearEstimateSearch_Click");
        Assert.Equal("Collapsed", (string?)Named(doc, "HintBar").Attribute("Visibility"));
        var header = Source("EstimateView.Header.cs");
        Assert.Contains("HintBar.Visibility = Visibility.Collapsed;", header);
        Assert.Contains("_uiSettings.EstimateHintHidden = true;", header);
        Assert.Contains("SaveEstimateUiSettings();", header);
        Assert.Contains("_uiSettings.EstimateHintHidden ? Visibility.Collapsed : Visibility.Visible", Source("EstimateView.xaml.cs"));
    }

    [Fact]
    public void DocumentTabsAndTitleUsePythonTextIconsAndGradient()
    {
        var doc = XDocument.Parse(Source("EstimateView.xaml"));
        var app = XDocument.Parse(Source("App.xaml"));
        var title = Named(doc, "EditTitleButton");
        Assert.Equal("Изменить", (string?)title.Attribute("Content"));
        Assert.Equal("{StaticResource IconLabelEdit}", (string?)title.Attribute("ContentTemplate"));
        Assert.Equal("4,0,0,0", (string?)title.Attribute("Margin"));
        Assert.Equal("{StaticResource DocumentFlatTool}", (string?)Key(doc, "DocumentTitleEdit").Attribute("BasedOn"));
        Assert.Equal("96", Value(Key(doc, "DocumentMainTab"), "MinWidth"));
        foreach (var (name, glyph) in new[] { ("EstimateModeTab", "List"), ("MaterialsModeTab", "Building"),
                     ("ContractEstimateTab", "List"), ("ContractProcurementTab", "Download"),
                     ("ContractShipmentsTab", "Upload"), ("ContractActsTab", "File") })
        {
            var tab = Named(doc, name);
            Assert.Equal("{StaticResource DocumentMainTab}", (string?)tab.Attribute("Style"));
            Assert.Equal("{StaticResource IconLabel" + glyph + "}", (string?)tab.Attribute("ContentTemplate"));
        }
        var tabStyle = Key(app, "ObjectTab");
        var chrome = Assert.Single(tabStyle.Descendants(W + "Border"));
        Assert.Equal("1", (string?)chrome.Attribute("BorderThickness"));
        Assert.Equal("9", (string?)chrome.Attribute("CornerRadius"));
        Assert.Equal("16,7", (string?)chrome.Attribute("Padding"));
        Assert.Equal("0,0,6,6", (string?)chrome.Attribute("Margin"));
        var checkedTrigger = Assert.Single(tabStyle.Descendants(W + "Trigger").Where(e => (string?)e.Attribute("Property") == "IsChecked"));
        Assert.Equal(new[] { "#3B74F0", "#2158D8" }, checkedTrigger.Descendants(W + "GradientStop")
            .Select(e => (string?)e.Attribute("Color")).ToArray());
        Assert.Equal("White", Value(checkedTrigger, "Foreground"));
        var phase = Named(doc, "PhaseButton");
        Assert.Equal("{StaticResource DocumentPhaseTool}", (string?)phase.Attribute("Style"));
        Assert.Equal("{StaticResource GlyphHistory}", (string?)Assert.Single(phase.Descendants(W + "Path")).Attribute("Data"));
        Assert.Contains("PhaseButtonText.Text", Source("EstimateView.Header.cs"));
        Assert.Equal("10,6", Value(Key(doc, "DocumentPhaseTool"), "Padding"));
        var copy = Named(doc, "TransferRatesButton");
        Assert.Equal("{StaticResource DocumentPhaseTool}", (string?)copy.Attribute("Style"));
        Assert.Equal("{StaticResource IconLabelCopy}", (string?)copy.Attribute("ContentTemplate"));
        Assert.Equal("0", (string?)copy.Attribute("MinWidth"));
    }
}
