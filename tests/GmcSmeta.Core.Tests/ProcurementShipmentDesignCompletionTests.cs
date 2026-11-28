using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementShipmentDesignCompletionTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument View(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file))) root = root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    private static XElement Named(XDocument view, string name) =>
        Assert.Single(view.Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));

    [Theory]
    [InlineData("ProcurementPane.xaml", "ProcurementSearchBox")]
    [InlineData("ShipmentsPane.xaml", "RowSearch")]
    public void SearchHasAccessibleNameAndSharedSmallIcon(string file, string name)
    {
        var search = Named(View(file), name);
        Assert.False(string.IsNullOrWhiteSpace((string?)search.Attribute("AutomationProperties.Name")));
        Assert.Contains(search.Parent!.Descendants(W + "Viewbox"), e =>
            (string?)e.Attribute("Style") == "{StaticResource LayoutSmallSearchIcon}");
    }

    [Theory]
    [InlineData("ProcurementPane.xaml", "BuyGrid")]
    [InlineData("ShipmentsPane.xaml", "LinesGrid")]
    [InlineData("ProcurementPane.xaml", "MovementHistoryGrid")]
    public void RowsAreNotManuallyResizedAndRemainVirtualized(string file, string name)
    {
        var grid = Named(View(file), name);
        Assert.Equal("False", (string?)grid.Attribute("CanUserResizeRows"));
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.Equal("Recycling", (string?)grid.Attribute("VirtualizingPanel.VirtualizationMode"));
    }

    [Fact]
    public void NoDateDoesNotReserveBadgeSpace()
    {
        var view = View("ProcurementPane.xaml");
        var trigger = Assert.Single(view.Descendants(W + "DataTrigger").Where(e =>
            (string?)e.Attribute("Binding") == "{Binding ExpectedCaption}" &&
            (string?)e.Attribute("Value") == ""));
        Assert.Contains(trigger.Elements(W + "Setter"), e => (string?)e.Attribute("Property") == "Visibility" &&
            (string?)e.Attribute("Value") == "Collapsed");
    }

    [Fact]
    public void MovementHistoryNoteRemainsReadable()
    {
        var history = Named(View("ProcurementPane.xaml"), "MovementHistoryGrid");
        var note = Assert.Single(history.Descendants(W + "DataGridTextColumn").Where(e =>
            (string?)e.Attribute("Header") == "Примечание"));
        Assert.Equal("130", (string?)note.Attribute("MinWidth"));
    }

    [Fact]
    public void ShipmentStepSeparatorsUseSharedVectors()
    {
        var steps = Named(View("ShipmentsPane.xaml"), "ShipmentStepsBar");
        Assert.Equal(4, steps.Descendants(W + "ContentControl").Count());
        Assert.Equal(3, steps.Descendants(W + "Path").Count(e =>
            (string?)e.Attribute("Data") == "{StaticResource GlyphArrowForward}"));
    }

    [Fact]
    public void ShipmentCardsWrapLongTitles()
    {
        var list = Named(View("ShipmentsPane.xaml"), "ShipmentCombo");
        var title = Assert.Single(list.Descendants(W + "TextBlock").Where(e =>
            (string?)e.Attribute("Text") == "{Binding Title}"));
        Assert.Equal("Wrap", (string?)title.Attribute("TextWrapping"));
        Assert.Equal("{Binding Title}", (string?)title.Attribute("ToolTip"));
    }

    [Fact]
    public void ShipmentFooterConsumesNoSpaceWhenEmpty()
    {
        var footer = Named(View("ShipmentsPane.xaml"), "FooterText");
        Assert.Null(footer.Parent!.Attribute("Margin"));
        Assert.Contains(footer.Descendants(W + "Trigger"), e =>
            (string?)e.Attribute("Property") == "Text" && (string?)e.Attribute("Value") == "" &&
            e.Elements(W + "Setter").Any(s => (string?)s.Attribute("Property") == "Visibility" &&
                (string?)s.Attribute("Value") == "Collapsed"));
    }
}
