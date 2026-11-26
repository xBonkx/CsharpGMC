namespace GmcSmeta.Core.Domain;

/// <summary>Soft limits for retained estimate views. Counts are row weights,
/// not an estimate of actual managed bytes. A live or dirty view always wins.</summary>
public static class EstimateCachePolicy
{
    public const int MaxViews = 3;
    public const long MaxRowWeight = 25_000;

    public readonly record struct Entry(int Id, long RowWeight, bool CanEvict);

    public static bool IsOverBudget(int count, long rowWeight) =>
        count > MaxViews || (count > 1 && rowWeight > MaxRowWeight);

    /// <summary>Entries are ordered least-recently-used first. Never evict
    /// the last view solely for its size, and never evict a protected entry.</summary>
    public static IReadOnlyList<int> SelectEvictions(IReadOnlyList<Entry> oldestFirst)
    {
        var count = oldestFirst.Count;
        var weight = oldestFirst.Sum(e => Math.Max(0L, e.RowWeight));
        if (!IsOverBudget(count, weight)) return Array.Empty<int>();
        var removed = new List<int>();
        foreach (var entry in oldestFirst)
        {
            if (!IsOverBudget(count, weight)) break;
            if (!entry.CanEvict) continue;
            removed.Add(entry.Id);
            count--;
            weight -= Math.Max(0L, entry.RowWeight);
        }
        return removed;
    }
}
