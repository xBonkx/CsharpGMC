namespace GmcSmeta.Core.Tests;

public sealed class ExcelPreviewVisualTests
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
    public void ImportPresentationRetainsStableDocumentKeysAndDuplicateGuard()
    {
        var source = Source("ExcelImportPreviewWindow.cs");
        Assert.Contains("new(\"kp\", \"КП\")", source);
        Assert.Contains("new(\"main\", \"Приложение\")", source);
        Assert.Contains("new(\"dop\", \"Допсоглашение\")", source);
        Assert.Contains("SelectedValueBinding = new Binding(nameof(PreviewRow.Kind))", source);
        Assert.Contains("ExcelPreviewUi.ReportSplitter()", source);
        Assert.Contains("ExcelPreviewUi.StatusStyle", source);
        Assert.Contains("MinRowHeight = 27", source);
        Assert.DoesNotContain("IconLabelUpload", source);
        Assert.Contains("selections.Any(c => sheets.Contains", source);
        Assert.Contains("selections.Any(c => !c.Block.Ok)", source);
    }

    [Fact]
    public void ExistingUpdateKeepsAllApprovalGuards()
    {
        var source = Source("ExcelExistingUpdatePreviewWindow.cs");
        Assert.Contains("new ExcelPreviewUi.ChangeLabelConverter()", source);
        Assert.Contains("ExcelPreviewUi.StatusStyle", source);
        Assert.Contains("TextWrapping.Wrap", source);
        Assert.Contains("new Binding(nameof(ExcelExistingDifference.SafeToApply))", source);
        Assert.Contains("var apply = selected && row.SafeForBulk;", source);
        Assert.Contains("!_hasMissingFormulaCache", source);
        Assert.Contains("WithExplicitMapping(source.Ordinal, old.Id, token)", source);
        Assert.Contains("if (_closed || _busy || _hasMissingFormulaCache", source);
        Assert.Contains("!CommitGrid()", source);
        Assert.Contains("selected.Any(x => !x.SafeToApply)", source);
        Assert.Contains("plan.FileSha256, _parsedFingerprint", source);
    }

    [Fact]
    public void PreviewPublishesOnceAndRejectsLateMappingAndCompareResults()
    {
        var source = Source("ExcelExistingUpdatePreviewWindow.cs");
        Assert.DoesNotContain("ObservableCollection<ExcelExistingDifference>", source);
        Assert.Contains("_grid.ItemsSource = plan.Differences;", source);
        Assert.Contains("await Task.Run(() => plan.MappingCandidates(source.Ordinal, token), token)", source);
        Assert.Contains("var mapped = plan.WithExplicitMapping(source.Ordinal, old.Id, token);", source);
        Assert.Contains("if (dialogClosed || _closed || token.IsCancellationRequested || !ReferenceEquals(Plan, plan)) return;", source);
        Assert.Contains("if (_closed || token.IsCancellationRequested) return;", source);
        Assert.Contains("if (!_closed) SetBusy(false);", source);
    }

    [Fact]
    public void ImportBindsAfterPopulationAndRequiresCommittedEdits()
    {
        var source = Source("ExcelImportPreviewWindow.cs");
        Assert.True(source.IndexOf("_rows.Add(new PreviewRow", StringComparison.Ordinal) <
            source.IndexOf("_table.ItemsSource = _rows;", StringComparison.Ordinal));
        Assert.Contains("if (!_table.CommitEdit(DataGridEditingUnit.Cell, true) ||", source);
        Assert.Contains("!_table.CommitEdit(DataGridEditingUnit.Row, true)) return;", source);
        Assert.DoesNotContain("ObservableCollection<PreviewRow>", source);
    }

    [Fact]
    public void StatusColorsYieldToSelectionAndDeletionIsNotAnAction()
    {
        var source = Source("ExcelPreviewUi.cs");
        Assert.Contains("GridSelectionTextBrush", source);
        Assert.Contains("nameof(DataGridCell.IsSelected)", source);
        Assert.Contains("ExcelExistingChangeKind.DeletedInSource => \"Нет в Excel\"", source);
        Assert.Contains("ResizeBehavior = GridResizeBehavior.PreviousAndNext", source);
    }
}
