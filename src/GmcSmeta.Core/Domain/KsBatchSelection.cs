namespace GmcSmeta.Core.Domain;

public readonly record struct KsBatchSelectionRow(bool IsSection, long SystemId, long PositionId);

/// <summary>Resolve selected section/position rows against the authoritative act sheet.
/// The editor retains all signature, revision, confirmation and persistence guards.</summary>
public static class KsBatchSelection
{
    public static List<KsBatchPosition> Resolve(IEnumerable<KsBatchPosition> positions,
        IEnumerable<KsBatchSelectionRow> rows, IEnumerable<KsBatchSelectionRow> selection)
    {
        var byId = positions.ToDictionary(p => p.PositionId);
        var bySystem = rows.Where(r => !r.IsSection).ToLookup(r => r.SystemId, r => r.PositionId);
        var chosen = new List<long>();
        foreach (var row in selection)
        {
            if (row.IsSection) chosen.AddRange(bySystem[row.SystemId]);
            else chosen.Add(row.PositionId);
        }
        var result = new List<KsBatchPosition>();
        foreach (var id in chosen.Distinct())
            if (byId.TryGetValue(id, out var state)) result.Add(state);
        return result;
    }
}
