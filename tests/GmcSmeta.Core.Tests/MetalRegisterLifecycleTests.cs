namespace GmcSmeta.Core.Tests;

public sealed class MetalRegisterLifecycleTests
{
    private static string Source()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "MetalRegisterWindow.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "MetalRegisterWindow.cs"));
    }

    [Fact]
    public void WorkbookInitializationAndMetadataAreOffDispatcherAndPagesAreLazy()
    {
        var code = Source();
        Assert.Contains("await Task.Run(() => new SqlServerMetalWarehouse(_connectionString))", code);
        Assert.Contains("book.Worksheets.Select(MetalRegisterSheet.Describe)", code);
        Assert.Contains("first = new MetalRegisterSheet(prepared.Sheets[0], MarkDirty)", code);
        Assert.Contains("if (tab.Content is not MetalRegisterSheet page)", code);
        Assert.Contains("if (_busy || !CommitEditor())", code);
        Assert.Contains("if (_busy || !CommitEditor() || !ConfirmDiscard()) e.Cancel = true;", code);
    }

    [Fact]
    public void SaveAndExportCommitEditorAndReserveTheLiveWorkbook()
    {
        var code = Source();
        Assert.Contains("!CommitEditor() || !_dirty", code);
        Assert.Contains("var version = _version;", code);
        Assert.Contains("store.SaveRegister(bytes,version,mainRevision)", code);
        Assert.Contains("var bytes = LiveWorkbookSnapshot.Capture(book)", code);
        Assert.Contains("await Task.Run(() => ExportFilePublication.WriteBytes(path, bytes))", code);
        Assert.Contains("_header.IsEnabled = !busy && _warehouse is not null;", code);
        Assert.Contains("_tabs.IsEnabled = !busy;", code);
        Assert.Contains("finally { SetBusy(false); }", code);
    }

    [Fact]
    public void SectionProjectionUsesOneScanAndRefreshIsDeferredUntilCommit()
    {
        var code = Source();
        Assert.Contains("nonempty = usedColumns[col] = true;", code);
        Assert.DoesNotContain("rows.Any(row => !_sheet.Cell(row,col)", code);
        Assert.Contains("_table.CommitEdit(DataGridEditingUnit.Cell, true)", code);
        Assert.Contains("_table.CommitEdit(DataGridEditingUnit.Row, true)", code);
        Assert.Contains("if (_table.Items is IEditableCollectionView { IsEditingItem: true }) return;", code);
        Assert.Contains("_table.RowEditEnding += (_, _) => QueueRefresh();", code);
        Assert.Contains("_formulas.Clear();", code);
    }
}
