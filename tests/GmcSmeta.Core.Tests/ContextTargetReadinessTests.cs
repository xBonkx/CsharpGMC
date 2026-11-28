namespace GmcSmeta.Core.Tests;

public sealed class ContextTargetReadinessTests
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
    public void StageHeaderCannotFallbackToAnUnrelatedStage()
    {
        var source = Source("StagesDialog.xaml.cs");
        var start = source.IndexOf("private void StagesGrid_ContextMenuOpening", StringComparison.Ordinal);
        var end = source.IndexOf("private void BuildStageRowsMenu", start, StringComparison.Ordinal);
        var menu = source[start..end];
        Assert.Contains("keyboard ? StagesGrid.CurrentColumn : ColumnUnderMouse(out header)", menu);
        Assert.Contains("ContextMenuTarget.SelectRow<DataRowView>(StagesGrid, e)", menu);
        Assert.Contains("!_columnStages.TryGetValue", menu);
        Assert.Contains("stage.Id == stageId && !stage.Rest", menu);
        Assert.Contains("if (selectedStage is null)", menu);
        Assert.Contains("menuRevision == _revision", menu);
        Assert.Contains("StagesGrid.InputHitTest(point)", menu);
        Assert.DoesNotContain("Columns.First", menu);
    }

    [Fact]
    public void ActMenuRetainsSelectionAndRejectsChangedSnapshotOrSignedAct()
    {
        var target = Source("ContextMenuTarget.cs");
        Assert.Contains("if (!grid.SelectedItems.Contains(item))", target);
        Assert.Contains("grid.UnselectAll();", target);
        Assert.Contains("grid.InputHitTest(point)", target);
        Assert.Contains("return grid.SelectedItems.Contains(item) ? item : null;", target);
        var editor = Source("ActsEditorView.xaml.cs");
        var start = editor.IndexOf("private void ActGrid_ContextMenuOpening", StringComparison.Ordinal);
        var end = editor.IndexOf("private List<KsBatchPosition> SelectedPositionStates", start, StringComparison.Ordinal);
        var menu = editor[start..end];
        Assert.Contains("hit is null", menu);
        Assert.Contains("!_signed && !_contractLocked", menu);
        Assert.Contains("_contractId == menuContractId", menu);
        Assert.Contains("ReferenceEquals(_sheet, menuSheet)", menu);
        Assert.Contains("SequenceEqual(menuRows)", menu);
        Assert.Contains("if (CanExecute()) FillRest_Click", menu);
        Assert.Contains("if (CanExecute()) ClearSelected_Click", menu);
    }

    [Theory]
    [InlineData("КП / смета — Excel (как в Python)", "pdf: false")]
    [InlineData("КП / смета — PDF", "pdf: true")]
    [InlineData("КП — Excel, с выделением НДС", "pdf: false, vatLine: true")]
    [InlineData("КП — PDF, с выделением НДС", "pdf: true, vatLine: true")]
    [InlineData("Внутренние расценки — Excel", "pdf: false, internalPrices: true")]
    [InlineData("Внутренние расценки — PDF", "pdf: true, internalPrices: true")]
    [InlineData("Позиционная смета — Excel", "pdf: false, positional: true")]
    [InlineData("Позиционная смета — PDF", "pdf: true, positional: true")]
    public void EveryNavigationExportSpecifiesExactFormatAndOptions(string caption, string args)
    {
        var main = Source("MainWindow.xaml.cs");
        Assert.Contains("AddKpExport(\"" + caption + "\", " + args + ");", main);
        Assert.Contains("void AddKpExport(string label, bool pdf,", main);
        Assert.Contains("ExportKpDocument(doc, internalPrices, positional, pdf, vatLine)", main);
        var export = Source("MainWindow.ContractExport.cs");
        Assert.Contains("if (pdf)", export);
        Assert.Contains("ContractDocumentSave.PdfAsync", export);
        Assert.Contains("KpPdfForms.Create", export);
        Assert.Contains("ContractDocumentSave.XlsxAsync", export);
        Assert.Contains("KpExcelForms.Create", export);
    }
}
