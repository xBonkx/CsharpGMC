using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementGridVisualSourceTests
{
    [Fact]
    public void ProcurementGridUsesLocalGeometryAndPreservesVirtualization()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ProcurementPane.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var view = XDocument.Load(Path.Combine(app, "ProcurementPane.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => Assert.Single(view.Descendants()
            .Where(e => (string?)e.Attribute(x + "Name") == name));
        string? Setter(string key, string property) => (string?)Assert.Single(
            Assert.Single(view.Descendants(p + "Style").Where(e => (string?)e.Attribute(x + "Key") == key))
                .Elements(p + "Setter").Where(e => (string?)e.Attribute("Property") == property)).Attribute("Value");
        var grid = Named("BuyGrid");
        Assert.Equal("27", (string?)grid.Attribute("MinRowHeight"));
        Assert.Null(grid.Attribute("RowHeight"));
        Assert.Null(grid.Attribute("AlternationCount"));
        Assert.Equal("4,4", Setter("ProcurementCell", "Padding"));
        Assert.Equal("6,8", Setter("ProcurementHeader", "Padding"));
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.Equal("True", (string?)grid.Attribute("VirtualizingPanel.IsVirtualizingWhenGrouping"));
        Assert.Equal("Recycling", (string?)grid.Attribute("VirtualizingPanel.VirtualizationMode"));
        Assert.Contains(grid.Descendants(p + "Path"), e => (string?)e.Attribute("Data") == "{StaticResource GlyphAlert}");
        Assert.Contains(grid.Descendants(p + "Grid"), e => ((string?)e.Attribute("Background") ?? "").Contains("Items[0].GroupBackground"));
        Assert.Contains(grid.Descendants(p + "DataTrigger"), e => ((string?)e.Attribute("Binding") ?? "").Contains("NameWrapPreference.Current"));
        Assert.Equal("{StaticResource CompactCostHeader}", (string?)Named("MovementHistoryGrid").Attribute("ColumnHeaderStyle"));
        var source = File.ReadAllText(Path.Combine(app, "ProcurementPane.xaml.cs"));
        Assert.Contains("GroupIdentity.StartsWith(\"system:\", StringComparison.Ordinal)", source);
        Assert.Contains("? \"#E8F5FC\" : \"#F1F4F9\"", source);
    }
}
