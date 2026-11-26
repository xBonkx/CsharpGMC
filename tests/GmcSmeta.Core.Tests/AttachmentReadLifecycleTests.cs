using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class AttachmentReadLifecycleTests
{
    [Fact]
    public void CancelledReadDoesNotOpenSql()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SqlServerAttachments.Read("invalid connection", 1, cancellation.Token));
    }

    [Fact]
    public void AttachmentReadPublishesOnceAndKeepsMutationCloseGuard()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "AttachmentWindow.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "AttachmentWindow.cs"));
        Assert.Contains("Loaded += async (_, _) => await ReloadAsync(allowClose: true)", source);
        Assert.Contains("if (_loading && !_allowCloseDuringRead) e.Cancel = true;", source);
        Assert.Contains("_readCancellation?.Cancel();", source);
        Assert.Contains("ReloadAsync(bool allowClose = false)", source);
        Assert.Contains("SqlServerAttachments.Read(_connectionString, _contractId, token)", source);
        Assert.Contains("if (_closed || token.IsCancellationRequested) return;", source);
        Assert.Contains("_rows = snapshot.Rows;", source);
        Assert.Contains("_grid.ItemsSource = _rows;", source);
        Assert.Contains("_grid.SelectedItem = _rows.FirstOrDefault(row => row.Id == selected);", source);
        Assert.DoesNotContain("foreach (var row in rows) _rows.Add(row)", source);
        Assert.Contains("finally { await ReloadAsync(); }", source);
        Assert.Contains("private bool CanWrite => !_loading && !string.IsNullOrWhiteSpace(_revision)", source);
    }
    [Fact]
    public void FileOpenChecksNetworkOnWorkerAndRevalidatesTargetBeforeShell()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "AttachmentWindow.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var source = File.ReadAllText(Path.Combine(app, "AttachmentWindow.cs"));
        Assert.Contains("await Task.Run(() => ValidateOpenFile(row, _signedOnly), token)", source);
        Assert.Contains("if (CanPublish()) LaunchFile(row);", source);
        Assert.Contains("ReferenceEquals(_grid.SelectedItem, row) && _rows.Contains(row)", source);
        Assert.Contains("_openCancellation?.Cancel();", source);
        Assert.Contains("!DirectOpenOwners.Add(owner)", source);
        Assert.Contains("DirectOpenOwners.Remove(owner);", source);
        Assert.Contains("owner.IsLoaded && validTarget()", source);
        Assert.Contains("if (!CanPublish()) return;", source);
        Assert.Contains("SqlServerAttachments.Read(connectionString, contractId, token)", source);
        Assert.Contains("SignedPdfSelection.DirectId(entries.Select", source);
        Assert.Contains("ValidateOpenFile(selected, signedOnly: true);", source);
        var main = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));
        Assert.Contains("await AttachmentWindow.OpenSignedAsync(this, connection!, doc.Id, doc.Title, ValidTarget)", main);
        Assert.Contains("await AttachmentWindow.OpenSignedAsync(this, connection, doc.Id, doc.Title, () =>", main);
    }

}
