using System.Windows.Threading;
using GmcSmeta.Core;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    // LRU with a soft count and row-weight budget. Pending writes and drafts
    // can temporarily exceed both limits; they must never be discarded.
    private readonly Dictionary<int, EstimateView> _estimateCache = new();
    private readonly LinkedList<int> _estimateCacheOrder = new();
    private DispatcherTimer? _estimateCacheTrimTimer;
    private int _estimateCacheTrimAttempts;

    private void InitializeEstimateCacheTrim()
    {
        _estimateCacheTrimTimer = new DispatcherTimer(DispatcherPriority.ContextIdle)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _estimateCacheTrimTimer.Tick += (_, _) =>
        {
            _estimateCacheTrimTimer.Stop();
            if (_startupClosed) return;
            TrimEstimateCache();
            // Bounded deferred attempts after pending writes/readback settle.
            // Permanent drafts can pin an entry until the next navigation.
            if (EstimateCacheOverBudget() && --_estimateCacheTrimAttempts > 0)
                _estimateCacheTrimTimer.Start();
        };
    }

    private void TraceEstimateCache(string phase)
    {
        if (StartupTrace.Enabled)
            StartupTrace.Sample(phase, _estimateCache.Count, _estimateCache.Values.Sum(view => view.CacheRowWeight));
    }

    private bool EstimateCacheOverBudget() =>
        EstimateCachePolicy.IsOverBudget(_estimateCache.Count,
            _estimateCache.Values.Sum(view => view.CacheRowWeight));

    private void CacheEstimate(int contractId, EstimateView view)
    {
        _estimateCache[contractId] = view;
        _estimateCacheOrder.Remove(contractId);
        _estimateCacheOrder.AddFirst(contractId);
        TrimEstimateCache(protectedId: contractId);
        TraceEstimateCache("estimate_cache_insert");
    }

    internal void ScheduleEstimateCacheTrim()
    {
        // Even a two-view cache can exceed the row budget after a load or
        // a nested projection materializes. Re-evaluate on navigation/load.
        if (_startupClosed || _estimateCacheTrimTimer is null || _estimateCache.Count <= 1) return;
        _estimateCacheTrimAttempts = 15;
        _estimateCacheTrimTimer.Stop();
        _estimateCacheTrimTimer.Start();
    }

    private void TrimEstimateCache(int? protectedId = null)
    {
        if (!EstimateCacheOverBudget()) return;
        var candidates = _estimateCacheOrder.Reverse().Select(id =>
        {
            var view = _estimateCache[id];
            return new EstimateCachePolicy.Entry(id, view.CacheRowWeight,
                id != protectedId && !ReferenceEquals(PageHost.Content, view) &&
                !view.HasUnsettledCacheWrites && view.CanReleaseCachedView);
        }).ToArray();
        foreach (var id in EstimateCachePolicy.SelectEvictions(candidates))
        {
            _estimateCache.Remove(id);
            _estimateCacheOrder.Remove(id);
            StartupTrace.Mark("estimate_cache_evicted");
            TraceEstimateCache("estimate_cache_after_eviction");
        }
    }
}
