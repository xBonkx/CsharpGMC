using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateGridGeometrySourceTests
{
    [Fact]
    public void EstimateUsesPythonPaddingAndKeepsVariableRowHeight()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var view = XDocument.Load(Path.Combine(app, "EstimateView.xaml"));
        XElement Style(string key) => Assert.Single(view.Descendants(p + "Style")
            .Where(e => (string?)e.Attribute(xaml + "Key") == key));
        string? Value(XElement element, string property) => (string?)Assert.Single(element.Elements(p + "Setter")
            .Where(e => (string?)e.Attribute("Property") == property)).Attribute("Value");
        var grid = Assert.Single(view.Descendants(p + "DataGrid")
            .Where(e => (string?)e.Attribute(xaml + "Name") == "EstimateGrid"));
        Assert.Equal("True", (string?)grid.Attribute(XName.Get("AdaptiveGridRowHeight.Enabled", "clr-namespace:GmcSmeta.App")));
        Assert.Null(grid.Attribute("RowHeight"));
        Assert.Null(grid.Attribute("MinRowHeight"));
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.Equal("Recycling", (string?)grid.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.Equal("White", (string?)grid.Attribute("Background"));
        Assert.Equal("#E4E8EF", (string?)grid.Attribute("HorizontalGridLinesBrush"));
        Assert.Equal("#E4E8EF", (string?)grid.Attribute("VerticalGridLinesBrush"));
        Assert.Equal("4,4", Value(Style("EstimateCell"), "Padding"));
        var header = Assert.Single(view.Descendants(p + "Style")
            .Where(e => (string?)e.Attribute("TargetType") == "DataGridColumnHeader"));
        Assert.Equal("6,8", Value(header, "Padding"));
        var row = Style("EstimateRow");
        Assert.Equal("{Binding MinRowHeight, RelativeSource={RelativeSource AncestorType=DataGrid}}", Value(row, "MinHeight"));
        Assert.DoesNotContain(row.Descendants(p + "Setter"), e => (string?)e.Attribute("Property") == "Height");
        Assert.Equal("White", Value(row, "Background"));
        var system = Assert.Single(row.Descendants(p + "DataTrigger")
            .Where(e => (string?)e.Attribute("Binding") == "{Binding IsSystem}"));
        Assert.Equal("#F1F4F9", Value(system, "Background"));
        Assert.Equal("{StaticResource GridSelectionTextBrush}", Value(system, "Foreground"));
        var subsystem = Assert.Single(row.Descendants(p + "MultiDataTrigger"));
        Assert.Equal("#E8F5FC", Value(subsystem, "Background"));
        Assert.Equal("#246B91", Value(subsystem, "Foreground"));
        Assert.Equal("Wrap", Value(Style("DocumentNameDisplay"), "TextWrapping"));
    }
}
