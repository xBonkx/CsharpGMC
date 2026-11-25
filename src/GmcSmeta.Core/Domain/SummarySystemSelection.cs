namespace GmcSmeta.Core.Domain;

/// <summary>Python SystemPicker semantics: no selection = all; selected
/// parents include their immediate children, without changing stored rows.</summary>
public static class SummarySystemSelection
{
    public static HashSet<long>? Expand(IEnumerable<long> selected, IReadOnlyDictionary<long, long> parents)
    {
        var ids = selected.ToHashSet();
        if (ids.Count == 0) return null;
        var result = new HashSet<long>(ids);
        foreach (var (child, parent) in parents)
            if (ids.Contains(parent)) result.Add(child);
        return result;
    }

    public static Dictionary<long, string> Labels(IEnumerable<(long Id, string Name)> systems,
        IReadOnlyDictionary<long, long> parents)
    {
        var names = systems.ToDictionary(s => s.Id, s => s.Name);
        return names.ToDictionary(s => s.Key, s =>
            parents.TryGetValue(s.Key, out var parent) && names.TryGetValue(parent, out var label)
                ? $"{label} › {s.Value}" : s.Value);
    }
}
