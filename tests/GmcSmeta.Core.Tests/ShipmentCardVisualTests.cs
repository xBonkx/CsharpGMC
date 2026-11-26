using System.Xml.Linq;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ShipmentCardVisualTests
{
    [Theory]
    [InlineData(0, "0 позиций")]
    [InlineData(1, "1 позиция")]
    [InlineData(2, "2 позиции")]
    [InlineData(5, "5 позиций")]
    [InlineData(11, "11 позиций")]
    [InlineData(12, "12 позиций")]
    [InlineData(14, "14 позиций")]
    [InlineData(21, "21 позиция")]
    [InlineData(24, "24 позиции")]
    [InlineData(111, "111 позиций")]
    public void PositionCountMatchesPython(int count, string expected) =>
        Assert.Equal(expected, ShipmentCardText.PositionCount(count));

    [Fact]
    public void CardPreservesListSelectionAndUsesConfirmedInfo()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ShipmentsPane.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var view = XDocument.Load(Path.Combine(app, "ShipmentsPane.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var list = Assert.Single(view.Descendants(p + "ListBox")
            .Where(e => (string?)e.Attribute(x + "Name") == "ShipmentCombo"));
        Assert.Equal("Shipment_Changed", (string?)list.Attribute("SelectionChanged"));
        Assert.Equal("Recycling", (string?)list.Attribute("VirtualizingPanel.VirtualizationMode"));
        foreach (var binding in new[] { "Title", "RecipientCaption", "StateCaption", "LineCountCaption" })
            Assert.Contains(list.Descendants(p + "TextBlock"), e => (string?)e.Attribute("Text") == "{Binding " + binding + "}");
        Assert.Contains(list.Descendants(p + "DataTrigger"), e => (string?)e.Attribute("Binding") == "{Binding Info.Fixed}");
        var code = File.ReadAllText(Path.Combine(app, "ShipmentsPane.xaml.cs"));
        Assert.Contains("ShipmentCombo.Items[cardIndex] = new ShipmentItem(current.Id, changes.Shipment);", code);
        Assert.Contains("Info.Fixed ? \"✓ Выдано\" : \"Готовится\"", code);
        Assert.Contains("кому — не указано", code);
    }
}
