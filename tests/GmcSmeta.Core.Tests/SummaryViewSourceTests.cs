using System.Xml.Linq;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>Source-level guard for SUM-VIS-01. Does not replace WPF runtime acceptance.</summary>
public sealed class SummaryViewSourceTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Local = "clr-namespace:GmcSmeta.App";

    private static XElement Panel()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "EstimateView.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "EstimateView.xaml"))
            .Descendants(Wpf + "Border").Single(e => (string?)e.Attribute(Xaml + "Name") == "SummaryPanel");
    }

    [Fact]
    public void SummaryColumnsHaveStableKeysAndDefaultVisibility()
    {
        var grid = Panel().Descendants(Wpf + "DataGrid").Single();
        var columns = grid.Element(Wpf + "DataGrid.Columns")!.Elements().ToArray();
        var keys = columns.Select(c => (string)c.Attribute("SortMemberPath")!).ToArray();
        Assert.Equal(19, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Equal("name", keys[0]);
        Assert.Equal("False", (string?)columns[0].Attribute("CanUserReorder"));
        Assert.True(Array.IndexOf(keys, "markup") < Array.IndexOf(keys, "t_cost"));
        var hidden = columns.Where(c => (string?)c.Attribute(Local + "TableColumnMenu.DefaultHidden") == "True").ToArray();
        Assert.Equal(new[] { "m_legacy_margin", "w_legacy_margin", "t_legacy_margin" },
            hidden.Select(c => (string)c.Attribute("SortMemberPath")!).ToArray());
        Assert.All(hidden, c => {
            Assert.Equal("Collapsed", (string?)c.Attribute("Visibility"));
            Assert.Equal("True", (string?)c.Attribute("IsReadOnly"));
        });
        Assert.All(keys, key => Assert.True(Enum.IsDefined(SummaryHeaderLayout.GroupFor(key))));
        Assert.DoesNotContain("supplier", keys);
        Assert.Equal("estimate-summary", (string?)grid.Attribute(Local + "TableColumnMenu.Key"));
        Assert.Equal("True", (string?)grid.Attribute("CanUserReorderColumns"));
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
        Assert.Equal("True", (string?)grid.Attribute("EnableColumnVirtualization"));
        Assert.Equal("Pixel", (string?)grid.Attribute("VirtualizingPanel.ScrollUnit"));
    }

    [Fact]
    public void MaterialAndWorkRetailUseDifferentBindings()
    {
        var columns = Panel().Descendants(Wpf + "DataGrid.Columns").Single().Elements()
            .ToDictionary(c => (string)c.Attribute("SortMemberPath")!);
        void HasBinding(string key, string property)
        {
            var attributes = columns[key].DescendantsAndSelf().Attributes();
            Assert.Contains(attributes, a => a.Value == "{Binding " + property + "}");
        }
        HasBinding("m_uprice", "PriceUnitText");
        HasBinding("m_msell", "MSellText");
        HasBinding("w_usell", "WorkSellText");
        HasBinding("w_wsell", "WSellText");
        HasBinding("t_sell", "TotalSellText");
    }

    [Fact]
    public void HeaderToolbarAndFiveTilesRemainOnTheSamePanel()
    {
        var panel = Panel();
        var frame = panel.Descendants(Local + "SummaryTableFrame").Single();
        var header = frame.Descendants(Local + "SummaryHeaderBands").Single();
        Assert.Single(frame.Descendants(Wpf + "DataGrid"));
        Assert.Equal("{Binding ElementName=SummaryGrid}", (string?)header.Attribute("SourceGrid"));
        Assert.Equal("False", (string?)header.Attribute("IsHitTestVisible"));
        var toolbar = panel.Descendants(Local + "SummaryToolbarPanel").Single();
        Assert.Equal(new[] { "SummaryActions", "SummaryFilters" },
            toolbar.Elements().Select(e => (string?)e.Attribute(Xaml + "Name")).ToArray());
        foreach (var name in new[] { "SummaryMissingChrome", "SummaryMissingToggle",
                     "SummaryMissingArrow", "SummarySystemFilter", "SummarySearch" })
            Assert.Single(toolbar.Descendants().Where(e => (string?)e.Attribute(Xaml + "Name") == name));
        var tiles = panel.Descendants(Wpf + "Border")
            .Where(e => (string?)e.Attribute("Style") == "{StaticResource SummaryTileFrame}").ToArray();
        Assert.Equal(new[] { "0", "2", "4", "6", "8" }, tiles.Select(e => (string?)e.Attribute("Grid.Column")).ToArray());
    }
}
