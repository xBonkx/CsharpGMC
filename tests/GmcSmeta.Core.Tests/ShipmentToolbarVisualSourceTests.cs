using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ShipmentToolbarVisualSourceTests
{
    [Fact]
    public void SearchAndActionIconsPreserveShipmentWorkflow()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ShipmentsPane.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var view = XDocument.Load(Path.Combine(app, "ShipmentsPane.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => Assert.Single(view.Descendants()
            .Where(e => (string?)e.Attribute(x + "Name") == name));
        var search = Named("RowSearch");
        Assert.Equal("{StaticResource PythonSearch}", (string?)search.Attribute("Style"));
        Assert.Equal("190", (string?)search.Parent!.Attribute("Width"));
        Assert.Equal("32", (string?)search.Attribute("Height"));
        Assert.Equal("RowSearch_Changed", (string?)search.Attribute("TextChanged"));
        var clear = Assert.Single(search.Parent!.Descendants(p + "Button"));
        Assert.Equal("ClearShipmentSearch_Click", (string?)clear.Attribute("Click"));
        Assert.Equal("{StaticResource IconOnlyX}", (string?)clear.Attribute("ContentTemplate"));
        Assert.Contains(clear.Descendants(p + "DataTrigger"), e => (string?)e.Attribute("Value") == "");
        foreach (var (name, glyph, handler) in new[]
        {
            ("EditFixedButton", "IconLabelEdit", "EditFixed_Click"),
            ("FinishFixedButton", "IconLabelCheck", "FinishFixed_Click"),
            ("RemoveRowsButton", "IconLabelX", "RemoveRows_Click"),
            ("StockRowsButton", "IconLabelCheck", "StockRows_Click"),
            ("AddRowsButton", "IconLabelUpload", "AddRows_Click")
        })
        {
            Assert.Equal("{StaticResource " + glyph + "}", (string?)Named(name).Attribute("ContentTemplate"));
            Assert.Equal(handler, (string?)Named(name).Attribute("Click"));
        }
        var code = File.ReadAllText(Path.Combine(app, "ShipmentsPane.xaml.cs"));
        Assert.Contains("RowSearch.Clear();", code);
        Assert.Contains("RowSearch.Focus();", code);
        Assert.Contains("var filter = EffectiveShipmentFilter;", code);
    }
}
