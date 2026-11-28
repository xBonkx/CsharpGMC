using System.Xml.Linq;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ShipmentStepPolicyTests
{
    [Theory]
    [InlineData(false, 0, "", false, 0)]
    [InlineData(false, 0, "Бригада", true, 0)]
    [InlineData(false, 1, "", false, 1)]
    [InlineData(false, 1, "  ", true, 1)]
    [InlineData(false, 1, null, false, 1)]
    [InlineData(false, 1, "Бригада", false, 2)]
    [InlineData(false, 1, "Бригада", true, 3)]
    [InlineData(true, 1, "Бригада", false, 4)]
    [InlineData(true, 0, "", false, 4)]
    public void StepsMatchPython(bool fixedShipment, int lines, string? recipient, bool exported, int expected) =>
        Assert.Equal(expected, ShipmentStepPolicy.Current(fixedShipment, lines, recipient, exported));

    [Fact]
    public void ExportSuccessAndAuthoritativeReadbackUpdateSteps()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ShipmentsPane.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var code = File.ReadAllText(Path.Combine(app, "ShipmentsPane.xaml.cs"));
        Assert.Equal(2, code.Split("if (saved) MarkShipmentExported(current.Id);").Length - 1);
        Assert.Contains("UpdateShipmentSteps(changes.Shipment);", code);
        Assert.Contains("UpdateShipmentSteps(current);", code);
        Assert.Contains("UpdateShipmentSteps(null);", code);
        var steps = File.ReadAllText(Path.Combine(app, "ShipmentsPane.Steps.cs"));
        Assert.Contains("current.Id == shipmentId && current.Id == _shipmentId", steps);
        Assert.Contains("_exportedShipments.Contains(current.Id)", steps);
        Assert.Contains("ExportExcelButton.Tag = ExportPdfButton.Tag = step == 2 ?", steps);
        Assert.Contains("ExportExcelButton.Tag = ExportPdfButton.Tag = \"\";", steps);

        var view = XDocument.Load(Path.Combine(app, "ShipmentsPane.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var (name, handler) in new[]
        {
            ("ExportExcelButton", "Export_Click"), ("ExportPdfButton", "ExportPdf_Click")
        })
        {
            var button = Assert.Single(view.Descendants(p + "Button")
                .Where(e => (string?)e.Attribute(x + "Name") == name));
            Assert.Equal("{StaticResource ShipmentExportButton}", (string?)button.Attribute("Style"));
            Assert.Equal("{StaticResource IconLabelFile}", (string?)button.Attribute("ContentTemplate"));
            Assert.Equal(handler, (string?)button.Attribute("Click"));
        }
        var style = Assert.Single(view.Descendants(p + "Style")
            .Where(e => (string?)e.Attribute(x + "Key") == "ShipmentExportButton"));
        foreach (var color in new[] { "#FFF4EB", "#F97316", "#C2410C", "#FFE2C7", "{StaticResource UiDisabledTextBrush}", "#E4E8EF" })
            Assert.Contains(color, style.ToString());
        Assert.Contains(style.Descendants(p + "Trigger"), e =>
            (string?)e.Attribute("Property") == "IsEnabled" && (string?)e.Attribute("Value") == "False");
    }
}
