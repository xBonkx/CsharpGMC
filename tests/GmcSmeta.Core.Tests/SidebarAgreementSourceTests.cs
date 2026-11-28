using System.Xml.Linq;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SidebarAgreementSourceTests
{
    [Theory]
    [InlineData(NavigationMode.Contracts, "Новый договор", "Поиск договора")]
    [InlineData(NavigationMode.Kp, "Новое КП", "Поиск КП")]
    [InlineData(NavigationMode.Archive, "Новый договор", "Поиск в архиве")]
    public void CreationAndSearchCaptionsFollowNavigationMode(NavigationMode mode, string create, string search)
    {
        var captions = NavigationModeCaptions.For(mode);
        Assert.Equal(create, captions.Create);
        Assert.Equal(search, captions.Search);
        if (mode == NavigationMode.Kp)
            Assert.DoesNotContain("договор", captions.EmptyHeading + captions.EmptyHint + captions.CreateHint);
    }

    [Fact]
    public void SidebarSearchHasOneInsetAndNoFixedHeightOrLongPlaceholder()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "MainWindow.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var markup = XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var search = markup.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ObjectSearch");
        Assert.Null(search.Attribute("Height"));
        Assert.Null(search.Attribute("Padding"));
        Assert.Equal("{StaticResource SidebarSearch}", (string?)search.Attribute("Style"));
        Assert.Contains("Ctrl+P", (string?)search.Attribute("ToolTip"));
        var style = markup.Descendants().Single(e => (string?)e.Attribute(x + "Key") == "SidebarSearch");
        var host = style.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "PART_ContentHost");
        Assert.Equal("Center", (string?)host.Attribute("VerticalAlignment"));
        Assert.Equal("30,0,32,0", (string?)host.Attribute("Margin"));
        Assert.Contains(markup.Descendants(), e => (string?)e.Attribute("Text") == "Поиск договора…");
        Assert.Contains(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == "NewObjectButton" &&
            (string?)e.Attribute("Content") == "Новый договор");
        Assert.Contains(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == "EmptyNewObjectButton");
        var app = Path.Combine(root.FullName, "src", "GmcSmeta.App");
        var navigation = File.ReadAllText(Path.Combine(app, "MainWindow.NavigationTree.cs"));
        Assert.Contains("private void RefreshNavigation()", navigation);
        Assert.Contains("UpdateNavigationCaptions();", navigation);
        var captions = File.ReadAllText(Path.Combine(app, "MainWindow.NavigationCaptions.cs"));
        Assert.Contains("NewObjectButton.Content = EmptyNewObjectButton.Content = captions.Create;", captions);
    }

    [Fact]
    public void NavigationChromeKeepsFullWidthSelectionAndStableIndent()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "MainWindow.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var view = XDocument.Load(Path.Combine(app, "MainWindow.xaml"));
        XElement Named(string name) => Assert.Single(view.Descendants().Where(e => (string?)e.Attribute(x + "Name") == name));
        XElement Key(string name) => Assert.Single(view.Descendants().Where(e => (string?)e.Attribute(x + "Key") == name));
        var row = Named("HeaderBorder");
        var mark = Named("SelectionMark");
        Assert.Same(row.Parent, mark.Parent);
        Assert.Equal("Stretch", (string?)row.Attribute("HorizontalAlignment"));
        Assert.Equal("31", (string?)row.Attribute("MinHeight"));
        Assert.Equal("3", (string?)mark.Attribute("Width"));
        Assert.Equal("0", (string?)mark.Attribute("Margin"));
        Assert.Equal("Stretch", (string?)mark.Attribute("VerticalAlignment"));
        Assert.Equal("False", (string?)mark.Attribute("IsHitTestVisible"));
        var tree = Named("NavigationTree");
        Assert.Equal("Stretch", (string?)tree.Attribute("HorizontalContentAlignment"));
        Assert.Equal("Disabled", (string?)tree.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        var style = view.Descendants(w + "Style").Single(e => (string?)e.Attribute("TargetType") == "TreeViewItem");
        var selected = Assert.Single(style.Descendants(w + "Trigger").Where(e => (string?)e.Attribute("Property") == "IsSelected"));
        Assert.Contains(selected.Elements(w + "Setter"), e => (string?)e.Attribute("TargetName") == "HeaderBorder" &&
            (string?)e.Attribute("Value") == "#E8F0FE");
        Assert.Contains(selected.Elements(w + "Setter"), e => (string?)e.Attribute("TargetName") == "PART_Header" &&
            (string?)e.Attribute("Property") == "TextElement.FontWeight" && (string?)e.Attribute("Value") == "SemiBold");
        Assert.Equal("4,0,0,0", (string?)Named("PART_Header").Attribute("Margin"));
        var doc = Key("DocumentNavigationTemplate");
        Assert.Contains(doc.Descendants(w + "TextBlock"), e => (string?)e.Attribute("Text") == "{Binding DisplayName}" &&
            (string?)e.Attribute("TextTrimming") == "CharacterEllipsis" && (string?)e.Attribute("Margin") == "6,0,4,0");
        Assert.Equal("17", (string?)Named("PhaseBadge").Attribute("MinWidth"));
        Assert.Equal("17", (string?)Named("PhaseBadge").Attribute("MinHeight"));
        Assert.Null(Named("PhaseBadge").Attribute("Width"));
        Assert.Null(Named("PhaseBadge").Attribute("Height"));
        var date = Assert.Single(doc.Descendants(w + "TextBlock").Where(e => (string?)e.Attribute("Text") == "{Binding Document.NavigationDate}"));
        Assert.Equal("76", (string?)date.Attribute("MaxWidth"));
        Assert.Equal("{Binding Document.NavigationDate}", (string?)date.Attribute("ToolTip"));
        var source = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));
        Assert.Contains("new(16 * Math.Max(0, depth), 7, 4, 7)", source);
        var navigationTree = File.ReadAllText(Path.Combine(app, "MainWindow.NavigationTree.cs"));
        Assert.Contains("FontWeight = FontWeights.SemiBold", navigationTree);
        Assert.Contains("FontStyle = header.IsItalic ? FontStyles.Italic : FontStyles.Normal", navigationTree);
        var metrics = XDocument.Load(Path.Combine(app, "Theme", "LayoutMetrics.xaml"));
        var icon = Assert.Single(metrics.Descendants(w + "Style").Where(e => (string?)e.Attribute(x + "Key") == "LayoutNavigationIcon"));
        foreach (var dimension in new[] { "Width", "Height" })
            Assert.Contains(icon.Elements(w + "Setter"), e => (string?)e.Attribute("Property") == dimension &&
                (string?)e.Attribute("Value") == "17");
    }

    [Fact]
    public void SidebarIconsUseOriginalCanvasAndDocumentRoles()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "MainWindow.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var view = XDocument.Load(Path.Combine(app, "MainWindow.xaml"));
        XElement Named(string name) => Assert.Single(view.Descendants().Where(e => (string?)e.Attribute(x + "Name") == name));
        XElement Key(string name) => Assert.Single(view.Descendants().Where(e => (string?)e.Attribute(x + "Key") == name));
        foreach (var (name, size, glyph) in new[] { ("SidebarBrandButton", "28", "Layers"), ("SidebarCollapseButton", "18", "ChevronLeft"), ("MainMenuItem", "20", "Menu") })
        {
            var control = Named(name);
            var icon = Assert.Single(control.Descendants(w + "Viewbox"));
            Assert.Equal(size, (string?)icon.Attribute("Width"));
            Assert.Equal(size, (string?)icon.Attribute("Height"));
            var canvas = Assert.Single(icon.Descendants(w + "Canvas"));
            Assert.Equal("24", (string?)canvas.Attribute("Width"));
            Assert.Equal("24", (string?)canvas.Attribute("Height"));
            Assert.Equal("{StaticResource Glyph" + glyph + "}", (string?)Assert.Single(canvas.Descendants(w + "Path")).Attribute("Data"));
        }
        Assert.Equal("Center", (string?)Named("SidebarBrandButton").Attribute("VerticalAlignment"));
        Assert.Equal("Center", (string?)Named("SidebarCollapseButton").Attribute("VerticalAlignment"));
        Assert.Equal("#D0D7E2", (string?)Named("SidebarImportButton").Attribute("BorderBrush"));
        Assert.Equal("{StaticResource IconLabelUpload}", (string?)Named("SidebarImportButton").Attribute("ContentTemplate"));
        foreach (var (name, glyph) in new[] { ("YearNavigationTemplate", "History"), ("ObjectNavigationTemplate", "Folder"), ("DocumentNavigationTemplate", "File") })
        {
            var canvas = Assert.Single(Key(name).Descendants(w + "Canvas"));
            Assert.Equal("24", (string?)canvas.Attribute("Width"));
            Assert.Equal("24", (string?)canvas.Attribute("Height"));
            Assert.Equal("{StaticResource Glyph" + glyph + "}", (string?)Assert.Single(canvas.Descendants(w + "Path")).Attribute("Data"));
        }
        var document = Key("DocumentNavigationTemplate");
        foreach (var (role, glyph, color) in new[] { ("Stage", "Stage", "#2563EB"), ("Remainder", "Stage", "#64748B"), ("Parent", "Layers", "#2563EB") })
        {
            var trigger = Assert.Single(document.Descendants(w + "DataTrigger").Where(e => (string?)e.Attribute("Value") == role));
            Assert.Contains(trigger.Elements(w + "Setter"), e => (string?)e.Attribute("Property") == "Data" &&
                (string?)e.Attribute("Value") == "{StaticResource Glyph" + glyph + "}");
            Assert.Contains(trigger.Elements(w + "Setter"), e => (string?)e.Attribute("Property") == "Stroke" &&
                (string?)e.Attribute("Value") == color);
        }
        var icons = XDocument.Load(Path.Combine(app, "Theme", "ToolbarIcons.xaml"));
        string? Geometry(string glyph) => (string?)icons.Descendants(w + "PathGeometry")
            .Single(e => (string?)e.Attribute(x + "Key") == "Glyph" + glyph).Attribute("Figures");
        Assert.Equal("M12 3 3 8l9 5 9-5z M3 13l9 5 9-5", Geometry("Layers"));
        Assert.Equal("M6 4v8a3 3 0 0 0 3 3h10 M15 11l4 4-4 4", Geometry("Stage"));
        Assert.Equal("M11 6l-6 6 6 6M19 6l-6 6 6 6", Geometry("ChevronLeft"));
        Assert.Equal("M4 6h16M4 12h16M4 18h16", Geometry("Menu"));
        var search = Named("ObjectSearch");
        var searchGlyph = Assert.Single(search.Parent!.Elements(w + "Viewbox"));
        Assert.Equal("False", (string?)searchGlyph.Attribute("IsHitTestVisible"));
        Assert.Equal("9,0,0,0", (string?)searchGlyph.Attribute("Margin"));
        Assert.Equal("{StaticResource GlyphSearch}", (string?)Assert.Single(searchGlyph.Descendants(w + "Path")).Attribute("Data"));
    }
}
