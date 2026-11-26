using Row = GmcSmeta.App.EstimateView.SummaryRow;

namespace GmcSmeta.App;

/// <summary>Presentation equivalence and exact, unambiguous restore targets.
/// Member IDs are also write targets: sequence equality is intentional.</summary>
internal static class SummaryRowSequence
{
    internal static bool Same(IReadOnlyList<Row> current, IReadOnlyList<Row> next)
    {
        if (current.Count != next.Count) return false;
        for (var i = 0; i < current.Count; i++)
        {
            var a = current[i];
            var b = next[i];
            if (ReferenceEquals(a, b)) continue;
            if (!a.MemberIds.AsSpan().SequenceEqual(b.MemberIds)) return false;
            // Record equality checks every stored presentation field, not just
            // the visible amounts. Normalize the sole array field for comparison.
            if (a.MemberIds != b.MemberIds)
            {
                if ((a with { MemberIds = b.MemberIds }) != b) return false;
            }
            else if (a != b) return false;
        }
        return true;
    }

    internal static Dictionary<string, Row> RestoreCandidates(IEnumerable<Row> rows,
        IReadOnlySet<string> selected, params string?[] anchors)
    {
        var wanted = new HashSet<string>(selected, StringComparer.Ordinal);
        foreach (var key in anchors)
            if (key is not null) wanted.Add(key);
        var matches = new Dictionary<string, Row>(StringComparer.Ordinal);
        if (wanted.Count == 0) return matches;
        // An ambiguous key is permanently removed, even if a third match appears.
        foreach (var row in rows)
        {
            var key = GmcSmeta.Core.Domain.SummarySystemSelection.RowKey(
                row.Kind, row.SystemId, row.SupplierGroupKey, row.MemberIds);
            if (key is null || !wanted.Contains(key)) continue;
            if (!matches.TryAdd(key, row))
            {
                matches.Remove(key);
                wanted.Remove(key);
            }
        }
        return matches;
    }
}
