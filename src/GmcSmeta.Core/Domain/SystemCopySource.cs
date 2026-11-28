namespace GmcSmeta.Core.Domain;

/// <summary>Resolve a real selected position/header against authoritative system rows.</summary>
public static class SystemCopySource
{
    public static bool CanReceive(DocRow document, int objectId) =>
        document.ObjectId == objectId && !document.Locked && !document.IsStage &&
        !document.StageRest && (document.Kind is "kp" or "main" or "dop");

    public sealed record Section(long Id, long? ParentId);

    // Reproduce Python builder._groups/_flat: all subsections follow their root.
    public static IReadOnlyList<long> InsertCopyOrder(IReadOnlyList<Section> existing,
        long? anchorId, long newId, IReadOnlyList<long> newChildren, bool subsection)
    {
        if (existing.Select(s => s.Id).Distinct().Count() != existing.Count ||
            existing.Any(s => s.Id == newId || newChildren.Contains(s.Id)) ||
            newChildren.Contains(newId) || newChildren.Distinct().Count() != newChildren.Count)
            throw new InvalidOperationException("Некорректные идентификаторы разделов.");
        var roots = existing.Where(s => s.ParentId is null).ToArray();
        var rootIds = roots.Select(s => s.Id).ToHashSet();
        if (existing.Any(s => s.ParentId is long p && !rootIds.Contains(p)))
            throw new InvalidOperationException("Некорректная структура подразделов.");
        if (subsection && (anchorId is not long anchor ||
            !existing.Any(s => s.Id == anchor && s.ParentId is not null) || newChildren.Count != 0))
            throw new InvalidOperationException("Подраздел-источник недоступен.");
        if (!subsection && anchorId is long root && !rootIds.Contains(root))
            throw new InvalidOperationException("Система-источник недоступна.");

        var result = new List<long>();
        foreach (var group in roots)
        {
            result.Add(group.Id);
            foreach (var child in existing.Where(s => s.ParentId == group.Id))
            {
                result.Add(child.Id);
                if (subsection && anchorId == child.Id) result.Add(newId);
            }
            if (!subsection && anchorId == group.Id)
            {
                result.Add(newId);
                result.AddRange(newChildren);
            }
        }
        if (anchorId is null)
        {
            result.Add(newId);
            result.AddRange(newChildren);
        }
        return result;
    }

    public static int? Resolve(EstimateRow? selected, IEnumerable<EstimateRow> systems)
    {
        if (selected is null || selected.SystemId <= 0) return null;
        return systems.Any(row => row.IsSystem && row.SystemId == selected.SystemId)
            ? selected.SystemId : null;
    }
}
