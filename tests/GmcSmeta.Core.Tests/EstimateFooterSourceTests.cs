using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateFooterSourceTests
{
    [Fact]
    public void FooterButtonsUsePythonGeometryAndKeepExistingCommands()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var view = XDocument.Load(Path.Combine(app, "EstimateView.xaml"));
        XElement Named(string name) => Assert.Single(view.Descendants().Where(e => (string?)e.Attribute(xaml + "Name") == name));

        var add = Named("BottomAddRowButton");
        Assert.Equal("Bottom", (string?)add.Attribute("DockPanel.Dock"));
        Assert.Equal("Stretch", (string?)add.Attribute("HorizontalAlignment"));
        Assert.Equal("Left", (string?)add.Attribute("HorizontalContentAlignment"));
        Assert.Equal("32", (string?)add.Attribute("MinHeight"));
        Assert.Equal("14,0", (string?)add.Attribute("Padding"));
        Assert.Equal("AddRow_Click", (string?)add.Attribute("Click"));
        Assert.Equal("{Binding IsEnabled, ElementName=AddRowButton}", (string?)add.Attribute("IsEnabled"));
        var icon = Assert.Single(add.Descendants(p + "Viewbox"));
        Assert.Equal("20", (string?)icon.Attribute("Width"));
        Assert.Equal("20", (string?)icon.Attribute("Height"));
        Assert.Equal("{StaticResource GlyphPlus}", (string?)Assert.Single(icon.Descendants(p + "Path")).Attribute("Data"));

        var toggle = Named("ToggleTotalsButton");
        Assert.Equal("Right", (string?)toggle.Attribute("DockPanel.Dock"));
        Assert.Equal("{StaticResource IconLabelArrowDown}", (string?)toggle.Attribute("ContentTemplate"));
        Assert.Equal("ToggleTotals_Click", (string?)toggle.Attribute("Click"));
        var code = File.ReadAllText(Path.Combine(app, "EstimateView.xaml.cs"));
        Assert.Contains("hidden ? \"Показать итоги\" : \"Скрыть итоги\"", code);
        Assert.Contains("hidden ? \"IconLabelArrowUp\" : \"IconLabelArrowDown\"", code);
        Assert.Contains("TotalsCards.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible", code);
        Assert.Contains("TotalsSummary.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed", code);
    }
}
