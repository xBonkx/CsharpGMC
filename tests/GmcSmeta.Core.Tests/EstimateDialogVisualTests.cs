using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateDialogVisualTests
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
    [InlineData("StagesDialog.xaml", "StagesGrid")]
    [InlineData("StageSystemsMoneyDialog.xaml", "SystemsGrid")]
    [InlineData("StageLinksDialog.xaml", "LinksGrid")]
    [InlineData("CompareDialog.xaml", "CompareGrid")]
    [InlineData("RateTransferDialog.xaml", "PreviewGrid")]
    [InlineData("ReplaceDialog.xaml", "PreviewGrid")]
    public void DialogRowsAreReadableAndVirtualized(string file, string name)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = XDocument.Parse(Source(file)).Descendants()
            .Single(e => e.Name.LocalName == "DataGrid" && (string?)e.Attribute(x + "Name") == name);
        // Дизайн-проход заменил литерал "27" ресурсом Theme/LayoutMetrics.xaml (=27); принимаем обе формы.
        var minRowHeight = (string?)grid.Attribute("MinRowHeight");
        Assert.True(
            minRowHeight == "27" || minRowHeight == "{StaticResource LayoutDocumentRowMinHeight}",
            $"MinRowHeight должен быть 27 или ресурсом LayoutDocumentRowMinHeight, фактически: {minRowHeight}");
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
    }

    [Fact]
    public void LayoutDocumentRowMinHeightResourceStaysTwentySeven()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resource = XDocument.Parse(Source(Path.Combine("Theme", "LayoutMetrics.xaml")))
            .Descendants()
            .Single(e => (string?)e.Attribute(x + "Key") == "LayoutDocumentRowMinHeight");
        Assert.Equal("27", (string?)resource);
    }

    [Fact]
    public void SearchClearKeepsOriginalFilterHandlers()
    {
        var stage = Source("StagesDialog.xaml");
        var rate = Source("RateTransferDialog.xaml");
        Assert.Contains("TextChanged=\"Search_Changed\"", stage);
        Assert.Contains("TextChanged=\"Search_Changed\"", rate);
        Assert.Contains("Click=\"ClearStageSearch_Click\"", stage);
        Assert.Contains("Click=\"ClearRateSearch_Click\"", rate);
        Assert.Contains("SourceSearch.Clear();", Source("EstimateDialogSearchUi.cs"));
        Assert.Contains("SearchBox.Clear();", Source("EstimateDialogSearchUi.cs"));
    }

    [Fact]
    public void AttachmentDesignRetainsMutationAndReadGuards()
    {
        var source = Source("AttachmentWindow.cs");
        Assert.Contains("WorkspaceWindowUi.StyleTable(_grid)", source);
        Assert.Contains("MinRowHeight = 27", source);
        Assert.Contains("CanWrite && _grid.SelectedItems.Count == 1", source);
        Assert.Contains("if (_loading && !_allowCloseDuringRead)", source);
        Assert.Contains("ChangeRoleAsync(row, signed, primary, _revision)", source);
    }
}
