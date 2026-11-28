using System.Globalization;

namespace GmcSmeta.Core.Db;

/// <summary>Single-entry cache for an accepted immutable summary snapshot.
/// Search and missing-price filters change group membership, not the underlying
/// ordered groups. Do not retain earlier SQL snapshots or all grouping modes.</summary>
public sealed class CostSummaryGroupingCache
{
    private CostSummaryData? _snapshot;
    private string? _mode, _sort;
    private CultureInfo? _culture;
    private IReadOnlyList<CostSummaryGrouping.Group>? _groups;

    public IReadOnlyList<CostSummaryGrouping.Group> Get(CostSummaryData snapshot, string mode, string sort)
    {
        var culture = CultureInfo.CurrentCulture;
        if (ReferenceEquals(_snapshot, snapshot) && _mode == mode && _sort == sort &&
            Equals(_culture, culture) && _groups is not null) return _groups;
        var groups = CostSummaryGrouping.Build(snapshot, mode, sort);
        _snapshot = snapshot;
        _mode = mode;
        _sort = sort;
        _culture = culture;
        _groups = groups;
        return groups;
    }

    public void Clear()
    {
        _snapshot = null;
        _groups = null;
        _mode = _sort = null;
        _culture = null;
    }
}
