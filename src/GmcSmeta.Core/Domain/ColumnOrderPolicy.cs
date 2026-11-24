namespace GmcSmeta.Core.Domain;

/// <summary>Normalize a saved/displayed column order without moving fixed slots.
/// Unknown, duplicated or temporarily missing preference IDs are harmless.</summary>
public static class ColumnOrderPolicy
{
    public static string[] Normalize(IReadOnlyList<string> defaults,
        IEnumerable<string> preferred, IReadOnlySet<string> fixedIds)
    {
        var available = defaults.ToHashSet(StringComparer.Ordinal);
        if (available.Count != defaults.Count)
            throw new ArgumentException("Duplicate default column IDs.", nameof(defaults));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var movable = new Queue<string>(preferred.Concat(defaults)
            .Where(id => id is not null && available.Contains(id) &&
                !fixedIds.Contains(id) && seen.Add(id)));
        return defaults.Select(id => fixedIds.Contains(id) ? id : movable.Dequeue()).ToArray();
    }
}
