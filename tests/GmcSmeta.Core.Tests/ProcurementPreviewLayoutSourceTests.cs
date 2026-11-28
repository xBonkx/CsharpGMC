namespace GmcSmeta.Core.Tests;

public sealed class ProcurementPreviewLayoutSourceTests
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
    [InlineData("ProcurementRequestDialog.cs", "PreviewLine", "var rows = _display.Select")]
    [InlineData("ProcurementMovementDialog.cs", "Line", "Items = _lines.Where")]
    [InlineData("ProcurementShopOrderPreview.cs", "PrintRow", "var rows = _rows.Select")]
    public void DetachedPreviewSupportsCellRangesWithoutNarrowingSavedScope(string file, string row, string scope)
    {
        var source = Source(file);
        Assert.Contains("SelectionUnit = DataGridSelectionUnit.Cell", source);
        Assert.Contains("SelectionMode = DataGridSelectionMode.Extended", source);
        Assert.Contains("ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader", source);
        Assert.Contains("PreviewGridPaste.Install<" + row + ">", source);
        Assert.Contains("CommitEdit(DataGridEditingUnit.Cell, true)", source);
        Assert.Contains("CommitEdit(DataGridEditingUnit.Row, true)", source);
        Assert.Contains(scope, source);
        Assert.DoesNotContain("_grid.SelectedItems", source);
        Assert.DoesNotContain("_grid.SelectedCells", source);
    }

    [Theory]
    [InlineData("ProcurementOrderDialog.cs")]
    [InlineData("ProcurementRequestDialog.cs")]
    [InlineData("ProcurementMovementDialog.cs")]
    [InlineData("ProcurementShopOrderPreview.cs")]
    public void PreviewActionsUseSharedAdaptiveMetrics(string file)
    {
        var source = Source(file);
        Assert.Contains("new WrapPanel", source);
        Assert.Contains("LayoutDialogActionGap", source);
        Assert.Contains("DynamicFormLayout.Field(", source);
        Assert.Contains("AutomationProperties.SetName(", source);
        Assert.Contains("ProcurementDialogChrome.Buttons(", source);
    }

    [Fact]
    public void ProcurementChromeUsesResourceMetrics()
    {
        var source = Source("ProcurementDialogChrome.cs");
        foreach (var key in new[] { "LayoutFormDialogMargin", "LayoutDialogMargin",
            "LayoutFormGroupSeparatorGap", "LayoutFormFooterGap", "LayoutFieldMinHeight" })
            Assert.Contains(key, source);
        Assert.DoesNotContain("=> new(", source);
    }

    [Fact]
    public void RangePasteChecksAllTargetsBeforeDetachedMutation()
    {
        var source = Source("PreviewGridPaste.cs");
        Assert.Contains("!editableProperties.Contains(propertyName)", source);
        Assert.Contains("column.IsReadOnly", source);
        Assert.Contains("NumberText.MaxNumber", source);
        Assert.Contains("Keyboard.FocusedElement is TextBoxBase", source);
        Assert.Contains("TryPasteInEditingCell(grid)", source);
        Assert.Contains("Distinct<T>(ReferenceEqualityComparer.Instance)", source);
        Assert.True(source.IndexOf("targets.Add(", StringComparison.Ordinal) <
            source.IndexOf("target.Property.SetValue(", StringComparison.Ordinal));
        Assert.DoesNotContain("SqlServer", source);
    }
}
