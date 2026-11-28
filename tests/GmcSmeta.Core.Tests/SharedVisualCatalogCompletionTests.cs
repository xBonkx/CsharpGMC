using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Tests;

public sealed class SharedVisualCatalogCompletionTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static string AppRoot()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "src", "GmcSmeta.App");
    }
    private static XDocument Load(string file) => XDocument.Load(Path.Combine(AppRoot(), file));
    private static IEnumerable<string> XamlFiles() => Directory.EnumerateFiles(AppRoot(), "*.xaml", SearchOption.AllDirectories)
        .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                    !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
    private static string[] Tokens(string value) =>
        Regex.Matches(value, @"[A-Za-z]|[-+]?(?:\d+(?:\.\d*)?|\.\d+)").Select(m => m.Value).ToArray();

    // All Python theme.py SVGs; rect/circle/ellipse converted into equivalent path arcs.
    [Theory]
    [InlineData("plus", "GlyphPlus", "M12 5v14M5 12h14")]
    [InlineData("materials", "GlyphMaterials", "M 12 2 l 5 3 -5 3 -5 -3 z M 7 5 v 5 l 5 3 5 -3 V 5 M 12 8 v 5 M 6 11 l 5 3 -5 3 -5 -3 z M 1 14 v 5 l 5 3 5 -3 v -5 M 6 17 v 5 M 18 11 l 5 3 -5 3 -5 -3 M 13 14 v 5 l 5 3 5 -3 v -5 M 18 17 v 5")]
    [InlineData("wrench", "GlyphWrench", "M14 6a5 5 0 0 0-6 6L2 18a2.8 2.8 0 0 0 4 4l6-6a5 5 0 0 0 6-6l-3 3-4-4 3-3z")]
    [InlineData("sort", "GlyphSort", "M4 5h12M4 10h9M4 15h6M19 4v16M16 17l3 3 3-3")]
    [InlineData("folder", "GlyphFolder", "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z")]
    [InlineData("folder-plus", "GlyphFolderPlus", "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z M12 11v5M9.5 13.5h5")]
    [InlineData("file", "GlyphFile", "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z M14 3v5h5 M9 13h6M9 17h4")]
    [InlineData("file-plus", "GlyphFilePlus", "M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z M14 3v5h5 M12 11v6M9 14h6")]
    [InlineData("upload", "GlyphUpload", "M12 15V4M7.5 8.5 12 4l4.5 4.5 M4 15v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3")]
    [InlineData("download", "GlyphDownload", "M12 4v11M7.5 10.5 12 15l4.5-4.5 M4 15v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3")]
    [InlineData("excel", "GlyphExcel", "M6 3H18A2 2 0 0 1 20 5V19A2 2 0 0 1 18 21H6A2 2 0 0 1 4 19V5A2 2 0 0 1 6 3Z M8.5 8.5l7 7M15.5 8.5l-7 7")]
    [InlineData("book", "GlyphBook", "M5 4.5A1.5 1.5 0 0 1 6.5 3H19v15H6.5A1.5 1.5 0 0 0 5 19.5z M5 19.5A1.5 1.5 0 0 0 6.5 21H19v-3 M9 7h6")]
    [InlineData("settings", "GlyphSettings", "M15 12A3 3 0 1 0 9 12A3 3 0 1 0 15 12 M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z")]
    [InlineData("copy", "GlyphCopy", "M11 9H19A2 2 0 0 1 21 11V19A2 2 0 0 1 19 21H11A2 2 0 0 1 9 19V11A2 2 0 0 1 11 9Z M5 15H4a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v1")]
    [InlineData("trash", "GlyphTrash", "M4 7h16M10 11v6M14 11v6 M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12 M9 7V4h6v3")]
    [InlineData("up", "GlyphArrowUp", "M12 19V5M6 11l6-6 6 6")]
    [InlineData("down", "GlyphArrowDown", "M12 5v14M6 13l6 6 6-6")]
    [InlineData("edit", "GlyphEdit", "M4 20h4L19 9l-4-4L4 16z M13.5 6.5l4 4")]
    [InlineData("search", "GlyphSearch", "M18 11A7 7 0 1 0 4 11A7 7 0 1 0 18 11 M 20 20 l -3.5 -3.5")]
    [InlineData("layers", "GlyphLayers", "M12 3 3 8l9 5 9-5z M 3 13 l 9 5 9 -5")]
    [InlineData("row-plus", "GlyphRowPlus", "M4 6h16M4 12h9M4 18h9 M18 14v6M15 17h6")]
    [InlineData("sub", "GlyphStage", "M6 4v8a3 3 0 0 0 3 3h10 M 15 11 l 4 4 -4 4")]
    [InlineData("history", "GlyphHistory", "M3 12a9 9 0 1 0 3-6.7L3 8 M3 3v5h5 M12 7v5l3 2")]
    [InlineData("database", "GlyphDatabase", "M20 5A8 3 0 1 0 4 5A8 3 0 1 0 20 5 M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5 M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3")]
    [InlineData("building", "GlyphBuilding", "M5.5 3H18.5A1.5 1.5 0 0 1 20 4.5V19.5A1.5 1.5 0 0 1 18.5 21H5.5A1.5 1.5 0 0 1 4 19.5V4.5A1.5 1.5 0 0 1 5.5 3Z M9 7h1M14 7h1M9 11h1M14 11h1M9 15h1M14 15h1M10.5 21v-3h3v3")]
    [InlineData("more", "GlyphMore", "M6.2 12A1.2 1.2 0 1 0 3.8 12A1.2 1.2 0 1 0 6.2 12 M13.2 12A1.2 1.2 0 1 0 10.8 12A1.2 1.2 0 1 0 13.2 12 M20.2 12A1.2 1.2 0 1 0 17.8 12A1.2 1.2 0 1 0 20.2 12")]
    [InlineData("check", "GlyphCheck", "M5 12.5 10 17l9-10")]
    [InlineData("alert", "GlyphAlert", "M12 4 2.5 20h19z M12 10v4M12 17h.01")]
    [InlineData("refresh", "GlyphRefresh", "M20 11a8 8 0 0 0-14.3-4.9L4 8 M4 3v5h5 M4 13a8 8 0 0 0 14.3 4.9L20 16 M20 21v-5h-5")]
    [InlineData("list", "GlyphList", "M9 6h11M9 12h11M9 18h11M4 6h.01M4 12h.01M4 18h.01")]
    [InlineData("x", "GlyphX", "M6 6l12 12M18 6 6 18")]
    [InlineData("indent", "GlyphIndent", "M4 6h16M10 12h10M10 18h10 M 4 10 l 3 2 -3 2")]
    [InlineData("outdent", "GlyphOutdent", "M4 6h16M10 12h10M10 18h10 M 7 10 l -3 2 3 2")]
    [InlineData("duplicate", "GlyphDuplicate", "M10 8H18A2 2 0 0 1 20 10V18A2 2 0 0 1 18 20H10A2 2 0 0 1 8 18V10A2 2 0 0 1 10 8Z M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2 M14 11v6M11 14h6")]
    [InlineData("menu", "GlyphMenu", "M4 6h16M4 12h16M4 18h16")]
    [InlineData("chev-left", "GlyphChevronLeft", "M 11 6 l -6 6 6 6 M 19 6 l -6 6 6 6")]
    [InlineData("undo", "GlyphUndo", "M9 14 4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 0 11H11")]
    [InlineData("redo", "GlyphRedo", "M 15 14 l 5 -5 -5 -5 M20 9H9.5a5.5 5.5 0 0 0 0 11H13")]
    [InlineData("clip", "GlyphClip", "M 20 11.5 l -8.2 8.2 a 5 5 0 0 1 -7.1 -7.1 l 8.5 -8.5 a 3.3 3.3 0 0 1 4.7 4.7 l -8.5 8.5 a 1.7 1.7 0 0 1 -2.4 -2.4 l 7.8 -7.8")]
    [InlineData("users", "GlyphUsers", "M12.2 8A3.2 3.2 0 1 0 5.8 8A3.2 3.2 0 1 0 12.2 8 M3 20c0-3.3 2.7-6 6-6s6 2.7 6 6 M16 4.6a3.2 3.2 0 0 1 0 6.3M18 14.3c1.8.8 3 2.6 3 4.7")]
    [InlineData("open", "GlyphOpen", "M14 4h6v6M20 4l-9 9 M18 14v4a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4")]
    public void OriginalPythonGlyphIsPresent(string pythonName, string key, string expected)
    {
        var glyph = Assert.Single(Load("Theme/ToolbarIcons.xaml").Descendants(W + "PathGeometry")
            .Where(e => (string?)e.Attribute(X + "Key") == key));
        Assert.False(string.IsNullOrEmpty(pythonName));
        Assert.Equal(Tokens(expected), Tokens((string)glyph.Attribute("Figures")!));
    }

    [Fact]
    public void EveryXamlGlyphResourceAndCaptionTemplateResolves()
    {
        var catalog = Load("Theme/ToolbarIcons.xaml");
        var keys = catalog.Descendants().Select(e => (string?)e.Attribute(X + "Key"))
            .Where(s => s is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var file in XamlFiles())
        {
            var doc = XDocument.Load(file);
            foreach (var attribute in doc.Descendants().Attributes())
            foreach (Match match in Regex.Matches(attribute.Value, @"\{StaticResource ((?:Glyph|IconLabel|IconOnly|SvgGlyph)\w+)\}"))
                Assert.True(keys.Contains(match.Groups[1].Value), file + ": " + match.Groups[1].Value);
            foreach (var control in doc.Descendants().Where(e =>
                ((string?)e.Attribute("ContentTemplate"))?.StartsWith("{StaticResource Icon", StringComparison.Ordinal) == true))
            {
                var caption = (string?)control.Attribute("Content") ?? "";
                Assert.False(Regex.IsMatch(caption, @"^\s*[↻⟳⬇⬆✓✔✗✕✖🔒📎📁📄]"),
                    file + ": duplicated glyph in caption " + caption);
            }
        }
        foreach (var template in catalog.Descendants(W + "DataTemplate").Where(e =>
            ((string?)e.Attribute(X + "Key"))?.StartsWith("Icon", StringComparison.Ordinal) == true))
        {
            var view = Assert.Single(template.Descendants(W + "Viewbox"));
            Assert.Equal("{StaticResource SvgGlyphViewbox}", (string?)view.Attribute("Style"));
            Assert.Equal("False", (string?)view.Attribute("IsHitTestVisible"));
            Assert.Equal("Center", (string?)view.Attribute("VerticalAlignment"));
            Assert.Single(view.Descendants(W + "Path"));
            Assert.Equal("24", (string?)Assert.Single(view.Descendants(W + "Canvas")).Attribute("Width"));
        }
    }

    [Fact]
    public void NegativeSpacingAndHiddenReserveAreExplicitExceptions()
    {
        foreach (var file in XamlFiles())
        foreach (var element in XDocument.Load(file).Descendants())
        {
            var margin = (string?)element.Attribute("Margin");
            Assert.False(margin is not null && Regex.IsMatch(margin, @"(^|,)\s*-\d"), file + ": " + margin);
            if (element.Name == W + "Thickness" && Regex.IsMatch(element.Value, @"(^|,)\s*-\d"))
                Assert.Equal("LayoutAuxTabPaneOverlap", (string?)element.Attribute(X + "Key"));
            if ((string?)element.Attribute("Visibility") == "Hidden" ||
                ((string?)element.Attribute("Property") == "Visibility" && (string?)element.Attribute("Value") == "Hidden"))
            {
                Assert.Equal("MainWindow.xaml", Path.GetFileName(file));
                Assert.Equal("Expander", (string?)element.Attribute("TargetName"));
            }
        }
        foreach (var (file, name) in new[] { ("DirectorView.xaml", "DirectorTiles"), ("ActsEditorView.xaml", "TilesPanel") })
        {
            var panel = Assert.Single(Load(file).Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));
            Assert.Equal("EqualGapTilePanel", panel.Name.LocalName);
            Assert.Equal("{StaticResource LayoutTotalsTileGap}", (string?)panel.Attribute("Gap"));
            Assert.All(panel.Elements(W + "Border"), b => Assert.Null(b.Attribute("Margin")));
        }
    }

    [Theory]
    [InlineData("EstimateView.xaml", "ToggleSection_Click")]
    [InlineData("EstimateSummaryPanel.xaml", "SummaryToggleSystem_Click")]
    public void TreeAffordanceHasLargerInputTargetThanGlyph(string file, string handler)
    {
        var button = Assert.Single(Load(file).Descendants(W + "Button").Where(e => (string?)e.Attribute("Click") == handler));
        Assert.Equal("22", (string?)button.Attribute("Width"));
        Assert.Equal("24", (string?)button.Attribute("MinHeight"));
        var glyph = Assert.Single(button.Descendants(W + "Viewbox"));
        Assert.Equal("12", (string?)glyph.Attribute("Width"));
        Assert.NotNull(button.Attribute("AutomationProperties.Name"));
    }

    [Fact]
    public void CostToolbarUsesSemanticIcons()
    {
        var doc = Load("CostSummaryPane.xaml");
        foreach (var (handler, icon) in new[] { ("Transport_Click","Truck"), ("SmrModeSwitch_Click","Refresh"),
                     ("MaterialsMarkup_Click","List"), ("WorkMarkup_Click","List"), ("CatalogFill_Click","Book") })
        {
            var button = Assert.Single(doc.Descendants(W + "Button").Where(e => (string?)e.Attribute("Click") == handler));
            Assert.Equal("{StaticResource IconLabel" + icon + "}", (string?)button.Attribute("ContentTemplate"));
        }
    }

    [Fact]
    public void WindowsReceiveRenderingDefaultsWithoutOverridingPopupExceptions()
    {
        var root = AppRoot();
        var policy = File.ReadAllText(Path.Combine(root, "VisualRenderingPolicy.cs"));
        Assert.Contains("FrameworkElement.UseLayoutRoundingProperty, true", policy);
        Assert.Contains("UIElement.SnapsToDevicePixelsProperty, true", policy);
        Assert.Contains("TextFormattingMode.Display", policy);
        Assert.Contains("TextRenderingMode.ClearType", policy);
        Assert.Contains("source.BaseValueSource is BaseValueSource.Default or BaseValueSource.Inherited", policy);
        Assert.Contains("VisualRenderingPolicy.Install()", File.ReadAllText(Path.Combine(root, "App.xaml.cs")));
    }
}
