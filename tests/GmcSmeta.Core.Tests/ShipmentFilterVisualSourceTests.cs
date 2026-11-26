using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ShipmentFilterVisualSourceTests
{
    [Fact]
    public void SegmentsReuseExistingFilterAndRespectFixedShipment()
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
        var track = Named("RowFilter");
        Assert.Equal(p + "Border", track.Name);
        Assert.Equal("330", (string?)track.Attribute("MinWidth"));
        var radios = track.Descendants(p + "RadioButton").ToArray();
        Assert.Equal(3, radios.Length);
        Assert.Equal(new[] { "Все строки", "Можно выдать", "В ведомости" },
            radios.Select(e => (string?)e.Attribute("Content")).ToArray());
        Assert.All(radios, radio =>
        {
            Assert.Equal("{StaticResource Segment}", (string?)radio.Attribute("Style"));
            Assert.Equal("ShipmentSegment_Click", (string?)radio.Attribute("Click"));
            Assert.Null(radio.Attribute("GroupName"));
            Assert.Same(radios[0].Parent, radio.Parent);
        });
        Assert.Equal("RowFilter_Changed", (string?)Named("DocumentFilter").Attribute("SelectionChanged"));
        var code = File.ReadAllText(Path.Combine(app, "ShipmentsPane.xaml.cs"));
        Assert.Contains("var filter = EffectiveShipmentFilter;", code);
        Assert.Contains("if (filter == 2 && !row.InShipment) return false;", code);
        Assert.Contains("if (filter != 2 && DocumentFilter.SelectedItem", code);
        Assert.Contains("if (filter == 1 && !row.InShipment", code);
        Assert.Contains("SyncShipmentFilter(hasShipment: false, editable: false);", code);
        Assert.Contains("SyncShipmentFilter(hasShipment: true, editable: editable);", code);
        var filters = File.ReadAllText(Path.Combine(app, "ShipmentsPane.Filters.cs"));
        Assert.Contains("Current?.Fixed == true && !IsEditingFixed ? 2 : _requestedRowFilter", filters);
        Assert.Contains("RowFilter.IsEnabled = hasShipment && editable;", filters);
        Assert.Contains("RowFilterList.IsChecked = filter == 2;", filters);
        Assert.Contains("RefreshRowFilter();", filters);
    }
}
