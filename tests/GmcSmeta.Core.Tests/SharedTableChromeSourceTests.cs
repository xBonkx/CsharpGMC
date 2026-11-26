using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class SharedTableChromeSourceTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string AppDirectory()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "src", "GmcSmeta.App");
    }

    private static XElement Style(XDocument doc, string type, string? key = null) =>
        Assert.Single(doc.Descendants(Wpf + "Style").Where(s =>
            (string?)s.Attribute("TargetType") == type && (string?)s.Attribute(X + "Key") == key));

    private static string? Value(XElement style, string property) =>
        (string?)Assert.Single(style.Elements(Wpf + "Setter").Where(s =>
            (string?)s.Attribute("Property") == property)).Attribute("Value");

    [Fact]
    public void GridOwnsMinimumHeightAndEditorDoesNotDoubleCellPadding()
    {
        var dir = AppDirectory();
        var app = XDocument.Load(Path.Combine(dir, "App.xaml"));
        var layout = XDocument.Load(Path.Combine(dir, "Theme", "LayoutMetrics.xaml"));
        var minimum = Assert.Single(layout.Descendants().Where(e =>
            (string?)e.Attribute(X + "Key") == "LayoutDocumentRowMinHeight"));
        Assert.Equal("27", minimum.Value);
        Assert.Equal("{StaticResource LayoutDocumentRowMinHeight}", Value(Style(app, "DataGrid"), "MinRowHeight"));
        var row = Style(app, "DataGridRow", "PythonTableRow");
        Assert.DoesNotContain(row.Elements(Wpf + "Setter"), s =>
            (string?)s.Attribute("Property") is "Height" or "MinHeight");
        var editor = Style(app, "TextBox", "PythonGridEditor");
        Assert.Equal("0", Value(editor, "Padding"));
        Assert.Equal("0", Value(editor, "Margin"));
        Assert.Equal("0", Value(editor, "MinHeight"));
    }

    [Fact]
    public void EstimateSharesHeaderTemplateWithoutLosingResizeGrips()
    {
        var dir = AppDirectory();
        var app = XDocument.Load(Path.Combine(dir, "App.xaml"));
        var estimate = XDocument.Load(Path.Combine(dir, "EstimateView.xaml"));
        var local = Style(estimate, "DataGridColumnHeader");
        Assert.Equal("{StaticResource ReportGridHeader}", (string?)local.Attribute("BasedOn"));
        Assert.Equal("31", Value(local, "MinHeight"));
        var shared = Style(app, "DataGridColumnHeader", "ReportGridHeader");
        Assert.Equal("{StaticResource TableHeaderSurface}", Value(shared, "Background"));
        Assert.Equal("#E4E8EF", Value(shared, "BorderBrush"));
        Assert.Equal("0,0,1,1", Value(shared, "BorderThickness"));
        Assert.Contains(shared.Descendants(Wpf + "Border"), b =>
            (string?)b.Attribute("Background") == "{StaticResource LineStrong}" &&
            (string?)b.Attribute("IsHitTestVisible") == "False" &&
            (string?)b.Attribute("VerticalAlignment") == "Bottom");
        foreach (var name in new[] { "PART_LeftHeaderGripper", "PART_RightHeaderGripper" })
            Assert.Single(shared.Descendants(Wpf + "Thumb").Where(e => (string?)e.Attribute(X + "Name") == name));
        Assert.Single(shared.Descendants(Wpf + "Path").Where(e => (string?)e.Attribute(X + "Name") == "SortArrow"));
    }

    [Fact]
    public void ScrollThumbHasFullHitTargetAndKeepsLargeThumbPolicy()
    {
        var app = XDocument.Load(Path.Combine(AppDirectory(), "App.xaml"));
        var scroll = Style(app, "ScrollBar");
        Assert.Equal("11", Value(scroll, "Width"));
        var track = Assert.Single(scroll.Descendants(Wpf + "Track"));
        Assert.Equal("PART_Track", (string?)track.Attribute(X + "Name"));
        var thumb = Assert.Single(track.Descendants(Wpf + "Thumb"));
        var template = Assert.Single(thumb.Descendants(Wpf + "ControlTemplate"));
        var target = Assert.Single(template.Elements(Wpf + "Grid"));
        Assert.Equal("Transparent", (string?)target.Attribute("Background"));
        var chrome = Assert.Single(target.Elements(Wpf + "Border"));
        Assert.Equal("2", (string?)chrome.Attribute("Margin"));
        foreach (var state in new[] { "IsMouseOver", "IsDragging" })
            Assert.Contains(template.Descendants(Wpf + "Trigger"), t =>
                (string?)t.Attribute("Property") == state &&
                t.Elements(Wpf + "Setter").Any(s => (string?)s.Attribute("TargetName") == "ThumbChrome"));
        Assert.Contains(track.Descendants(), e => e.Value == "80" &&
            ((string?)e.Attribute(X + "Key"))?.Contains("VerticalScrollBarButtonHeightKey") == true);
        Assert.Contains(track.Descendants(), e => e.Value == "68" &&
            ((string?)e.Attribute(X + "Key"))?.Contains("HorizontalScrollBarButtonWidthKey") == true);
        Assert.Contains(scroll.Descendants(Wpf + "Setter"), s =>
            (string?)s.Attribute("TargetName") == "ThumbElement" &&
            (string?)s.Attribute("Property") == "MinHeight" && (string?)s.Attribute("Value") == "40");
    }
}
