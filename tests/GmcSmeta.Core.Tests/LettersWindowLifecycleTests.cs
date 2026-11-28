namespace GmcSmeta.Core.Tests;

public sealed class LettersWindowLifecycleTests
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
    public void ListRequestsAreDebouncedAndSingleFlight()
    {
        var code = Source("LettersWindow.xaml.cs");
        Assert.Contains("TimeSpan.FromMilliseconds(200)", code);
        Assert.Contains("Loaded += InitializeAsync;", code);
        Assert.Contains("await Task.Run(() => new LettersArchive", code);
        Assert.Contains("if (_closed || _busy || _reading || _archive is null) return;", code);
        Assert.Contains("while (_refreshRequested && !_closed && !_busy)", code);
        Assert.Contains("generation != _generation || !ReferenceEquals(archive, _archive)", code);
        Assert.Contains("await Task.Run(() => archive.Sql.ListAsync(query, status, token, Belongs), token)", code);
        Assert.Contains("_listReadCancellation?.Cancel();", code);
        Assert.Contains("token.IsCancellationRequested || generation != _generation", code);
        Assert.Contains("_listReadCancellation = null;", code);
        Assert.Contains("_searchTimer.Tick -= SearchTimer_Tick;", code);
    }

    [Fact]
    public void ActionsCaptureArchiveAndKeepCloseBlockedUntilOutcome()
    {
        var code = Source("LettersWindow.xaml.cs");
        Assert.Contains("if (_closed || _busy || !_ready || _archive is not { } archive) return;", code);
        Assert.Contains("if (_busy) e.Cancel = true;", code);
        Assert.Contains("await Task.Run(() => archive.Sql.Save(doc", code);
        Assert.Contains("await Task.Run(() => archive.Sql.Get(selected.Id))", code);
        Assert.Contains("ItemsControl.ContainerFromElement(LetterList, source) is DataGridRow row", code);
        Assert.Contains("ReferenceEquals(row.Item, Selected())", code);
        Assert.Contains("current.Revision != chosen.Revision || !LetterContext.CanAttach(current)", code);
        Assert.Contains("archive.Sql.Save(current, validateContext: true)", code);
        Assert.Contains("await Task.Run(() => archive.Backup(path))", code);
        Assert.Contains("await Task.Run(() => LettersArchive.RestoreBackup", code);
    }

    [Fact]
    public void BackupPublishesOnlyCompleteZipAndKeepsRootGuard()
    {
        var code = Source("LettersArchive.cs");
        var start = code.IndexOf("public void Backup(", StringComparison.Ordinal);
        var end = code.IndexOf("public static void ValidateRestoreZip", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var backup = code[start..end];
        Assert.Contains("zipPath.StartsWith(Root.TrimEnd", backup);
        Assert.Contains("ExportFilePublication.Write(zipPath, stream => WriteBackup(stream));", backup);
        Assert.Contains("new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)", backup);
        Assert.Contains("AddJson(zip, \"history.json\"", backup);
        Assert.DoesNotContain("ZipFile.Open(zipPath", backup);
    }

    [Fact]
    public void EditorUsesExclusiveWorkerAndStaForDocumentRendering()
    {
        var code = Source("LetterEditorWindow.xaml.cs");
        Assert.Contains("await Task.Run(() => _archive.Sql.Save(_letter))", code);
        Assert.Contains("await RunStaAsync(() => _archive.Build(_letter, combine))", code);
        Assert.Contains("thread.SetApartmentState(ApartmentState.STA)", code);
        Assert.Contains("FilesGrid.ItemsSource = _letter.Files.ToArray();", code);
        Assert.Contains("if (_busy) { e.Cancel = true; return; }", code);
        Assert.Contains("Dispatcher.BeginInvoke(new Action(Close))", code);
        Assert.Contains("request != _numberRequest", code);
        Assert.Contains("await Task.Run(() => _archive.Sql.ReadHistory(_letter.Id))", code);
    }

    [Fact]
    public void ListReaderObservesCancellationAndSharesLegacySearchPolicy()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerLetters.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerLetters.cs"));
        Assert.Contains("await OpenReadAsync(token).ConfigureAwait(false)", code);
        Assert.Contains("SqlServerDatabase.OpenAsync(_connectionString, token)", code);
        Assert.Contains("RequirePilotConnection(await SqlServerDatabase.OpenAsync", code);
        Assert.Contains("await cmd.ExecuteReaderAsync(token)", code);
        Assert.Contains("await reader.ReadAsync(token)", code);
        Assert.Contains("token.ThrowIfCancellationRequested();", code);
        Assert.Contains("if (MatchesList(item, query, status)) list.Add(item);", code);
        Assert.Contains("MatchesList(item, query, status) && (belongs is null || belongs(item))", code);
        Assert.Contains("StringComparison.CurrentCultureIgnoreCase", code);
        Assert.Contains("ORDER BY updated_utc DESC,id DESC", code);
    }

    [Fact]
    public void NewLetterPrefillsRecipientButExistingAttachmentDoesNot()
    {
        var window = Source("LettersWindow.xaml.cs");
        Assert.Contains("useAgreementRecipient: _contractContext is not null", window);
        Assert.Contains("archive.Sql.Save(current, validateContext: true)", window);
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerLetters.AgreementContext.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var folder = Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db");
        var save = File.ReadAllText(Path.Combine(folder, "SqlServerLetters.cs"));
        Assert.Contains("useAgreementRecipient && (!validateContext || existing is not null || release is not null)", save);
        Assert.Contains("ApplyAgreementContext(conn, tx, document, objectId, useAgreementRecipient);", save);
        var context = File.ReadAllText(Path.Combine(folder, "SqlServerLetters.AgreementContext.cs"));
        Assert.Contains("!SqlServerAgreements.Enabled(conn, tx)", context);
        Assert.Contains("a.id=c.agreement_id AND a.object_id=c.object_id", context);
        Assert.Contains("a.archived=0", context);
        Assert.Contains("c.id=@contract AND c.object_id=@object", context);
        Assert.Contains("WITH(HOLDLOCK)", context);
    }
}
