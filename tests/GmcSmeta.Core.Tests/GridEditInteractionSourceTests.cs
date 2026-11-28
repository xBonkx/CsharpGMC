namespace GmcSmeta.Core.Tests;

public sealed class GridEditInteractionSourceTests
{
    private static string Source(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", name)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", name));
    }

    [Fact]
    public void SharedDoubleClickUsesNativeEditAndPreservesGuards()
    {
        Assert.Contains("GridEditInteraction.Install();", Source("App.xaml.cs"));
        var code = Source("GridEditInteraction.cs");
        Assert.Contains("RegisterClassHandler(typeof(DataGrid), Mouse.PreviewMouseDownEvent", code);
        Assert.Contains("e.ClickCount != 2", code);
        Assert.Contains("Keyboard.Modifiers != ModifierKeys.None", code);
        Assert.Contains("grid.IsReadOnly", code);
        Assert.Contains("cell.IsEditing || cell.IsReadOnly", code);
        Assert.Contains("ButtonBase or TextBoxBase or Selector or Thumb", code);
        Assert.Contains("grid.BeginEdit(e)", code);
        Assert.Contains("ReferenceEquals(row.Item, grid.CurrentCell.Item)", code);
    }

    [Theory]
    [InlineData("EstimateView.Materials.cs")]
    [InlineData("EstimateView.CostSummary.cs")]
    [InlineData("EstimateView.CostTotals.cs")]
    public void FinancialEditorsUseSingleGuardedFocusCallback(string file)
    {
        var code = Source(file);
        Assert.Contains("GridEditInteraction.FocusTextEditor(grid, e);", code);
        Assert.DoesNotContain("editor.Loaded += onLoaded", code);
        var shared = Source("GridEditInteraction.cs");
        Assert.Contains("DispatcherPriority.Loaded", shared);
        Assert.Contains("!grid.IsKeyboardFocusWithin", shared);
        Assert.Contains("ReferenceEquals(grid.CurrentCell.Item, item)", shared);
        Assert.Contains("IsEditing: true", shared);
        Assert.Equal(1, shared.Split("editor.SelectAll();", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void EstimateFocusesTemplateEditorAndRoutesPasteBeforeStructuralImport()
    {
        var cells = Source("EstimateView.Cells.cs");
        Assert.Contains("GridEditInteraction.FocusTextEditor(EstimateGrid, e);", cells);
        var shared = Source("GridEditInteraction.cs");
        Assert.Contains("presenter.ApplyTemplate();", shared);
        Assert.Contains("if (FocusCurrentEditor()) return;", shared);
        Assert.Contains("ApplicationCommands.Paste.Execute(null, editor);", shared);
        Assert.Contains("e.EditingEventArgs is not TextCompositionEventArgs", shared);
        var shortcuts = Source("EstimateView.Undo.cs");
        var cellPaste = shortcuts.IndexOf("GridEditInteraction.TryPasteInEditingCell(EstimateGrid)", StringComparison.Ordinal);
        var rowPaste = shortcuts.IndexOf("PasteRows_Click(sender, e)", StringComparison.Ordinal);
        Assert.True(cellPaste >= 0 && rowPaste > cellPaste);
    }

    [Fact]
    public void UnchangedFinancialEditorsDoNotWriteAndCacheDoesNotResetMaterialEdit()
    {
        var materials = Source("EstimateView.Materials.cs");
        Assert.Contains("if (text == (column switch", materials);
        Assert.Contains("=> row.Supplier", materials);
        // e4f7b4d5: проверка редактирования вынесена в общий хелпер HasGridEdit.
        Assert.Contains("if (!force && (HasGridEdit(MaterialsGrid) || _materialsBusy)) return;", materials);
        Assert.Contains("is System.ComponentModel.IEditableCollectionView { IsEditingItem: true }",
            Source("EstimateView.xaml.cs"));
        Assert.Contains("MaterialsVisibleRows.SameReferences(existing, result)", materials);
        Assert.Contains("if (text == (column switch", Source("EstimateView.CostSummary.cs"));
        Assert.Contains("if (editor.Text == (column == \"cost\" ? row.CostRaw : row.SellRaw)) return;", Source("EstimateView.CostTotals.cs"));
    }
}
