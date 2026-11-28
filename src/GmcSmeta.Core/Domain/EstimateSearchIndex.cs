namespace GmcSmeta.Core.Domain;

/// <summary>One authoritative row snapshot, one retained query result. The
/// structural parent map is built lazily; no editable view state is cached.
/// UI-thread owned, not a shared global cache.</summary>
public sealed class EstimateSearchIndex
{
    public EstimateSearchIndex(IReadOnlyList<EstimateRow> rows) =>
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));

    public IReadOnlyList<EstimateRow> Rows { get; }
    private int[]? _parents;
    private string? _lastQuery;
    private IReadOnlyList<EstimateRow>? _lastResult;

    private int[] BuildParents()
    {
        var parents = new int[Rows.Count];
        var stack = new List<int>();
        for (var i = 0; i < Rows.Count; i++)
        {
            while (stack.Count > 0 && Rows[stack[^1]].Level >= Rows[i].Level)
                stack.RemoveAt(stack.Count - 1);
            parents[i] = stack.Count == 0 ? -1 : stack[^1];
            stack.Add(i);
        }
        return parents;
    }

    public IReadOnlyList<EstimateRow> Match(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Rows;
        if (_lastResult is not null && string.Equals(query, _lastQuery, StringComparison.Ordinal))
            return _lastResult;
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var parents = _parents ??= BuildParents();
        var visible = new bool[Rows.Count];
        int? matchedLevel = null;
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (matchedLevel is int level && row.Level <= level) matchedLevel = null;
            if (matchedLevel.HasValue) { visible[i] = true; continue; }

            var matches = true;
            foreach (var word in words)
                if (!row.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            if (!matches) continue;
            visible[i] = true;
            matchedLevel = row.Level;
            for (var parent = parents[i]; parent >= 0 && !visible[parent]; parent = parents[parent])
                visible[parent] = true;
        }

        var result = new List<EstimateRow>();
        for (var i = 0; i < Rows.Count; i++)
            if (visible[i]) result.Add(Rows[i]);
        _lastQuery = query;
        return _lastResult = result.AsReadOnly();
    }

    // Public projection callers may still supply a different snapshot.
    internal static IReadOnlyList<EstimateRow> Apply(IReadOnlyList<EstimateRow> rows,
        string? query, EstimateSearchIndex? index) =>
        index is not null && ReferenceEquals(index.Rows, rows)
            ? index.Match(query) : EstimateFilter.Apply(rows, query ?? "");
}
