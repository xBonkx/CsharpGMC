namespace GmcSmeta.Core.Tests;

public sealed class EstimateReloadSchedulingSourceTests
{
    private static string ReadEditor()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null &&
            !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "EstimateView.xaml.cs"))
            .Replace("\r\n", "\n");
    }

    [Fact]
    public void TimerDefersInsteadOfDroppingRefreshWhileReadOrWriteIsActive()
    {
        var source = ReadEditor();
        var start = source.IndexOf("private async void RefreshAfterSave(", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task ReloadDocumentAsync(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var timer = source[start..end];
        Assert.Contains("if (_refreshing || Volatile.Read(ref _pendingWrites) > 0)\n        {", timer);
        var busy = timer.IndexOf("if (_refreshing ||", StringComparison.Ordinal);
        var rearm = timer.IndexOf("_reloadTimer.Start();", busy, StringComparison.Ordinal);
        var exit = timer.IndexOf("return;", busy, StringComparison.Ordinal);
        Assert.True(rearm > busy && exit > rearm);
        Assert.True(timer.IndexOf("await ReloadDocumentAsync();", StringComparison.Ordinal) > exit);
    }

    [Fact]
    public void DirectReloadConsumesPendingTimerAndForcesAuthoritativeReadback()
    {
        var source = ReadEditor();
        var start = source.IndexOf("private async Task ReloadDocumentAsync(", StringComparison.Ordinal);
        var request = source.IndexOf("var request = _reloadGate.NextRequest();", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && request > start);
        var entry = source[start..request];
        Assert.Contains("if (_reloadTimer.IsEnabled)", entry);
        Assert.Contains("allowUnchanged = false;", entry);
        Assert.Contains("_reloadTimer.Stop();", entry);
    }
}
