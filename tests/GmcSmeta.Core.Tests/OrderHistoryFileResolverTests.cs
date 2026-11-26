using System.Text.Json;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class OrderHistoryFileResolverTests
{
    private static AccessHistory History(string folder, string data) =>
        new(1, 2, "order", DateTime.Today, folder, "test", data);

    [Theory]
    [InlineData("Files")]
    [InlineData("files")]
    public void PrefersExistingFilesInHistoryOrderOverDirectories(string key)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var first = Path.Combine(dir.FullName, "first.docx");
            var second = Path.Combine(dir.FullName, "second.docx");
            File.WriteAllText(first, "test");
            File.WriteAllText(second, "test");
            var data = JsonSerializer.Serialize(new Dictionary<string, string[]>
            {
                [key] = new[] { dir.FullName, Path.Combine(dir.FullName, "missing.docx"), first, second }
            });
            Assert.Equal(first, OrderHistoryFileResolver.Resolve(History(dir.FullName, data)));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void UsesLegacyFolderOnlyWhenNoPathsWereSpecified()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            Assert.Equal(dir.FullName, OrderHistoryFileResolver.Resolve(History(dir.FullName, "{}")));
            var data = JsonSerializer.Serialize(new { Files = new[] { Path.Combine(dir.FullName, "missing.docx") } });
            Assert.Throws<FileNotFoundException>(() => OrderHistoryFileResolver.Resolve(History(dir.FullName, data)));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void CancelledReadDoesNotParseOrProbe()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            OrderHistoryFileResolver.Resolve(History("", "invalid json"), cancellation.Token));
    }

    [Fact]
    public void WindowRejectsLateResultsAfterSelectionChangeOrClose()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "OrdersWindow.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "OrdersWindow.xaml.cs"));
        var open = source[source.IndexOf("private async void OpenHistory_Click", StringComparison.Ordinal)..
            source.IndexOf("private void Make_Click", StringComparison.Ordinal)];
        Assert.Contains("await Task.Run(() => OrderHistoryFileResolver.Resolve(selected.History, token), token)", open);
        Assert.Contains("!ReferenceEquals(OrderHistory.SelectedItem, selected)", open);
        Assert.True(open.IndexOf("if (_closed || token.IsCancellationRequested", StringComparison.Ordinal) <
            open.IndexOf("Process.Start(", StringComparison.Ordinal));
        Assert.Contains("MakeButton.IsEnabled = false;", open);
        Assert.Contains("if (_historyOpenCancellation is not null) return;", source);
        Assert.Equal(2, source.Split("_historyOpenCancellation?.Cancel();", StringSplitOptions.None).Length - 1);
    }
}
