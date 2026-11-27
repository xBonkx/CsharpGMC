using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class MainWorkspacesVisualTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Theory]
    [InlineData("EstimateMaterialsPanel.xaml", "MaterialsGrid")]
    [InlineData("EstimateSummaryPanel.xaml", "SummaryGrid")]
    [InlineData("CostSummaryPane.xaml", "MaterialsGrid")]
    [InlineData("CostSummaryPane.xaml", "SmrWorkGrid")]
    [InlineData("CostSummaryPane.xaml", "SmrExpenseGrid")]
    [InlineData("CostSummaryPane.xaml", "SummaryGrid")]
    public void WorkingTablesUseReadableMinimumHeight(string file, string name)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = XDocument.Parse(Source(file)).Descendants()
            .Single(e => e.Name.LocalName == "DataGrid" && (string?)e.Attribute(x + "Name") == name);
        Assert.Equal("27", (string?)grid.Attribute("MinRowHeight"));
        var rowHeight = grid.Attribute("RowHeight");
        Assert.True(rowHeight is null || (rowHeight.Value?.Contains("Binding") ?? false));
    }

    [Fact]
    public void CostNamesFollowGlobalWrapPreference()
    {
        var source = Source("CostSummaryPane.xaml");
        Assert.Contains("NameWrapPreference.Current", source);
        Assert.Contains("CostNameDisplay", source);
        Assert.Contains("TextWrapping", source);
    }

    [Fact]
    public void ClearSearchKeepsExistingFilterWiring()
    {
        Assert.Contains("ClearMaterialsSearch_Click", Source("EstimateMaterialsPanel.xaml"));
        Assert.Contains("view.MaterialsSearch.TextChanged += MaterialsSearch_TextChanged", Source("EstimateView.MaterialsPanel.cs"));
        Assert.Contains("TextChanged=\"MaterialsSearch_Changed\"", Source("CostSummaryPane.xaml"));
        var source = Source("MainWorkspaceSearchUi.cs");
        Assert.Contains("MaterialsSearch.Clear();", source);
        Assert.Contains("MaterialSearch.Clear();", source);
    }

    [Fact]
    public void DocumentCardsWrapMoneyAndKeepCommandIdentity()
    {
        var source = Source("MainWindow.xaml");
        Assert.Contains("Margin=\"0,10,0,4\" TextWrapping=\"Wrap\"", source);
        Assert.Contains("Click=\"DocumentCard_Click\" Tag=\"{Binding Id}\"", source);
        Assert.Contains("Click=\"CardSignedPdf_Click\"", source);
    }
}
