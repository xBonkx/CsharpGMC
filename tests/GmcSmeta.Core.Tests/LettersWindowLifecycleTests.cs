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
        Assert.Contains("await Task.Run(() => archive.Sql.List(query, status).Where(Belongs).ToList())", code);
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
}
