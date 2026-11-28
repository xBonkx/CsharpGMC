namespace GmcSmeta.Core.Tests;

public sealed class PassportWindowLifecycleTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", file));
    }

    [Fact]
    public void InitialReadAndSummaryPublicationAreSeparated()
    {
        var code = Source("GmcSmeta.App/PassportsWindow.xaml.cs");
        Assert.Contains("Loaded += InitializeAsync;", code);
        Assert.Contains("var data = await Task.Run", code);
        Assert.Contains("if (_closed) return;", code);
        Assert.Contains("PassportList.ItemsSource = summaries;", code);
        Assert.DoesNotContain("_summaries.Add", code);
        Assert.Contains("finally { _loading = wasLoading; }", code);
    }

    [Fact]
    public void ArchiveRootAndHistoryUseWorkerAndCapturedPath()
    {
        var code = Source("GmcSmeta.App/PassportsWindow.xaml.cs");
        Assert.Contains("_sql.HasAnyExports()", code);
        Assert.DoesNotContain("_rows.Any(p => _sql.Exports", code);
        Assert.Contains("await Task.Run(() => _sql.Exports(_objectId, id))", code);
        Assert.Contains("PdfFilePath(root, selected.RelativePath)", code);
        Assert.Contains("if (!pickerClosed) OpenPath(file);", code);
        Assert.Contains("if (!digest.Equals(selected.Sha256", code);
    }

    [Fact]
    public void PdfExportPreservesRevisionAndUncertainCommitGuard()
    {
        var code = Source("GmcSmeta.App/PassportsWindow.xaml.cs");
        Assert.Contains("await Task.Run(() => ExportCore(selected, root))", code);
        Assert.Contains("_exportUncertain = result.Uncertain;", code);
        Assert.Contains("if (!_ready || _busy || _exportUncertain) return;", code);
        Assert.Contains("p.Revision);", code);
        Assert.Contains("if (ownsTemporary)", code);
        Assert.Contains("if (_busy) { e.Cancel = true; return; }", Source("GmcSmeta.App/PassportsWindow.Operations.cs"));
        Assert.Contains("uncertain = true;", code);
        Assert.Contains("break; // do not continue the batch after an unknown registration", code);
    }

    [Fact]
    public void WritesCollectBeforeExclusiveWorkerAndCloseOnlyAfterSave()
    {
        var code = Source("GmcSmeta.App/PassportsWindow.Operations.cs");
        Assert.True(code.IndexOf("if (collect) Collect();", StringComparison.Ordinal) <
            code.IndexOf("SetBusy(true);", StringComparison.Ordinal));
        Assert.Contains("await Task.Run(() => _sql.Save(p))", code);
        // Delete-снимок id/revision берётся до подтверждения и перепроверяется после диалога.
        Assert.Contains("await Task.Run(() => _sql.Delete(_objectId, id, revision))", code);
        Assert.Contains("!ReferenceEquals(_rows[index], p) || p.Id != id || p.Revision != revision", code);
        Assert.Contains("e.Cancel = true; // cancel synchronously", code);
        Assert.Contains("else if (!await SaveAsync()) return;", code);
        Assert.Contains("Dispatcher.BeginInvoke(new Action(Close))", code);
        Assert.Contains("ex is PassportWriteCommitUncertainException or DBConcurrencyException", code);
    }

    [Fact]
    public void ProgrammaticSelectionDoesNotTriggerAnotherSaveAndBatchUsesDetachedCopies()
    {
        var code = Source("GmcSmeta.App/PassportsWindow.Operations.cs");
        var window = Source("GmcSmeta.App/PassportsWindow.xaml.cs");
        Assert.Contains("if (!_ready || _busy || _loading", code);
        Assert.Contains("SelectPassport(_rows.Count - 1);", code);
        Assert.DoesNotContain("PassportList.SelectedIndex = _rows.Count - 1", code);
        Assert.Contains("copy.Revision = p.Revision;", code);
        Assert.Contains("_sql.SaveBatch(_objectId, copies);", code);
        Assert.Contains("PassportList.SelectedItems.Add(summary);", window);
        Assert.DoesNotContain("SaveCurrent()", window);
    }

    [Fact]
    public void UnknownWriteCommitIsDistinctFromExportAndCatalogueReadFailure()
    {
        var sql = Source("GmcSmeta.Core/Db/SqlServerPassports.cs");
        var code = Source("GmcSmeta.App/PassportsWindow.Operations.cs");
        Assert.Contains("CommitWrite(tx);", sql);
        Assert.Contains("throw new PassportWriteCommitUncertainException", sql);
        Assert.Contains("throw new PassportExportCommitUncertainException", sql);
        Assert.Contains("Паспорт сохранён, но каталог оборудования не обновлён.", code);
    }
}
