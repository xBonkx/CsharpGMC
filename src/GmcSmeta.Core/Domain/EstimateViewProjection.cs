using System.Globalization;

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
    public const string ByNumber = "number";
    public const string ByUnit = "unit";
    public const string ByBasis = "basis";
    public const string ByPurchase = "purchase";
    public const string ByWork = "work";
    public const string ByWorkTotal = "workTotal";
    public const string ByNote = "note";
    public const string ByAreaUnit = "areaUnit";
    public const string ByAreaTotal = "areaTotal";
    private const string ReverseSuffix = ":reverse";

    public static string SortField(string sort) =>
        sort.EndsWith(ReverseSuffix, StringComparison.Ordinal) ? sort[..^ReverseSuffix.Length] : sort;

    public static bool IsValidSort(string sort) =>
        sort == DocumentOrder || SortField(sort) is ByName or ByAmount or ByPrice or ByQuantity
            or ByNumber or ByUnit or ByBasis or ByPurchase or ByWork or ByWorkTotal
            or ByNote or ByAreaUnit or ByAreaTotal;

    public static bool IsDescending(string sort)
    {
        var field = SortField(sort);
        var defaultDescending = field is ByAmount or ByPrice or ByQuantity;
        return defaultDescending != (field != sort);
    }

    public static string WithDirection(string field, bool descending)
    {
        if (field == DocumentOrder) return field;
        return IsDescending(field) == descending ? field : field + ReverseSuffix;
    }

    public static string NextSort(string current, string field) =>
        SortField(current) != field ? WithDirection(field, false) :
        IsDescending(current) ? DocumentOrder : WithDirection(field, true);

    internal static decimal? AreaValue(EstimateRow row, bool total)
    {
        var area = AreaCellPresentation.For(row.Name, row.Unit, row.Qty, row.AreaManual, row.IsAreaSummary);
        return decimal.TryParse(total ? area.Total : area.Unit, NumberStyles.Number,
            CultureInfo.GetCultureInfo("ru-RU"), out var value) ? value : null;
    }

    public static IReadOnlyList<EstimateRow> Apply(
        IReadOnlyList<EstimateRow> rows, string? query = null,
        bool noPriceOnly = false, string sort = DocumentOrder, bool flat = false,
        IReadOnlyDictionary<int, int>? frozenOrder = null, EstimateSearchIndex? searchIndex = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (!IsValidSort(sort))
            throw new ArgumentOutOfRangeException(nameof(sort));

        // Most opens use document order without a search or missing-price
        // filter. No intermediate hash set or list is needed for this case.
        if (string.IsNullOrWhiteSpace(query) && !noPriceOnly &&
            sort == DocumentOrder && !flat)
            return rows;

        var searchResult = EstimateSearchIndex.Apply(rows, query, searchIndex);
        // In ordinary document order the filter already returns the original
        // ordered rows, including ancestors and components. Avoid hashing and
        // scanning the full document a second time.
        if (!noPriceOnly && sort == DocumentOrder && !flat)
            return searchResult;

        // Sorting alone can consume the already ordered filter result.
        // Only the missing-price intersection needs a membership set.
        IReadOnlyList<EstimateRow> filtered = searchResult;
        if (noPriceOnly)
        {
            // Blank search already contains the entire document. Avoid a
            // full membership set in the common missing-prices-only view.
            var allowed = string.IsNullOrWhiteSpace(query) ? null
                : searchResult.Select(row => row.SortKey).ToHashSet();
            // Display an empty-price top-level position together with its
            // components and section ancestors, but do not show other priced
            // positions from the same section.
            var matched = rows.Where(row => !row.IsSystem && !row.IsSub &&
                row.HasNoPrice && (allowed is null || allowed.Contains(row.SortKey))).ToArray();
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
            if (allowed is not null) requested.IntersectWith(allowed);
            filtered = rows.Where(row => requested.Contains(row.SortKey)).ToList();
        }
        if (sort == DocumentOrder && !flat) return filtered;

        // Split into atomic position blocks (parent + its child components).
        // Keep system headers separately in their original sequence.
        var result = new List<EstimateRow>(filtered.Count);
        // A flat unsorted view simply removes section headers. The
        // original order of every parent and component stays unchanged.
        if (sort == DocumentOrder)
        {
            foreach (var row in filtered)
                if (!row.IsSystem) result.Add(row);
            return result;
        }

        if (flat)
            filtered = filtered.Where(row => !row.IsSystem).ToArray();

        // Ranges reference the existing filtered list, rather than allocating
        // one List<EstimateRow> (and backing array) for every position.
        var blocks = new List<(int Start, int Count)>();
        var blockStart = -1;
        var blockCount = 0;
        var field = SortField(sort);
        var descending = IsDescending(sort);
        var areaKeys = new EstimateAreaSortKeys(field);
        int CompareBlocks((int Start, int Count) a, (int Start, int Count) b) =>
            Compare(filtered[a.Start], filtered[b.Start], field, descending, frozenOrder, areaKeys);

        void Flush()
        {
            if (blockCount > 0) blocks.Add((blockStart, blockCount));
            blockStart = -1;
            blockCount = 0;
        }

        void OutputBlocks()
        {
            Flush();
            blocks.Sort(CompareBlocks);
            foreach (var group in blocks)
                for (var i = 0; i < group.Count; i++)
                    result.Add(filtered[group.Start + i]);
            blocks.Clear();
        }

        for (var i = 0; i < filtered.Count; i++)
        {
            var row = filtered[i];
            if (row.IsSystem)
            {
                // In flat mode headers are omitted. End the current range
                // without sorting yet so all sections can be sorted together.
                if (flat) Flush();
                else
                {
                    OutputBlocks();
                    result.Add(row);
                }
                continue;
            }

            if (row.IsSub && blockCount > 0 &&
                row.ParentPositionId == filtered[blockStart].Id)
                blockCount++;
            else
            {
                Flush();
                blockStart = i;
                blockCount = 1;
            }
        }
        OutputBlocks();
        return result;
    }

    private static int Compare(EstimateRow left, EstimateRow right, string field, bool descending,
        IReadOnlyDictionary<int, int>? frozenOrder, EstimateAreaSortKeys areaKeys)
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
        var compared = field switch
        {
            ByName => NaturalCompare(left.Name, right.Name),
            ByNumber => NaturalCompare(left.Num, right.Num),
            ByUnit => NaturalCompare(left.Unit, right.Unit),
            ByBasis => NaturalCompare(left.Basis, right.Basis),
            ByNote => NaturalCompare(left.Note, right.Note),
            ByAmount => left.Amount.CompareTo(right.Amount),
            ByPrice => left.Price.CompareTo(right.Price),
            ByQuantity => left.Qty.CompareTo(right.Qty),
            ByPurchase => Nullable.Compare(left.PurchasePrice, right.PurchasePrice),
            ByWork => Nullable.Compare(left.WorkPrice, right.WorkPrice),
            ByWorkTotal => Nullable.Compare(left.WorkPrice * left.Qty, right.WorkPrice * right.Qty),
            ByAreaUnit or ByAreaTotal => Nullable.Compare(areaKeys.Get(left), areaKeys.Get(right)),
            _ => left.SortKey.CompareTo(right.SortKey)
        };
        return compared != 0 ? (descending ? -Math.Sign(compared) : compared)
            : left.SortKey.CompareTo(right.SortKey);
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
                // Compare numeric runs in place; preserve zero trimming,
                // arbitrary length and the original ordinal digit semantics.
                while (startI < i && a[startI] == '0') startI++;
                while (startJ < j && b[startJ] == '0') startJ++;
                var aa = a.AsSpan(startI, i - startI);
                var bb = b.AsSpan(startJ, j - startJ);
                var cmp = aa.Length.CompareTo(bb.Length);
                if (cmp == 0) cmp = aa.SequenceCompareTo(bb);
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
