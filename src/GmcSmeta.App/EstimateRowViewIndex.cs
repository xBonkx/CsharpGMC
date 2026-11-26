using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Structural lookups for the current authoritative editor snapshot. Advance
/// cached maps only when their keys stay valid. Never cache editable text or money.
/// All access is on the estimate's UI thread.
/// </summary>
internal sealed class EstimateRowViewIndex(IReadOnlyList<EditableEstimateRow> rows)
{
    public IReadOnlyList<EditableEstimateRow> Rows { get; private set; } = rows;

    private Dictionary<int, EditableEstimateRow>? _bySortKey;
    private Dictionary<int, EditableEstimateRow>? _byPositionId;
    private Dictionary<int, EditableEstimateRow>? _sections;
    private HashSet<int>? _areaSummaryIds;

    public IReadOnlyDictionary<int, EditableEstimateRow> BySortKey =>
        _bySortKey ??= Rows.ToDictionary(row => row.SortKey);

    public IReadOnlyDictionary<int, EditableEstimateRow> ByPositionId =>
        _byPositionId ??= Rows.Where(row => !row.IsSystem).ToDictionary(row => row.Id);

    public IReadOnlySet<int> AreaSummaryIds =>
        _areaSummaryIds ??= Rows.Where(row => row.IsAreaSummary).Select(row => row.Id).ToHashSet();

    // Keep the original list when all SQL records are equal. A changed record
    // always gets a new model; previously published lists are never mutated.
    internal static List<EditableEstimateRow> Reconcile(
        List<EditableEstimateRow> previous, IReadOnlyList<EstimateRow> sources, bool allowReuse)
    {
        if (!allowReuse || previous.Count != sources.Count)
            return sources.Select(source => new EditableEstimateRow(source)).ToList();

        List<EditableEstimateRow>? next = null;
        for (var i = 0; i < sources.Count; i++)
        {
            if (previous[i].TryReuseAuthoritative(sources[i])) continue;
            next ??= new List<EditableEstimateRow>(previous);
            next[i] = new EditableEstimateRow(sources[i]);
        }
        return next ?? previous;
    }

    // Validate before touching any map; an incompatible snapshot must build
    // a fresh index. Limit the incremental path to small readback changes.
    internal bool TryAdvance(IReadOnlyList<EditableEstimateRow> next)
    {
        if (ReferenceEquals(Rows, next)) return true;
        if (Rows.Count != next.Count) return false;
        List<int>? changed = null;
        for (var i = 0; i < next.Count; i++)
        {
            var a = Rows[i];
            var b = next[i];
            if (ReferenceEquals(a, b)) continue;
            if (a.IsVirtual || b.IsVirtual || a.SortKey != b.SortKey || a.Id != b.Id ||
                a.Kind != b.Kind || a.Source.SystemId != b.Source.SystemId ||
                a.IsAreaSummary != b.IsAreaSummary || a.Level != b.Level ||
                a.IsSub != b.IsSub || a.ParentSystemId != b.ParentSystemId ||
                a.Source.ParentPositionId != b.Source.ParentPositionId)
                return false;
            changed ??= new List<int>();
            changed.Add(i);
            if (changed.Count > 32) return false;
        }
        if (changed is not null)
            foreach (var i in changed)
            {
                var row = next[i];
                if (_bySortKey is not null) _bySortKey[row.SortKey] = row;
                if (row.IsSystem)
                {
                    if (_sections is not null) _sections[row.Source.SystemId] = row;
                }
                else if (_byPositionId is not null) _byPositionId[row.Id] = row;
            }
        Rows = next;
        return true;
    }

    // The usual unfiltered document-order projection returns the SQL source
    // list itself. Its order matches Rows by construction; skip the hash map.
    internal IEnumerable<EditableEstimateRow> Project(
        IReadOnlyList<EstimateRow> projected, IReadOnlyList<EstimateRow> documentRows)
    {
        if (ReferenceEquals(projected, documentRows)) return Rows;
        var byKey = BySortKey;
        return projected.Select(row => byKey[row.SortKey]);
    }

    // Call before publishing each visible row, even if it is reused from a
    // different grouping. Hidden rows need no presentation update until they
    // return. Structural Level/Num remain authoritative for all rows.
    public void PrepareDocumentRow(EditableEstimateRow row, bool flat)
    {
        var number = flat && !row.IsSystem
            ? SectionName(row.Source.SystemId, row.Num)
            : row.Num;
        var depth = flat ? (row.IsSub ? 1 : 0) : row.Level;
        if (row.DisplayNum != number) row.DisplayNum = number;
        if (row.DisplayIndentLevel != depth) row.DisplayIndentLevel = depth;
    }

    public EditableEstimateRow? FindSection(int systemId)
    {
        _sections ??= Rows.Where(row => row.IsSystem)
            .ToDictionary(row => row.Source.SystemId);
        return _sections.GetValueOrDefault(systemId);
    }

    public string SectionName(int systemId, string fallback) =>
        FindSection(systemId)?.Name ?? fallback;
}
