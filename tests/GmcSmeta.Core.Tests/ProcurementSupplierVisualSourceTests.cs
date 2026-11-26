using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementSupplierVisualSourceTests
{
    [Fact]
    public void WorkshopGlyphAndSelectionDoNotReplaceSupplierSaveWorkflow()
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
        var grid = Named("BuyGrid");
        var supplier = Assert.Single(grid.Descendants(p + "DataGridTemplateColumn")
            .Where(e => (string?)e.Attribute("Header") == "Поставщик"));
        var glyph = Assert.Single(supplier.Descendants(p + "Path"));
        Assert.Equal("{StaticResource GlyphSettings}", (string?)glyph.Attribute("Data"));
        Assert.Equal("#A21CAF", (string?)glyph.Attribute("Stroke"));
        Assert.Contains(supplier.Descendants(p + "DataTrigger"), e =>
            (string?)e.Attribute("Binding") == "{Binding Source}" && (string?)e.Attribute("Value") == "shop");
        Assert.Equal("{Binding Supplier, Mode=OneWay}", (string?)Assert.Single(supplier.Descendants(p + "TextBox")).Attribute("Text"));
        Assert.Equal("BuyGrid_CellEditEnding", (string?)grid.Attribute("CellEditEnding"));
        Assert.Contains(Named("ProcurementSelectionText").Descendants(p + "Setter"), e =>
            (string?)e.Attribute("Property") == "FontWeight" && (string?)e.Attribute("Value") == "SemiBold");
        Assert.Equal("Wrap", (string?)Named("FooterText").Attribute("TextWrapping"));
        var code = File.ReadAllText(Path.Combine(app, "ProcurementPane.xaml.cs"));
        Assert.Contains("\"Поставщик\" => \"supplier\"", code);
        Assert.Contains("ContentPresenter presenter => FindVisualChild<ComboBox>(presenter)", code);
        Assert.Contains("FindVisualChild<TextBox>(presenter)?.Text.Trim()", code);
        Assert.Contains("ProcurementSelectionText.Tag = count > 0 ? \"selected\" : null;", code);
    }
}
