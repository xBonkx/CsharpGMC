using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class CompactDynamicFormSourceTests
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
    [InlineData("PromptDialog.cs", "DynamicFormLayout.Secondary(label", "LayoutFieldPadding")]
    [InlineData("KsCloseSectionsDialog.cs", "DynamicFormLayout.Secondary(", "LayoutDialogListItemGap")]
    [InlineData("EstimateView.Import.Structured.cs", "DynamicFormLayout.Apply(progress)", "HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled")]
    [InlineData("MainWindow.ContractTransfer.cs", "DynamicFormLayout.Secondary(\"Карточка назначения\"", "LayoutDialogActionGap")]
    [InlineData("EstimateView.CostSummary.cs", "AutomationProperties.SetName(_percent", "LayoutDialogActionGap")]
    [InlineData("EstimateView.Sections.cs", "TextWrapping = TextWrapping.Wrap, ToolTip = row.Name", "HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled")]
    [InlineData("SummarySystemPickerPopup.cs", "DynamicFormLayout.Secondary(\"\"", "new WrapPanel")]
    [InlineData("WorkspaceQueryWindows.cs", "resultText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap)", "ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled)")]
    public void DynamicSurfacesKeepTheirVisualRoles(string file, string first, string second)
    {
        var source = Source(file);
        Assert.Contains(first, source);
        Assert.Contains(second, source);
    }

    [Theory]
    [InlineData("ArchiveYearDialog.xaml", "MoveButton")]
    [InlineData("ObjectDialog.xaml", "SaveButton")]
    [InlineData("ConfirmDeleteDialog.xaml", "DeleteButton")]
    [InlineData("ContractDialog.xaml", "OkButton")]
    public void ConfirmationRemainsOutsideScrollingBody(string file, string action)
    {
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Parse(Source(file));
        var button = Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(x + "Name") == action));
        Assert.Empty(button.Ancestors(w + "ScrollViewer"));
        Assert.Contains(doc.Descendants(w + "ScrollViewer"), e =>
            (string?)e.Attribute("VerticalScrollBarVisibility") == "Auto" &&
            (string?)e.Attribute("HorizontalScrollBarVisibility") == "Disabled");
        Assert.Contains("UiSecondaryText", Source(file));
    }

    [Fact]
    public void DynamicDialogRolesAreDefinedOnce()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var metrics = XDocument.Parse(Source("Theme/LayoutMetrics.xaml"));
        foreach (var key in new[] { "LayoutDialogActionGap", "LayoutDialogIntroGap", "LayoutDialogListGap",
                     "LayoutDialogListItemGap", "LayoutDialogActionRowGap" })
            Assert.Single(metrics.Descendants().Where(e => (string?)e.Attribute(x + "Key") == key));
        var helper = Source("DynamicFormLayout.cs");
        Assert.Contains("UiSecondaryText", helper);
        Assert.Contains("LineStackingStrategy.MaxHeight", helper);
        Assert.Contains("LayoutNoInset", helper);
    }
}
