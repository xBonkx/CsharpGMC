using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class PriceLikeMenuSourceTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Fact]
    public void PriceMenuUsesWholeSnapshotAndExistingGuardedWriter()
    {
        var source = Source("EstimateView.TemporaryView.cs");
        Assert.Contains("PriceLikeBatchPlan.Preview(snapshot.PositionsInOrder, selected.Id)", source);
        Assert.Contains("preview.ClientCount > 0", source);
        Assert.Contains("preview.PurchaseCount > 0", source);
        Assert.Contains("revision != _serverRevision", source);
        Assert.Contains("SavePriceLikeAsync(selected.Id, \"price\")", source);
        Assert.Contains("SavePriceLikeAsync(selected.Id, \"purchase_price\")", source);
        Assert.DoesNotContain("PriceLike_Click", source);
        Assert.DoesNotContain("SqlServerPriceLike.SamePriceCount", source);
        Assert.Contains("SqlServerPriceLike.SetPriceLike", Source("EstimateView.Cells.cs"));
    }

    [Theory]
    [InlineData("GlyphIndent", "M4 6h16M10 12h10M10 18h10 M4 10l3 2-3 2")]
    [InlineData("GlyphOutdent", "M4 6h16M10 12h10M10 18h10 M7 10l-3 2 3 2")]
    [InlineData("GlyphUndo", "M9 14 4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 0 11H11")]
    public void ExactPythonGeometry(string key, string figures)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var icon = Assert.Single(XDocument.Parse(Source("Theme/ToolbarIcons.xaml")).Descendants()
            .Where(e => (string?)e.Attribute(x + "Key") == key));
        Assert.Equal(figures, (string?)icon.Attribute("Figures"));
    }

    [Fact]
    public void NavigationKeepsExportVariantsAndGlobalCommandsInMainMenu()
    {
        var main = Source("MainWindow.xaml.cs");
        var start = main.IndexOf("private void NavigationTree_ContextMenuOpening", StringComparison.Ordinal);
        var end = main.IndexOf("private TreeViewItem? NavigationNodeUnderMouse", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var menu = main[start..end];
        Assert.DoesNotContain("OpenHistory(null)", menu);
        Assert.DoesNotContain("OpenTrash()", menu);
        Assert.Contains("exportKp.Items.Add(exportEstimate)", menu);
        Assert.Equal(8, menu.Split("            AddKpExport(").Length - 1);
        Assert.Contains("OpenHistory(doc.Id)", menu);
        Assert.Contains("AddDocumentFileMenu(menu, doc)", menu);
        var global = Source("MainWindow.Navigation.cs");
        Assert.Contains("OpenHistory(null)", global);
        Assert.Contains("OpenTrash", global);
    }
}
