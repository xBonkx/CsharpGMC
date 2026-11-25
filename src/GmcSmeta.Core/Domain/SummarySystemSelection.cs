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

    public static bool MatchesSearch(string label, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.All(word => label.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    // Python common._family: ВЕ12 -> ВЕ, В1.4 -> В1, ДП1.2 -> ДП1.
    public static string Family(string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(label ?? "",
            @"^\s*([A-Za-zА-Яа-яЁё]+)(\d*)");
        if (!match.Success) return "";
        var letters = match.Groups[1].Value.ToUpperInvariant();
        var digits = match.Groups[2].Value;
        var next = match.Index + match.Length;
        return digits.Length > 0 && next < label.Length && label[next] is '.' or ','
            ? letters + digits : letters;
    }

    // Group member IDs are part of the identity: if a filter changes the
    // group, do not carry an old selection over to a different set of rows.
    public static string? RowKey(string kind, long systemId, string? supplierKey, IEnumerable<long> members)
    {
        if (kind == "total") return "total";
        if (kind is not ("pos" or "system")) return null;
        var ids = members.Distinct().OrderBy(id => id).ToArray();
        if (ids.Length == 0) return null;
        var suffix = string.Join(",", ids);
        if (kind == "pos") return "pos:" + suffix;
        return supplierKey is null ? $"sys:{systemId}:{suffix}" :
            $"supplier:{supplierKey.Length}:{supplierKey}:{suffix}";
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
