using System.Threading;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class DirectorReadLifecycleTests
{
    [Theory]
    [InlineData("17", "17", 0, true)]
    [InlineData("17", "18", 0, false)]
    [InlineData("17", "17", 1, false)]
    [InlineData("", "", 0, false)]
    [InlineData(" ", " ", 0, false)]
    public void ReuseRequiresMatchingNonemptyRevisionAndDay(string cached, string current, int offset, bool expected)
    {
        var day = new DateTime(2026, 11, 28);
        Assert.Equal(expected, SqlServerDirector.CanReuseSnapshot(cached, current, day.AddDays(offset), day));
        Assert.False(SqlServerDirector.CanReuseSnapshot(cached, current, null, day));
    }

    [Fact]
    public void CancelledReadDoesNotOpenSql()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlServerDirector.ReadIfChanged("", "kp", "", null, cancellation.Token));
    }

    [Fact]
    public void ViewChecksCurrentGenerationAndExportsCapturedRows()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "DirectorView.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "DirectorView.xaml.cs"));
        Assert.Contains("_snapshots.GetValueOrDefault(mode)", code);
        Assert.Contains("_snapshots.Clear();", code);
        Assert.Contains("await _readGate.WaitAsync(token);", code);
        Assert.Contains("if (entered) _readGate.Release();", code);
        Assert.Contains("if (token.IsCancellationRequested || generation != _loadGeneration || !IsLoaded) return;", code);
        Assert.Contains("if (data.Day != DateTime.Today)", code);
        Assert.Contains("DirectorGrid.Items.OfType<DirectorPreviewRow>().ToArray()", code);
        Assert.Contains("await ContractDocumentSave.XlsxAsync", code);
        Assert.Contains("() => CreateWorkbook(rows, token), token", code);
        Assert.Contains("_exportCancellation?.Cancel();", code);
        Assert.DoesNotContain("foreach (var rowItem in DirectorGrid.Items", code);
    }
}
