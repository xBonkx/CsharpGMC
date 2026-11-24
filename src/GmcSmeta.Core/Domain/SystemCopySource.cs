namespace GmcSmeta.Core.Domain;

/// <summary>Resolve a real selected position/header against authoritative system rows.</summary>
public static class SystemCopySource
{
    public static int? Resolve(EstimateRow? selected, IEnumerable<EstimateRow> systems)
    {
        if (selected is null || selected.SystemId <= 0) return null;
        return systems.Any(row => row.IsSystem && row.SystemId == selected.SystemId)
            ? selected.SystemId : null;
    }
}
