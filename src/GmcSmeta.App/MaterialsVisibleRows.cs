using Row = GmcSmeta.App.EstimateView.MaterialsRow;

namespace GmcSmeta.App;

/// <summary>Preordered hierarchy of the authoritative material view.
/// Filtering and disclosure never mutate financial rows or their order.</summary>
internal sealed class MaterialsVisibleRows
{
    private readonly Entry[] _entries;
    private readonly record struct Entry(Row Row, string Name, string SystemLabel, int Parent);

    internal MaterialsVisibleRows(IEnumerable<Row> rows)
    {
        var entries = new List<Entry>();
        var indexes = new Dictionary<Row, int>();
        foreach (var row in rows)
        {
            row.HasChildren = false;
            var parent = -1;
            if (row.Parent is { } p)
            {
                if (!indexes.TryGetValue(p, out parent))
                    throw new InvalidOperationException("Родитель строки материалов отсутствует.");
                p.HasChildren = true;
            }
            indexes.Add(row, entries.Count);
            entries.Add(new Entry(row, row.Name.ToLowerInvariant(), row.SystemLabel.ToLowerInvariant(), parent));
        }
        _entries = entries.ToArray();
    }

    internal void SetNestedExpanded(bool expand)
    {
        foreach (var e in _entries)
            if (e.Row.HasChildren)
                e.Row.IsExpanded = expand || (e.Parent < 0 && e.Row.IsSystemHeader);
    }

    internal List<Row> Filter(string? query, bool onlyMissing, bool onlyNoPrice, long? allowedId,
        bool export = false)
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

        var eligible = new bool[_entries.Length];
        // Propagate qualifying descendants before applying disclosure. A collapsed
        // merged group remains discoverable by its members, not just its first system.
        for (int i = _entries.Length - 1; i >= 0; i--)
        {
            var e = _entries[i];
            var row = e.Row;
            if (row.IsSystemHeader)
                eligible[i] &= row.IsGroup || allowedId is null || allowedId == row.SystemId;
            else if (row.IsGroup)
                eligible[i] |= !onlyMissing && !onlyNoPrice && allowedId is null && Matches(e.Name);
            else
                eligible[i] |= PositionOk(e);
            if (eligible[i] && e.Parent >= 0)
                eligible[e.Parent] = true;
        }

        var visible = new bool[_entries.Length];
        var result = new List<Row>(_entries.Length);
        for (int i = 0; i < _entries.Length; i++)
        {
            var e = _entries[i];
            if (!eligible[i]) continue;
            if (e.Parent >= 0)
            {
                if (!visible[e.Parent]) continue;
                var parent = _entries[e.Parent].Row;
                // The old material report contains the aggregate once, not its
                // newly inspectable members. The normal view respects disclosure.
                if (export ? parent.IsGroup && !parent.IsSystemHeader : !parent.IsExpanded)
                    continue;
            }
            visible[i] = true;
            result.Add(e.Row);
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
