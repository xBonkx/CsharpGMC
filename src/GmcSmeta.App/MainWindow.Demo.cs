namespace GmcSmeta.App;

public partial class MainWindow
{
    /// <summary>Called only by --demo ... --smoke-test after the real workspace loads.</summary>
    internal async Task<bool> VerifyDemoEstimateAsync()
    {
        if (!GmcSmeta.Core.Demo.DemoMode.Active || !_ready)
            return false;
        var document = _docs.FirstOrDefault(row => row.Kind == "main" && !row.IsStage);
        if (document is null) return false;
        OpenEstimate(document.Id);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!_startupClosed && timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (PageHost.Content is EstimateView page && !page.CacheReadOrWriteInProgress &&
                page.CacheRowWeight > 0 && !string.IsNullOrWhiteSpace(page.ServerRevision) &&
                page.CanReuseCacheRevision(page.ServerRevision))
                return true;
            await Task.Delay(100);
        }
        return false;
    }
}
