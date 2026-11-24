namespace GmcSmeta.Core.Domain;

/// <summary>
/// Python acts.move_extra_line: only positions visible to the selected act
/// are valid neighbors/anchors; other acts' hidden extras retain their order.
/// </summary>
public static class KsExtraOrdering
{
    public sealed record Row(long Id, bool OutOfKp, bool InAct);

    public static IReadOnlyList<long> Plan(IReadOnlyList<Row> roots, long movingId,
        int direction = 0, long? anchorId = null, bool before = false)
    {
        if (roots is null) throw new ArgumentNullException(nameof(roots));
        if (roots.Select(x=>x.Id).Distinct().Count()!=roots.Count)
            throw new ArgumentException("Duplicate position identifiers.",nameof(roots));
        var moving=roots.FirstOrDefault(x=>x.Id==movingId);
        if (moving is null || !moving.OutOfKp || !moving.InAct)
            throw new InvalidOperationException("Перемещать можно только свою дополнительную строку КС.");
        var visible=roots.Where(x=>!x.OutOfKp||x.InAct).ToList();
        var index=visible.FindIndex(x=>x.Id==movingId);
        Row target;
        if (anchorId.HasValue)
        {
            target=visible.FirstOrDefault(x=>x.Id==anchorId && x.Id!=movingId)
                ?? throw new InvalidOperationException("Позиция назначения недоступна в КС.");
        }
        else
        {
            if (direction is not (-1 or 1))
                throw new ArgumentException("Нужно указать направление вверх или вниз.",nameof(direction));
            var next=index+direction;
            if (next<0||next>=visible.Count)
                return roots.Select(x=>x.Id).ToArray();
            target=visible[next];
        }
        var items=roots.ToList();
        var old=items.FindIndex(x=>x.Id==movingId);
        if (anchorId.HasValue)
        {
            items.RemoveAt(old);
            var at=items.FindIndex(x=>x.Id==target.Id);
            items.Insert(at+(before?0:1),moving);
        }
        else
        {
            // Python computes the target index before deleting the moving row.
            var at=items.FindIndex(x=>x.Id==target.Id);
            items.RemoveAt(old);
            items.Insert(at,moving);
        }
        return items.Select(x=>x.Id).ToArray();
    }
}
