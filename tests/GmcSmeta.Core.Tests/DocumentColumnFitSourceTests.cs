using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class DocumentColumnFitSourceTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace L = "clr-namespace:GmcSmeta.App";

    private static string App()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "src", "GmcSmeta.App");
    }

    [Theory]
    [InlineData("ActsEditorView.xaml", 9)]
    [InlineData("ProcurementPane.xaml", 11)]
    [InlineData("ShipmentsPane.xaml", 6)]
    [InlineData("EstimateMaterialsPanel.xaml", 10)]
    [InlineData("EstimateTotalsPanel.xaml", 4)]
    [InlineData("Ks3ReportWindow.xaml", 1)]
    [InlineData("KsGroupReportWindow.xaml", 9)]
    [InlineData("AllContractActsWindow.xaml", 4)]
    [InlineData("CompareDialog.xaml", 6)]
    [InlineData("StageSystemsMoneyDialog.xaml", 1)]
    [InlineData("TrashWindow.xaml", 3)]
    [InlineData("PtoWindow.xaml", 3)]
    [InlineData("PassportsWindow.xaml", 2)]
    [InlineData("LettersWindow.xaml", 3)]
    [InlineData("HistoryWindow.xaml", 1)]
    [InlineData("MetalWarehouseWindow.xaml", 8)]
    public void OptInColumnsReferToTheirActualDisplayBinding(string file, int count)
    {
        var doc = XDocument.Load(Path.Combine(App(), file));
        var columns = doc.Descendants().Where(e => e.Attribute(L + "TableColumnMenu.FitDisplayPath") is not null).ToArray();
        Assert.Equal(count, columns.Length);
        foreach (var column in columns)
        {
            var path = (string)column.Attribute(L + "TableColumnMenu.FitDisplayPath")!;
            var grid = column.Ancestors(W + "DataGrid").First();
            Assert.NotNull(grid.Attribute(L + "TableColumnMenu.Key"));
            var width = (string?)column.Attribute("Width");
            Assert.NotNull(width);
            Assert.DoesNotContain("*", width!);
            var expected = "{Binding " + path;
            bool IsBinding(string? value) => value == expected + "}" ||
                value?.StartsWith(expected + ",", StringComparison.Ordinal) == true;
            if (column.Name == W + "DataGridTextColumn")
            {
                var binding = (string?)column.Attribute("Binding");
                Assert.True(IsBinding(binding), file + ": " + path + " does not match the text binding");
                Assert.DoesNotContain("Converter=", binding!);
            }
            else
            {
                Assert.Equal(W + "DataGridTemplateColumn", column.Name);
                var template = column.Element(W + "DataGridTemplateColumn.CellTemplate");
                Assert.NotNull(template);
                Assert.Contains(template!.Descendants(W + "TextBlock"), e => IsBinding((string?)e.Attribute("Text")));
            }
        }
    }

    [Fact]
    public void SharedFitterPreservesManualWidthsAndInvalidatesAfterEditOrSourceChange()
    {
        var app = App();
        var menu = File.ReadAllText(Path.Combine(app, "TableColumnMenu.cs"));
        Assert.Contains("!entry.Width.IsAbsolute", menu);
        Assert.Contains("_prefs.Widths.TryGetValue(entry.Id, out var manual)", menu);
        Assert.Contains("manual is >= 36 and <= 2000 ? null", menu);
        Assert.Contains("_documentFit.Suspended = true", menu);
        Assert.Contains("_documentFit.Suspended = false", menu);
        Assert.Contains("_documentFit?.Invalidate()", menu);
        var fit = File.ReadAllText(Path.Combine(app, "DocumentColumnFit.cs"));
        Assert.Contains("CollectionChangedEventManager.AddHandler(grid.Items", fit);
        Assert.Contains("DataGridEditAction.Commit", fit);
        Assert.Contains("IsEditingItem: true", fit);
        Assert.Contains("binding.Converter is not null", fit);
        Assert.Contains("ReferenceEquals(prior, source)", fit);
        Assert.DoesNotContain("ScrollIntoView", fit);
        Assert.DoesNotContain("UpdateSource(", fit);
    }
}
