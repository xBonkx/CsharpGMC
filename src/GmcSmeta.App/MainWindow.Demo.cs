using GmcSmeta.Core.Demo;

namespace GmcSmeta.App;

public partial class MainWindow
{
    /// <summary>Called only by --demo ... --smoke-test after the real workspace loads.</summary>
    internal async Task<bool> VerifyDemoEstimateAsync()
    {
        if (!DemoMode.Active || !_ready)
        {
            DemoMode.ReportSmokeFailure("demo-workspace-not-ready");
            return false;
        }
        var document = _docs.FirstOrDefault(row => row.Kind == "main" && !row.IsStage);
        if (document is null)
        {
            DemoMode.ReportSmokeFailure("no-non-stage-main-document");
            return false;
        }
        OpenEstimate(document.Id);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!_startupClosed && timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (_openDocumentId == document.Id && PageHost.Content is EstimateView page &&
                !page.CacheReadOrWriteInProgress && page.CacheRowWeight > 0 &&
                !string.IsNullOrWhiteSpace(page.ServerRevision) &&
                page.CanReuseCacheRevision(page.ServerRevision))
                return true;
            await Task.Delay(100);
        }
        DemoMode.ReportSmokeFailure(_startupClosed ? "workspace-closed-during-smoke" :
            PageHost.Content is EstimateView estimate
                ? $"estimate-not-ready: rows={estimate.CacheRowWeight}; busy={estimate.CacheReadOrWriteInProgress}; revision-present={!string.IsNullOrWhiteSpace(estimate.ServerRevision)}"
                : "estimate-view-not-open");
        return false;
    }
}
