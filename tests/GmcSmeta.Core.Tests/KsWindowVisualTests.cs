using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class KsWindowVisualTests
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
    [InlineData("ActsEditorView.xaml", "ActGrid")]
    [InlineData("AllContractActsWindow.xaml", "ActListGrid")]
    [InlineData("ContractActsWorkspace.xaml", "SummaryGrid")]
    [InlineData("ContractActsWorkspace.xaml", "ExtraGrid")]
    [InlineData("Ks3ReportWindow.xaml", "PreviousGrid")]
    [InlineData("KsGroupReportWindow.xaml", "ActGrid")]
    [InlineData("KsGroupReportWindow.xaml", "PreviousGrid")]
    public void TablesKeepCompactReadableRowsAndVirtualization(string file, string name)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = XDocument.Parse(Source(file)).Descendants()
            .Single(e => e.Name.LocalName == "DataGrid" && (string?)e.Attribute(x + "Name") == name);
        Assert.Equal("27", (string?)grid.Attribute("MinRowHeight"));
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
    }

    [Fact]
    public void SearchClearUsesExistingTextChangedHandlers()
    {
        var editor = Source("ActsEditorView.xaml");
        var all = Source("AllContractActsWindow.xaml");
        Assert.Contains("TextChanged=\"Search_Changed\"", editor);
        Assert.Contains("TextChanged=\"ActsSearch_Changed\"", all);
        Assert.Contains("Click=\"ClearPositionSearch_Click\"", editor);
        Assert.Contains("Click=\"ClearActsSearch_Click\"", all);
        var handlers = Source("KsSearchUi.cs");
        Assert.Contains("SearchBox.Clear();", handlers);
        Assert.Contains("ActsSearch.Clear();", handlers);
    }

    [Fact]
    public void WorkspaceSelectionAndRequisitesRemainReadable()
    {
        Assert.Contains("KsActListText", Source("ContractActsWorkspace.xaml"));
        Assert.Contains("SystemColors.HighlightTextBrushKey", Source("ContractActsWorkspace.xaml"));
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", Source("KsRequisitesWindow.xaml"));
        Assert.Contains("Click=\"Save_Click\"", Source("KsRequisitesWindow.xaml"));
    }
}
