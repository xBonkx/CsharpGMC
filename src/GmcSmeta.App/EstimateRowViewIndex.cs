namespace GmcSmeta.App;

/// <summary>
/// Structural lookups for one authoritative editor row snapshot. Create a new
/// index when the row list is replaced. Never cache editable text or money.
/// All access is on the estimate's UI thread.
/// </summary>
internal sealed class EstimateRowViewIndex(IReadOnlyList<EditableEstimateRow> rows)
{
    public IReadOnlyList<EditableEstimateRow> Rows { get; } = rows;

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

    public string SectionName(int systemId, string fallback)
    {
        _sections ??= Rows.Where(row => row.IsSystem)
            .ToDictionary(row => row.Source.SystemId);
        return _sections.TryGetValue(systemId, out var section) ? section.Name : fallback;
    }
}
