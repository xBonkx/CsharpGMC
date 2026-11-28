namespace GmcSmeta.Core.Domain;

/// <summary>One-system, two-level hierarchy. Planning never changes financial fields.</summary>
public static class IndentHierarchyPlan
{
    public sealed record Row(long Id, long? ParentId, long Order, bool Normal);
    public sealed record Result(long? OldParentId, long? NewParentId, IReadOnlyList<long> OrderedIds);

    public static Result Build(IEnumerable<Row> source, long positionId, bool makeSub)
    {
        var rows = source.OrderBy(row => row.Order).ThenBy(row => row.Id).ToArray();
        if (rows.Any(row => row.Id <= 0) || rows.Select(row => row.Id).Distinct().Count() != rows.Length)
            throw new InvalidOperationException("Некорректные ID позиций.");
        var byId = rows.ToDictionary(row => row.Id);
        foreach (var row in rows)
            if (row.ParentId is long parent &&
                (!byId.TryGetValue(parent, out var owner) || owner.ParentId is not null))
                throw new InvalidOperationException("Нарушена структура комплектующих.");
        if (!byId.TryGetValue(positionId, out var selected) || !selected.Normal)
            throw new InvalidOperationException("Выберите обычную позицию.");
        if (selected.ParentId is long oldParent && !byId[oldParent].Normal)
            throw new InvalidOperationException("Комплектующее защищённой строки нельзя отделить.");
        long? nextParent = null;
        if (makeSub)
        {
            if (selected.ParentId is not null || rows.Any(row => row.ParentId == positionId))
                throw new InvalidOperationException("Строка уже компонент или содержит комплектующие.");
            var above = rows.TakeWhile(row => row.Id != positionId).LastOrDefault(row => row.ParentId is null);
            if (above is null || !above.Normal)
                throw new InvalidOperationException("Нет обычной строки выше для вложения.");
            nextParent = above.Id;
        }
        else if (selected.ParentId is null)
            throw new InvalidOperationException("Строка уже верхнего уровня.");

        var projected = rows.Select(row => row.Id == positionId ? row with { ParentId = nextParent } : row).ToArray();
        var children = projected.Where(row => row.ParentId is not null).ToLookup(row => row.ParentId!.Value);
        var order = projected.Where(row => row.ParentId is null)
            .SelectMany(row => new[] { row.Id }.Concat(children[row.Id].Select(child => child.Id))).ToArray();
        if (order.Length != rows.Length)
            throw new InvalidOperationException("Не все позиции вошли в новую структуру.");
        return new(selected.ParentId, nextParent, order);
    }
}
