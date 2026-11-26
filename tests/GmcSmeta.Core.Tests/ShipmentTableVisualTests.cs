using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ShipmentTableVisualTests
{
    [Fact]
    public void TablePreservesActionsAndMatchesPythonQuantityPresentation()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ShipmentsPane.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var document = XDocument.Load(Path.Combine(app, "ShipmentsPane.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => Assert.Single(document.Descendants()
            .Where(e => (string?)e.Attribute(x + "Name") == name));

        var grid = Named("LinesGrid");
        Assert.Equal("0", (string?)grid.Attribute("AlternationCount"));
        var columns = grid.Descendants(p + "DataGridTextColumn")
            .ToDictionary(e => (string)e.Attribute("Header")!);
        foreach (var name in new[] { "По смете", "Выдано раньше", "Есть на складе" })
            Assert.Equal("{StaticResource CompactCostMoney}", (string?)columns[name].Attribute("ElementStyle"));
        Assert.Equal("{StaticResource ShipmentLeftText}", (string?)columns["Останется выдать"].Attribute("ElementStyle"));
        var now = columns["Выдать сейчас ✎"];
        Assert.Equal("{StaticResource ShipmentNowCell}", (string?)now.Attribute("CellStyle"));
        Assert.Equal("{StaticResource ShipmentNowText}", (string?)now.Attribute("ElementStyle"));
        Assert.Equal("{StaticResource CompactCostMoneyEditor}", (string?)now.Attribute("EditingElementStyle"));
        Assert.Equal("{StaticResource ShipmentNowHeader}", (string?)now.Attribute("HeaderStyle"));
        Assert.Equal("LinesGrid_CellEditEnding", (string?)grid.Attribute("CellEditEnding"));
        Assert.Equal("MarkRow_Click", (string?)grid.Descendants(p + "CheckBox").Single().Attribute("Click"));

        XElement Style(string key) => Assert.Single(document.Descendants(p + "Style")
            .Where(e => (string?)e.Attribute(x + "Key") == key));
        var nowCell = Style("ShipmentNowCell").ToString();
        Assert.Contains("#EEF4FF", nowCell);
        Assert.Contains("#ECFDF3", nowCell);
        Assert.Contains("StockOverrun", nowCell);
        Assert.Contains("GridSelectionTextBrush", nowCell);
        Assert.Contains("{Binding QtyHint}", nowCell);
        var cellTemplate = Assert.Single(Style("ShipmentNowCell").Descendants(p + "ControlTemplate"));
        var alert = Assert.Single(cellTemplate.Descendants(p + "Viewbox"));
        Assert.Equal("14", (string?)alert.Attribute("Width"));
        Assert.Equal("14", (string?)alert.Attribute("Height"));
        Assert.Equal("Collapsed", (string?)alert.Attribute("Visibility"));
        Assert.Equal("False", (string?)alert.Attribute("IsHitTestVisible"));
        var alertPath = Assert.Single(alert.Descendants(p + "Path"));
        Assert.Equal("{StaticResource GlyphAlert}", (string?)alertPath.Attribute("Data"));
        Assert.Equal("#B45309", (string?)alertPath.Attribute("Stroke"));
        Assert.Single(cellTemplate.Descendants(p + "ContentPresenter"));
        var triggers = cellTemplate.Descendants(p + "Trigger").ToArray();
        Assert.Contains(triggers, e => (string?)e.Attribute("Property") == "IsEditing" &&
            e.Elements(p + "Setter").Any(s => (string?)s.Attribute("TargetName") == "StockAlert" &&
                (string?)s.Attribute("Value") == "Collapsed"));
        Assert.Contains(triggers, e => (string?)e.Attribute("Property") == "IsKeyboardFocusWithin" &&
            e.Elements(p + "Setter").Any(s => (string?)s.Attribute("TargetName") == "CurrentCellFrame"));
        var stockTrigger = Assert.Single(cellTemplate.Descendants(p + "DataTrigger"));
        Assert.Equal("{Binding StockOverrun}", (string?)stockTrigger.Attribute("Binding"));
        Assert.Contains(stockTrigger.Elements(p + "Setter"), s => (string?)s.Attribute("TargetName") == "StockAlert" &&
            (string?)s.Attribute("Value") == "Visible");
        Assert.Contains("{Binding QtyHint}", Style("ShipmentNowText").ToString());
        var leftStyle = Style("ShipmentLeftText").ToString();
        Assert.Contains("всё", leftStyle);
        Assert.Contains("#94A3B8", leftStyle);
        Assert.Contains("GridSelectionTextBrush", leftStyle);

        var group = Assert.Single(grid.Descendants(p + "Expander"));
        Assert.Equal("SystemGroup_Loaded", (string?)group.Attribute("Loaded"));
        Assert.Equal("SystemGroup_DataContextChanged", (string?)group.Attribute("DataContextChanged"));
        Assert.Equal("SystemGroup_ExpansionChanged", (string?)group.Attribute("Expanded"));
        Assert.Equal("SystemGroup_ExpansionChanged", (string?)group.Attribute("Collapsed"));
        var groupHeader = Assert.Single(group.Descendants(p + "DockPanel"));
        var groupStyle = Assert.Single(groupHeader.Descendants(p + "Style"));
        Assert.Contains(groupStyle.Elements(p + "Setter"), e =>
            (string?)e.Attribute("Property") == "Background" && (string?)e.Attribute("Value") == "#F1F4F9");
        var subTrigger = Assert.Single(groupStyle.Descendants(p + "DataTrigger"));
        Assert.Equal("{Binding Name.IsSubsystem}", (string?)subTrigger.Attribute("Binding"));
        Assert.Equal("True", (string?)subTrigger.Attribute("Value"));
        Assert.Contains(subTrigger.Elements(p + "Setter"), e =>
            (string?)e.Attribute("Property") == "Background" && (string?)e.Attribute("Value") == "#E8F5FC");
        Assert.Equal("27", (string?)groupHeader.Attribute("MinHeight"));
        Assert.Equal("Bold", (string?)groupHeader.Descendants(p + "TextBlock").Single().Attribute("FontWeight"));
        Assert.Equal("SelectSystem_Click", (string?)groupHeader.Descendants(p + "Button").Single().Attribute("Click"));
        Assert.NotNull(Named("StockWarningText"));
        Assert.NotNull(Named("ShipmentHelpText"));

        var code = File.ReadAllText(Path.Combine(app, "ShipmentsPane.xaml.cs"));
        Assert.Contains("IsSubsystem = row.IsSubsystem", code);
        Assert.Contains("old.IsSubsystem != candidate.IsSubsystem", code);
        Assert.Contains("SystemLabel) { IsSubsystem = IsSubsystem }", code);
        var sql = File.ReadAllText(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerShipments.cs"));
        Assert.Contains("AS issued, p.parent_id, sy.parent_id", sql);
        Assert.Contains("outOfKp, !r.IsDBNull(11)) { IsSubsystem = !r.IsDBNull(12) }", sql);
        Assert.Contains("ShipmentCardText.PositionCount(lineCount)", code);
        Assert.Contains("ShipmentCardText.PositionCount(over)", code);
        Assert.Contains("ShipmentCardText.PositionCount(planned - done)", code);
        Assert.Contains("StockWarningText.Visibility = over == 0 ?", code);
        Assert.Contains("TotalTile.ToolTip =", code);
        // The transient status line must not retain a second, stale stock warning.
        // Both full load and incremental readback update the dedicated warning.
        Assert.Contains("private void UpdateSummary(int lineCount)", code);
        Assert.Contains("UpdateSummary(snapshot.LineCount);", code);
        Assert.Contains("UpdateSummary(changes.Shipment.Lines);", code);
        Assert.DoesNotContain("превышают складской остаток", code);
        Assert.Contains("StockWarningText.Text = over == 0 ?", code);
        Assert.Contains("StockWarningText.Text = \"\";", code);
    }
}
