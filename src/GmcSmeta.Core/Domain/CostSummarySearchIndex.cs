using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Search text and flags prepared once for an accepted cost summary snapshot.
/// Filtering retains the original row objects and their original order.
/// </summary>
public sealed class CostSummarySearchIndex
{
    private readonly Entry[] _entries;
    private readonly record struct Entry(CostSummaryRow Row, string SearchText, CostMissingPrice Missing);

    public CostSummarySearchIndex(IEnumerable<CostSummaryRow> rows, IReadOnlyDictionary<long, string> labels)
    {
        _entries = rows.Select(row => new Entry(row,
            $"{row.Name} {labels.GetValueOrDefault(row.SystemId, "")} {row.Supplier}".ToLowerInvariant(),
            CostMissingPriceIndex.FlagsOf(row))).ToArray();
    }

    public IEnumerable<CostSummaryRow> Filter(string? query, IReadOnlySet<long>? systems,
        bool missingOnly, CostMissingPrice missingMask)
    {
        // Preserve the original space-splitting and invariant casing exactly.
        var words = (query ?? "").Split(' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.ToLowerInvariant()).ToArray();
        if (missingOnly && missingMask == CostMissingPrice.None) yield break;

        foreach (var entry in _entries)
        {
            if (systems is not null && !systems.Contains(entry.Row.SystemId)) continue;
            if (missingOnly && (entry.Missing & missingMask) == 0) continue;
            var matches = true;
            foreach (var word in words)
            {
                if (entry.SearchText.Contains(word, StringComparison.Ordinal)) continue;
                matches = false;
                break;
            }
            if (matches) yield return entry.Row;
        }
    }
}
