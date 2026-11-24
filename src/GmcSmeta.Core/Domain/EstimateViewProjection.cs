using System.Globalization;
using System.Text;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Temporary, read-only view of the estimate (Python kp_editor.py: _build_view).
/// Never changes SortKey, positions, SQL Server ordering or document totals.
/// A position and all its component rows must stay together while sorting.
/// </summary>
public static class EstimateViewProjection
{
    public const string DocumentOrder = "kp";
    public const string ByName = "name";
    public const string ByAmount = "sum";
    public const string ByPrice = "price";
    public const string ByQuantity = "qty";

    public static IReadOnlyList<EstimateRow> Apply(
        IReadOnlyList<EstimateRow> rows, string? query = null,
        bool noPriceOnly = false, string sort = DocumentOrder, bool flat = false,
        IReadOnlyDictionary<int, int>? frozenOrder = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (sort is not (DocumentOrder or ByName or ByAmount or ByPrice or ByQuantity))
            throw new ArgumentOutOfRangeException(nameof(sort));

        // Most opens use document order without a search or missing-price
        // filter. No intermediate hash set or list is needed for this case.
        if (string.IsNullOrWhiteSpace(query) && !noPriceOnly &&
            sort == DocumentOrder && !flat)
            return rows;

        var allowed = EstimateFilter.Apply(rows, query ?? "")
            .Select(row => row.SortKey).ToHashSet();

        if (noPriceOnly)
        {
            // Display an empty-price top-level position together with its
            // components and section ancestors, but do not show other priced
            // positions from the same section.
            var matched = rows.Where(row => !row.IsSystem && !row.IsSub &&
                row.HasNoPrice && allowed.Contains(row.SortKey)).ToArray();
            var parentIds = matched.Select(row => row.Id).ToHashSet();
            var sectionIds = matched.Select(row => row.SystemId).ToHashSet();

            var rootBySection = new Dictionary<int, int>();
            var root = 0;
            foreach (var row in rows)
            {
                if (!row.IsSystem) continue;
                if (row.Level == 0) root = row.SystemId;
                rootBySection[row.SystemId] = root;
            }
            foreach (var sectionId in sectionIds.ToArray())
                if (rootBySection.TryGetValue(sectionId, out var rootId))
                    sectionIds.Add(rootId);

            var requested = matched.Select(row => row.SortKey).ToHashSet();
            foreach (var row in rows)
            {
                if (row.IsSub && row.ParentPositionId is int parentId &&
                    parentIds.Contains(parentId))
                    requested.Add(row.SortKey);
                else if (row.IsSystem && sectionIds.Contains(row.SystemId))
                    requested.Add(row.SortKey);
            }
            allowed.IntersectWith(requested);
        }

        var filtered = rows.Where(row => allowed.Contains(row.SortKey)).ToList();
        if (sort == DocumentOrder && !flat) return filtered;

        // Split into atomic position blocks (parent + its child components).
        // Keep system headers separately in their original sequence.
        var result = new List<EstimateRow>(filtered.Count);
        var blocks = new List<List<EstimateRow>>();
        List<EstimateRow>? block = null;

        void Flush()
        {
            if (block is not null && block.Count > 0) blocks.Add(block);
            block = null;
        }

        void OutputBlocks()
        {
            Flush();
            if (sort != DocumentOrder)
                blocks.Sort((a, b) => Compare(a[0], b[0], sort, frozenOrder));
            foreach (var group in blocks) result.AddRange(group);
            blocks.Clear();
        }

        foreach (var row in filtered)
        {
            if (row.IsSystem)
            {
                if (!flat) OutputBlocks();
                if (!flat) result.Add(row);
                continue;
            }

            if (row.IsSub && block is not null &&
                row.ParentPositionId == block[0].Id)
            {
                block.Add(row);
            }
            else
            {
                Flush();
                block = new List<EstimateRow> { row };
            }
        }
        OutputBlocks();
        return result;
    }

    private static int Compare(EstimateRow left, EstimateRow right, string sort,
        IReadOnlyDictionary<int, int>? frozenOrder)
    {
        // Python kp_editor._view_key: keep the result of the first sort
        // stable while a row's price/quantity is being edited.
        if (frozenOrder is not null &&
            frozenOrder.TryGetValue(left.Id, out var leftRank) &&
            frozenOrder.TryGetValue(right.Id, out var rightRank))
        {
            var existing = leftRank.CompareTo(rightRank);
            if (existing != 0) return existing;
        }
        var compared = sort switch
        {
            ByName => NaturalCompare(left.Name, right.Name),
            ByAmount => right.Amount.CompareTo(left.Amount),
            ByPrice => right.Price.CompareTo(left.Price),
            ByQuantity => right.Qty.CompareTo(left.Qty),
            _ => left.SortKey.CompareTo(right.SortKey)
        };
        return compared != 0 ? compared : left.SortKey.CompareTo(right.SortKey);
    }

    /// <summary>Natural Cyrillic/Latin title sort: В2 precedes В10.</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        a ??= ""; b ??= "";
        var ru = CultureInfo.GetCultureInfo("ru-RU").CompareInfo;
        var i = 0; var j = 0;
        while (i < a.Length && j < b.Length)
        {
            var digitA = char.IsDigit(a[i]);
            var digitB = char.IsDigit(b[j]);
            if (digitA && digitB)
            {
                var startI = i; var startJ = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var aa = a[startI..i].TrimStart('0');
                var bb = b[startJ..j].TrimStart('0');
                var cmp = aa.Length.CompareTo(bb.Length);
                if (cmp == 0) cmp = string.CompareOrdinal(aa, bb);
                if (cmp != 0) return cmp;
            }
            else
            {
                var startI = i; var startJ = j;
                while (i < a.Length && !char.IsDigit(a[i])) i++;
                while (j < b.Length && !char.IsDigit(b[j])) j++;
                if (i == startI || j == startJ)
                    return digitA ? -1 : 1;
                var cmp = ru.Compare(a, startI, i - startI,
                    b, startJ, j - startJ, CompareOptions.IgnoreCase);
                if (cmp != 0) return cmp;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
