using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateSelectionChromeSourceTests
{
    [Fact]
    public void SelectionSuppressesGridSeamsButKeepsFocusedEditorFrame()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
                   "src", "GmcSmeta.App", "EstimateView.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var doc = XDocument.Load(Path.Combine(root!.FullName,
            "src", "GmcSmeta.App", "EstimateView.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = doc.Descendants(p + "DataGrid")
            .Single(e => (string?)e.Attribute(x + "Name") == "EstimateGrid");
        Assert.Equal("None", (string?)grid.Attribute("GridLinesVisibility"));
        var style = doc.Descendants(p + "Style")
            .Single(e => (string?)e.Attribute(x + "Key") == "EstimateCell");
        var lines = style.Descendants(p + "Border")
            .Single(e => (string?)e.Attribute(x + "Name") == "EstimateCellGridLines");
        Assert.Equal("0,0,1,1", (string?)lines.Attribute("BorderThickness"));
        Assert.Equal("#E4E8EF", (string?)lines.Attribute("BorderBrush"));
        Assert.Contains(style.Descendants(p + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsSelected" &&
            (string?)trigger.Attribute("Value") == "True" &&
            trigger.Elements(p + "Setter").Any(s =>
                (string?)s.Attribute("TargetName") == "EstimateCellGridLines" &&
                (string?)s.Attribute("Value") == "Transparent"));
        foreach (var state in new[] { "IsKeyboardFocusWithin", "IsEditing" })
            Assert.Contains(style.Descendants(p + "Trigger"), trigger =>
                (string?)trigger.Attribute("Property") == state &&
                trigger.Elements(p + "Setter").Any(s =>
                    (string?)s.Attribute("TargetName") == "EstimateFocusFrame" &&
                    (string?)s.Attribute("Value") == "{StaticResource GridFocusBrush}"));
        var focus = style.Descendants(p + "Rectangle")
            .Single(e => (string?)e.Attribute(x + "Name") == "EstimateFocusFrame");
        Assert.Equal("2", (string?)focus.Attribute("StrokeThickness"));
        Assert.Equal("1", (string?)focus.Attribute("Margin"));
        Assert.Equal("False", (string?)focus.Attribute("IsHitTestVisible"));
        foreach (var state in new[] { "IsKeyboardFocusWithin", "IsEditing" })
            Assert.Contains(style.Element(p + "Style.Triggers")!.Elements(p + "Trigger"), trigger =>
                (string?)trigger.Attribute("Property") == state &&
                trigger.Elements(p + "Setter").Any(setter =>
                    (string?)setter.Attribute("Property") == "BorderBrush" &&
                    (string?)setter.Attribute("Value") == "Transparent"));
        foreach (var key in new[] { "EstimatePriceCell", "EstimateWorkPriceCell" })
            Assert.Contains(doc.Descendants(p + "Style"), e =>
                (string?)e.Attribute(x + "Key") == key &&
                (string?)e.Attribute("BasedOn") == "{StaticResource EstimateCell}");
    }

    [Fact]
    public void SelectionOverridesLocalNoteAreaAndStageColors()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,
                   "src", "GmcSmeta.App", "EstimateView.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var doc = XDocument.Load(Path.Combine(app, "EstimateView.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        const string selected = "{Binding IsSelected, RelativeSource={RelativeSource AncestorType=DataGridCell}}";
        foreach (var key in new[] { "Price", "WorkPrice", "EstimateNoteDisplay" })
        {
            var style = Assert.Single(doc.Descendants(p + "Style")
                .Where(e => (string?)e.Attribute(x + "Key") == key));
            var trigger = style.Descendants(p + "DataTrigger").Last();
            Assert.Equal(selected, (string?)trigger.Attribute("Binding"));
            Assert.Contains(trigger.Elements(p + "Setter"), setter =>
                (string?)setter.Attribute("Property") == "Foreground" &&
                (string?)setter.Attribute("Value") == "{StaticResource GridSelectionTextBrush}");
        }
        var area = Assert.Single(doc.Descendants(p + "DataGridTemplateColumn")
            .Where(e => (string?)e.Attribute("SortMemberPath") == "areaUnit"));
        var areaSelection = area.Descendants(p + "DataTrigger").Last();
        Assert.Equal(selected, (string?)areaSelection.Attribute("Binding"));
        Assert.Contains(areaSelection.Elements(p + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Background" &&
            (string?)setter.Attribute("Value") == "Transparent");
        Assert.Contains(areaSelection.Elements(p + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Foreground" &&
            (string?)setter.Attribute("Value") == "{StaticResource GridSelectionTextBrush}");
        var stages = File.ReadAllText(Path.Combine(app, "EstimateView.InlineStages.cs"));
        Assert.Contains("new Binding(nameof(DataGridCell.IsSelected))", stages);
        Assert.Contains("new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1)", stages);
        Assert.Contains("selected.Setters.Add(new Setter(TextBlock.ForegroundProperty, FindResource(\"GridSelectionTextBrush\")))", stages);
        Assert.True(stages.IndexOf("style.Triggers.Add(warning)", StringComparison.Ordinal) <
                    stages.IndexOf("style.Triggers.Add(selected)", StringComparison.Ordinal));
    }
}
