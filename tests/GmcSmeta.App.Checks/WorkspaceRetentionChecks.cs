using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using GmcSmeta.App;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

internal static class WorkspaceRetentionChecks
{
    private const int Cycles = 60;

    internal static void RequireIsolatedPilot()
    {
        // The real editor saves WPF preferences on unload. Require an explicit
        // opt-in from a dedicated Windows test account, not the office profile.
        if (Environment.GetEnvironmentVariable("GMC_SMETA_RETENTION_ISOLATED_ACCOUNT") != "1")
            throw new InvalidOperationException("Run under a dedicated Windows test account; set GMC_SMETA_RETENTION_ISOLATED_ACCOUNT=1.");
        var cs = Environment.GetEnvironmentVariable("GMC_SMETA_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Pilot connection required.");
        SqlServerWorkspace.RequirePilotDatabase(cs);
    }

    internal static void Run(string outputDirectory)
    {
        RequireIsolatedPilot();
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "workspace-retention-" + Guid.NewGuid().ToString("N") + ".jsonl");
        using var output = new StreamWriter(path) { AutoFlush = true };
        var seen = new List<WeakReference<EstimateView>>();
        var previous = StartupTrace.CaptureMemory();
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.InitializeWorkspaceNavigation();
            window.Show();
            var startup = Dispatcher.CurrentDispatcher.InvokeAsync<Task<bool>>(
                () => window.InitializeWorkspaceAsync(offerRetry: false)).Task.Unwrap();
            Program.PumpUntil(() => startup.IsCompleted, "retention workspace startup");
            if (!startup.GetAwaiter().GetResult()) throw new InvalidOperationException("Pilot startup failed.");
            var ids = window.RetentionDocumentIds();
            if (ids.Length < 4)
                throw new InvalidOperationException("Retention fixture requires at least four distinct non-stage documents.");
            var host = (ContentControl)window.FindName("PageHost");
            Sample(0, "baseline");
            for (var i = 1; i <= Cycles; i++)
            {
                // In every fifth cycle supersede an open immediately, before
                // its read/revision probe can complete. Never invoke a write.
                if (i % 5 == 0)
                {
                    window.RetentionOpen(ids[i % ids.Length]);
                    Remember(host, seen);
                    window.RetentionClose();
                    WaitDetached(window, host);
                    Sample(i, "superseded_open");
                }

                window.RetentionOpen(ids[(i - 1) % ids.Length]);
                Program.PumpUntil(() => host.Content is EstimateView view &&
                    view.IsEnabled && view.CanReuseCacheRevision(view.ServerRevision), DescribeEstimateWait(host));
                Remember(host, seen);
                VisitTabs(host, cancelImmediately: i % 3 == 0);
                window.RetentionClose();
                WaitDetached(window, host);
                Sample(i, "closed");
            }
            var evicted = CountEvicted(window, seen);
            if (evicted == 0)
                throw new InvalidOperationException("No cache eviction exercised; fixture/result invalid.");
            // Only here, after the measured workload, force a collection to
            // distinguish retained roots from objects awaiting natural GC.
            // Never do this in the production navigation path.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Sample(Cycles, "post_gc_probe");
            output.WriteLine(JsonSerializer.Serialize(new
            {
                completedCycles = Cycles, distinctDocuments = ids.Length,
                observedViews = seen.Count, evicted, evictedStillAlive = CountEvictedAlive(window, seen),
                status = "MEASURED", note = "Retention counts are evidence, not a leak-free verdict."
            }));
            Console.WriteLine("Retention evidence: " + path);
        }
        finally { window.Close(); }

        void Sample(int cycle, string phase)
        {
            var m = StartupTrace.CaptureMemory();
            var elapsed = (m.MonotonicTicks - previous.MonotonicTicks) / (double)m.Frequency;
            var state = window.RetentionCacheState();
            output.WriteLine(JsonSerializer.Serialize(new
            {
                cycle, phase, memory = m,
                allocationBytesPerSecond = elapsed > 0 ? (m.AllocatedBytes - previous.AllocatedBytes) / elapsed : 0,
                gcPauseDeltaMs = m.GcPauseMs - previous.GcPauseMs,
                cacheEntries = state.Entries, cacheRowWeight = state.Weight,
                settled = state.Settled, overBudget = state.OverBudget,
                observedViews = seen.Count, evictedStillAlive = CountEvictedAlive(window, seen)
            }));
            previous = m;
        }
    }

    private static void WaitDetached(MainWindow window, ContentControl host)
    {
        Program.PumpUntil(() => host.Content is null &&
            window.RetentionCacheState() is { Settled: true, OverBudget: false },
            "detached reads and cache budget");
    }

    /// <summary>Timeout diagnostics: publish gates visible to the harness, no internals.</summary>
    private static string DescribeEstimateWait(ContentControl host) =>
        host.Content is EstimateView view
            ? $"authoritative estimate (content={view.GetType().Name}, enabled={view.IsEnabled}, " +
              $"revision='{view.ServerRevision[..Math.Min(12, view.ServerRevision.Length)]}', " +
              $"reusable={view.CanReuseCacheRevision(view.ServerRevision)}, busy={view.CacheReadOrWriteInProgress})"
            : $"authoritative estimate (content={(host.Content?.GetType().Name ?? "null")})";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Remember(ContentControl host, List<WeakReference<EstimateView>> seen)
    {
        if (host.Content is EstimateView view &&
            !seen.Any(w => w.TryGetTarget(out var existing) && ReferenceEquals(existing, view)))
            seen.Add(new WeakReference<EstimateView>(view));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VisitTabs(ContentControl host, bool cancelImmediately)
    {
        var view = (EstimateView)host.Content;
        foreach (var mode in new[] { "materials", "summary", "totals" })
        {
            view.RetentionCostMode(mode);
            if (!cancelImmediately)
                Program.PumpUntil(() => !view.HasNestedCacheActivity, "cost tab read");
            view.RetentionCostMode("estimate");
        }
    }

    private static int CountEvicted(MainWindow window, List<WeakReference<EstimateView>> seen) =>
        seen.Count(w => !w.TryGetTarget(out var target) || !window.RetentionContains(target));

    private static int CountEvictedAlive(MainWindow window, List<WeakReference<EstimateView>> seen) =>
        seen.Count(w => w.TryGetTarget(out var target) && !window.RetentionContains(target));
}
