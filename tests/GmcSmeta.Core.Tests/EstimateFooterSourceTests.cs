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

    [Fact]
    public void TotalsShareOneDockedRowAndCompactPaddingWhenCollapsed()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var view = XDocument.Load(Path.Combine(app, "EstimateView.xaml"));
        XElement Named(string name) => Assert.Single(view.Descendants().Where(e => (string?)e.Attribute(xaml + "Name") == name));
        var toggle = Named("ToggleTotalsButton");
        var cards = Named("TotalsCards");
        Assert.Same(cards.Parent, Named("TotalsSummary").Parent);
        Assert.Same(toggle.Parent, cards.Parent!.Parent);
        Assert.Equal("True", (string?)toggle.Parent!.Attribute("LastChildFill"));
        Assert.Equal("10,0,0,0", (string?)toggle.Attribute("Margin"));
        Assert.Equal("5", (string?)Named("TotalsFooterPanel").Attribute("Grid.Row"));
        Assert.Equal(4, cards.Elements(w + "Border").Count());
        Assert.All(cards.Elements(w + "Border"), card => Assert.Null(card.Attribute("Margin")));
        var metrics = XDocument.Load(Path.Combine(app, "Theme", "LayoutMetrics.xaml"));
        string Metric(string name) => metrics.Descendants().Single(e => (string?)e.Attribute(xaml + "Key") == name).Value;
        Assert.Equal("24,12,24,14", Metric("LayoutDocumentFooterPadding"));
        Assert.Equal("24,6,24,8", Metric("LayoutDocumentFooterCompactPadding"));
        Assert.Equal("16,11,16,11", Metric("LayoutTilePadding"));
        var shared = XDocument.Load(Path.Combine(app, "App.xaml"));
        var tile = shared.Descendants(w + "Style").Single(e => (string?)e.Attribute(xaml + "Key") == "PythonTile");
        Assert.Contains(tile.Elements(w + "Setter"), e => (string?)e.Attribute("Property") == "Padding" &&
            (string?)e.Attribute("Value") == "{StaticResource LayoutTilePadding}");
        var accent = shared.Descendants(w + "Style").Single(e => (string?)e.Attribute(xaml + "Key") == "PythonTileAccent");
        Assert.Equal(new[] { "#2563EB", "#4F46E5" }, accent.Descendants(w + "GradientStop")
            .Select(e => (string?)e.Attribute("Color")).ToArray());
        var code = File.ReadAllText(Path.Combine(app, "EstimateView.xaml.cs"));
        Assert.Contains("LayoutDocumentFooterCompactPadding", code);
        Assert.Contains("if (!hidden) LayoutTotalsCards();", code);
        Assert.Contains("_uiSettings.TotalsHidden = hidden;", code);
        Assert.Contains("ActualWidth >= 1180", code);
        var layout = File.ReadAllText(Path.Combine(app, "EstimateTotalsCardsPanel.cs"));
        Assert.Contains("internal const double Gap = 10;", layout);
        Assert.Contains("PreferredMinCardWidth = 200", layout);
        Assert.Contains("FitColumns(availableSize.Width, cards.Length)", layout);
        Assert.Contains("FitColumns(finalSize.Width, cards.Length)", layout);
        Assert.Contains("card.Visibility != Visibility.Collapsed", layout);
    }
}
