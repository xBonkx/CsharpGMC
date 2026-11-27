namespace GmcSmeta.Core.Domain;

/// <summary>Per-projection lazy area keys. Never survive a new SQL snapshot,
/// and use exactly the existing displayed rounding and missing-value policy.</summary>
internal sealed class EstimateAreaSortKeys(string field)
{
    private Dictionary<int, decimal?>? _keys;

    internal decimal? Get(EstimateRow row)
    {
        _keys ??= new Dictionary<int, decimal?>();
        if (_keys.TryGetValue(row.SortKey, out var value)) return value;
        value = EstimateViewProjection.AreaValue(row, field == EstimateViewProjection.ByAreaTotal);
        _keys.Add(row.SortKey, value);
        return value;
    }
}
