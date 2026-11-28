using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class RemainingVisualSourceTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Theory]
    [InlineData("ProcurementExtraDialog.cs")]
    [InlineData("MaterialsMarkupDialog.cs")]
    [InlineData("SectionPickerDialog.cs")]
    public void DynamicActionFootersWrapAndStayOutsideScrollingContent(string file)
    {
        var source = Source(file);
        Assert.Contains("new WrapPanel", source);
        Assert.Contains("DynamicFormLayout.WithFooter(", source);
        Assert.Contains("LayoutDialogActionGap", source);
        Assert.Contains("AutomationProperties.SetName(", source);
    }

    [Theory]
    [InlineData("EstimateTotalsPanel.xaml")]
    [InlineData("CostSummaryPane.xaml")]
    public void TotalsSupportsExtendedCellCopyWithoutRowSelection(string file)
    {
        var grid = Assert.Single(XDocument.Parse(Source(file)).Descendants(W + "DataGrid")
            .Where(e => (string?)e.Attribute(X + "Name") == "TotalsGrid"));
        Assert.Equal("Extended", (string?)grid.Attribute("SelectionMode"));
        Assert.Equal("Cell", (string?)grid.Attribute("SelectionUnit"));
        Assert.Equal("ExcludeHeader", (string?)grid.Attribute("ClipboardCopyMode"));
        Assert.Null(grid.Attribute("ColumnHeaderHeight"));
        Assert.Equal("0", (string?)grid.Attribute("BorderThickness"));
        Assert.Equal("SummaryTableFrame", grid.Parent!.Name.LocalName);
    }

    [Fact]
    public void StoredSmrModeRestorationSuppressesWritesAndClearsUnknownChoice()
    {
        var source = Source("EstimateView.Header.cs");
        Assert.Contains("_smrComboSilent = true;", source);
        Assert.Contains("finally { _smrComboSilent = false; }", source);
        Assert.Contains(".FirstOrDefault(item => (string?)item.Tag == mode)", source);
        Assert.Contains("if (_smrComboSilent || _estimate is not { } snapshot) return;", source);
        Assert.Contains("if (mode == snapshot.SmrMode) return;", source);
        Assert.Contains("est.Locked || est.MoneyFromStages || (!pct && mode != \"sum\")", source);
    }

    [Fact]
    public void MainHeadingRolesShareNativeWrappingAndQtWeight()
    {
        var doc = XDocument.Parse(Source("MainWindow.xaml"));
        foreach (var name in new[] { "ObjectTitle", "EmptyNavigationHeading" })
        {
            var title = Assert.Single(doc.Descendants(W + "TextBlock").Where(e => (string?)e.Attribute(X + "Name") == name));
            Assert.Equal("{StaticResource UiPageHeading}", (string?)title.Attribute("Style"));
            Assert.Equal("{StaticResource UiH1Size}", (string?)title.Attribute("FontSize"));
            Assert.Null(title.Attribute("Height"));
        }
        var segment = Assert.Single(XDocument.Parse(Source("App.xaml")).Descendants(W + "Style")
            .Where(e => (string?)e.Attribute(X + "Key") == "Segment"));
        var selected = Assert.Single(segment.Descendants(W + "Trigger").Where(e =>
            (string?)e.Attribute("Property") == "IsChecked" && (string?)e.Attribute("Value") == "True"));
        Assert.Contains(selected.Elements(W + "Setter"), s =>
            (string?)s.Attribute("Property") == "FontWeight" && (string?)s.Attribute("Value") == "SemiBold");
    }

    [Fact]
    public void SharedIconCaptionsUseVerticalCenterNotNegativeMargins()
    {
        var templates = XDocument.Parse(Source("Theme/ToolbarIcons.xaml")).Descendants(W + "DataTemplate")
            .Where(e => ((string?)e.Attribute(X + "Key"))?.StartsWith("IconLabel", StringComparison.Ordinal) == true).ToArray();
        Assert.True(templates.Length >= 30);
        foreach (var template in templates)
        {
            var glyph = Assert.Single(template.Descendants(W + "Viewbox"));
            var caption = Assert.Single(template.Descendants(W + "TextBlock"));
            Assert.Equal("Center", (string?)glyph.Attribute("VerticalAlignment"));
            Assert.Equal("Center", (string?)caption.Attribute("VerticalAlignment"));
            Assert.DoesNotContain("-", (string?)caption.Attribute("Margin") ?? "");
        }
    }

    [Fact]
    public void PtoLabelColumnsCanShrinkInsteadOfClippingFields()
    {
        var source = Source("PtoFormUi.cs");
        Assert.Contains("MaxWidth=labelWidth", source);
        Assert.Contains("new GridLength(2,GridUnitType.Star)", source);
        Assert.Contains("LayoutFormColumnGap", source);
        Assert.Contains("LayoutFormFieldMargin", source);
    }
}
