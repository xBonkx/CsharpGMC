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
        foreach (var key in new[] { "EstimatePriceCell", "EstimateWorkPriceCell" })
            Assert.Contains(doc.Descendants(p + "Style"), e =>
                (string?)e.Attribute(x + "Key") == key &&
                (string?)e.Attribute("BasedOn") == "{StaticResource EstimateCell}");
    }
}
