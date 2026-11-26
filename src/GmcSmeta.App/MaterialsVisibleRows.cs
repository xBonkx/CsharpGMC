using Row = GmcSmeta.App.EstimateView.MaterialsRow;

namespace GmcSmeta.App;

/// <summary>Immutable search projection of the currently built materials rows.
/// Retains the exact header/child visibility and same-field AND search rules.</summary>
internal sealed class MaterialsVisibleRows
{
    private readonly Entry[] _entries;
    private readonly record struct Entry(Row Row, string Name, string SystemLabel);
    internal MaterialsVisibleRows(IEnumerable<Row> rows) =>
        _entries = rows.Select(r => new Entry(r, r.Name.ToLowerInvariant(),
            r.SystemLabel.ToLowerInvariant())).ToArray();

    internal List<Row> Filter(string? query, bool onlyMissing, bool onlyNoPrice, long? allowedId)
    {
        var words = (query ?? "").Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant()).ToArray();

        bool Matches(string text)
        {
            foreach (var word in words)
                if (!text.Contains(word, StringComparison.Ordinal)) return false;
            return true;
        }

        bool PositionOk(Entry e) =>
            (!onlyMissing || e.Row.NoCostFlag) && (!onlyNoPrice || e.Row.NoPriceFlag) &&
            (allowedId is null || e.Row.SystemId == allowedId) &&
            (Matches(e.Name) || Matches(e.SystemLabel));

        var result = new List<Row>(_entries.Length);
        // A header belongs to the following contiguous positions up to the
        // next header. Delay insertion until we know whether it has children.
        for (int i = 0; i < _entries.Length;)
        {
            var entry = _entries[i++];
            var row = entry.Row;
            if (!row.IsSystemHeader && !row.IsGroup)
            {
                if (PositionOk(entry)) result.Add(row);
                continue;
            }

            var insertAt = result.Count;
            while (i < _entries.Length && !_entries[i].Row.IsSystemHeader && !_entries[i].Row.IsGroup)
            {
                var child = _entries[i++];
                if (PositionOk(child)) result.Add(child.Row);
            }
            var hasChildren = result.Count > insertAt;
            var showHeader = row.IsGroup
                ? hasChildren || (!onlyMissing && !onlyNoPrice && allowedId is null && Matches(entry.Name))
                : hasChildren && (allowedId is null || allowedId == row.SystemId);
            if (showHeader) result.Insert(insertAt, row);
        }
        return result;
    }

    internal static bool SameReferences(IList<Row> current, IReadOnlyList<Row> next)
    {
        if (current.Count != next.Count) return false;
        for (int i = 0; i < next.Count; i++)
            if (!ReferenceEquals(current[i], next[i])) return false;
        return true;
    }
}
