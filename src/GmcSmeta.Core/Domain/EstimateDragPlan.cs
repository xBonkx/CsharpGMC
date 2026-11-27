namespace GmcSmeta.Core.Domain;

/// <summary>
/// Pure, deterministic version of builder.place_positions/place_group/
/// place_subsection, with complete hierarchy validation before SQL writes.
/// All writes are later checked again inside one SQL Server transaction.
/// </summary>
public static class EstimateDragPlan
{
    public const int MaxPositions = 10000;

    public sealed record Section(long Id, long? ParentId, int Order, string Name);
    public sealed record Position(long Id, long SystemId, long? ParentId, int Order);
    public sealed record PositionAssignment(long Id, long SystemId, int Order);
    public sealed record SectionAssignment(long Id, long? ParentId, int Order);

    public sealed record PositionsResult(
        IReadOnlyList<PositionAssignment> Assignments,
        IReadOnlyList<long> MovedRoots, bool Changed);

    public sealed record SectionsResult(
        IReadOnlyList<SectionAssignment> Assignments, bool Changed);

    public static PositionsResult PlacePositions(
        IReadOnlyCollection<Position> source,
        IReadOnlyCollection<Section> sections,
        IReadOnlyList<long> selected,
        long targetSystem,
        long? afterPosition)
    {
        ValidateSections(sections);
        if (source is null || source.Count > MaxPositions ||
            selected is null || selected.Count == 0 ||
            selected.Count > MaxPositions || targetSystem <= 0)
            throw new ArgumentException("Некорректное выделение строк КП.");
        if (!sections.Any(s => s.Id == targetSystem))
            throw new InvalidOperationException("Целевой раздел не принадлежит документу.");

        var all = source.ToDictionary(p => p.Id);
        if (all.Count != source.Count ||
            source.Any(p => p.Id <= 0 || p.SystemId <= 0 ||
                            !sections.Any(s => s.Id == p.SystemId)) ||
            selected.Any(id => id <= 0 || !all.ContainsKey(id)))
            throw new InvalidOperationException("Список позиций изменился или содержит чужие ID.");
        if (selected.Distinct().Count() != selected.Count)
            throw new InvalidOperationException("Повторяющаяся позиция в выделении.");

        var roots = new Dictionary<long, List<Position>>();
        var children = new Dictionary<long, List<Position>>();
        foreach (var system in sections)
            roots[system.Id] = new List<Position>();
        foreach (var row in source)
        {
            if (row.ParentId is null) roots[row.SystemId].Add(row);
            else
            {
                if (!all.TryGetValue(row.ParentId.Value, out var parent) ||
                    parent.ParentId is not null || parent.SystemId != row.SystemId)
                    throw new InvalidOperationException("Нарушена структура комплектующих.");
                if (!children.TryGetValue(parent.Id, out var bucket))
                    children[parent.Id] = bucket = new List<Position>();
                bucket.Add(row);
            }
        }
        foreach (var part in roots.Values)
            part.Sort((a, b) => a.Order != b.Order
                ? a.Order.CompareTo(b.Order) : a.Id.CompareTo(b.Id));
        foreach (var part in children.Values)
            part.Sort((a, b) => a.Order != b.Order
                ? a.Order.CompareTo(b.Order) : a.Id.CompareTo(b.Id));

        // Python place_positions: dragging a component moves its root and
        // all siblings. Preserve on-screen selection order, once per root.
        var moveRoots = selected.Select(id => all[id].ParentId ?? id)
            .Distinct().ToArray();
        var moving = moveRoots.ToHashSet();
        long? anchorRoot = null;
        if (afterPosition is long anchor)
        {
            if (!all.TryGetValue(anchor, out var a))
                throw new InvalidOperationException("Строка назначения удалена.");
            anchorRoot = a.ParentId ?? a.Id;
            if (a.SystemId != targetSystem)
                throw new InvalidOperationException("Строка назначения не в выбранном разделе.");
            if (moving.Contains(anchorRoot.Value))
                throw new InvalidOperationException("Нельзя переместить позиции после самих себя.");
        }

        var newRoots = roots.ToDictionary(x => x.Key, x => x.Value
            .Where(p => !moving.Contains(p.Id)).ToList());
        var destination = newRoots[targetSystem];
        var index = anchorRoot is long after
            ? destination.FindIndex(p => p.Id == after) + 1 : 0;
        if (index < 0 || index > destination.Count ||
            anchorRoot is not null && index == 0)
            throw new InvalidOperationException("Якорная строка больше не существует.");
        destination.InsertRange(index, moveRoots.Select(id => all[id]));

        var newOrder = new List<PositionAssignment>(source.Count);
        foreach (var sys in sections)
        {
            var sequence = newRoots[sys.Id];
            var rank = 0;
            foreach (var root in sequence)
            {
                newOrder.Add(new PositionAssignment(root.Id, sys.Id, rank++));
                if (children.TryGetValue(root.Id, out var subs))
                    foreach (var child in subs)
                        newOrder.Add(new PositionAssignment(child.Id, sys.Id, rank++));
            }
        }
        if (newOrder.Count != source.Count)
            throw new InvalidOperationException("При построении порядка потеряна строка.");
        var changed = newOrder.Any(x =>
        {
            var prior = all[x.Id];
            return prior.SystemId != x.SystemId || prior.Order != x.Order;
        });
        return new PositionsResult(newOrder, moveRoots, changed);
    }

    public static bool ChangesPositionSystems(IReadOnlyCollection<Position> source, PositionsResult plan)
    {
        var systems = source.ToDictionary(p => p.Id, p => p.SystemId);
        return plan.Assignments.Count != systems.Count ||
            plan.Assignments.Any(p => !systems.TryGetValue(p.Id, out var system) || system != p.SystemId);
    }

    public static SectionsResult PlaceGroup(
        IReadOnlyCollection<Section> source, long movingSystem, long? afterSystem)
    {
        var groups = Groups(source);
        if (!groups.SelectMany(x => x).Any(x => x.Id == movingSystem))
            throw new InvalidOperationException("Перетаскиваемая система не найдена.");
        var byId = source.ToDictionary(x => x.Id);
        var movingRoot = byId[movingSystem].ParentId ?? movingSystem;
        long? afterRoot = null;
        if (afterSystem is long id)
        {
            if (!byId.TryGetValue(id, out var row))
                throw new InvalidOperationException("Система назначения удалена.");
            afterRoot = row.ParentId ?? row.Id;
            if (afterRoot == movingRoot)
                throw new InvalidOperationException("Нельзя переместить систему после себя.");
        }

        var group = groups.Single(x => x[0].Id == movingRoot);
        groups.Remove(group);
        var index = afterRoot is long after
            ? groups.FindIndex(x => x[0].Id == after) + 1 : 0;
        if (afterRoot is not null && index == 0)
            throw new InvalidOperationException("Система назначения исчезла.");
        groups.Insert(index, group);
        return ToSectionResult(source, groups);
    }

    public static SectionsResult PlaceSubsection(
        IReadOnlyCollection<Section> source, long subsectionId,
        long targetParent, long? afterSubsection)
    {
        var groups = Groups(source);
        var byId = source.ToDictionary(x => x.Id);
        if (!byId.TryGetValue(subsectionId, out var subsection) ||
            subsection.ParentId is null)
            throw new InvalidOperationException("Перетаскивать как подраздел можно только подраздел.");
        if (!byId.TryGetValue(targetParent, out var target))
            throw new InvalidOperationException("Целевая система удалена.");
        targetParent = target.ParentId ?? target.Id;
        if (afterSubsection == subsectionId)
            throw new InvalidOperationException("Нельзя поставить подраздел после самого себя.");

        var src = groups.Single(x => x.Skip(1).Any(y => y.Id == subsectionId));
        src.RemoveAll(x => x.Id == subsectionId);
        var dst = groups.Single(x => x[0].Id == targetParent);
        var index = 1;
        if (afterSubsection is long anchor)
        {
            index = dst.FindIndex(x => x.Id == anchor) + 1;
            if (index <= 1)
                throw new InvalidOperationException("Якорный подраздел не существует в этой системе.");
        }
        dst.Insert(index, subsection with { ParentId = targetParent });
        return ToSectionResult(source, groups);
    }

    /// <summary>
    /// Whether a planned section drop changes its parent (financial grouping).
    /// Reordering inside the same parent is safe with existing linked documents;
    /// reparenting requires a SQL Server dependency check.
    /// </summary>
    public static bool ReparentsSection(IReadOnlyCollection<Section> source,
        SectionsResult plan, long sectionId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        var before = source.Single(x => x.Id == sectionId);
        var after = plan.Assignments.Single(x => x.Id == sectionId);
        return before.ParentId != after.ParentId;
    }

    private static List<List<Section>> Groups(IReadOnlyCollection<Section> sections)
    {
        ValidateSections(sections);
        var roots = sections.Where(x => x.ParentId is null)
            .OrderBy(x => x.Order).ThenBy(x => x.Id).ToArray();
        return roots.Select(root =>
        {
            var group = new List<Section> { root };
            group.AddRange(sections.Where(x => x.ParentId == root.Id)
                .OrderBy(x => x.Order).ThenBy(x => x.Id));
            return group;
        }).ToList();
    }

    private static void ValidateSections(IReadOnlyCollection<Section> sections)
    {
        if (sections is null || sections.Count == 0 ||
            sections.Count > MaxPositions ||
            sections.Any(x => x.Id <= 0) ||
            sections.Select(x => x.Id).Distinct().Count() != sections.Count)
            throw new InvalidOperationException("Список систем пуст или повреждён.");
        var byId = sections.ToDictionary(x => x.Id);
        foreach (var system in sections.Where(x => x.ParentId is long))
        {
            var id = system.ParentId!.Value;
            if (!byId.TryGetValue(id, out var parent) || parent.ParentId is not null)
                throw new InvalidOperationException("Подраздел привязан к чужой системе.");
        }
    }

    private static SectionsResult ToSectionResult(
        IReadOnlyCollection<Section> source, List<List<Section>> groups)
    {
        var original = source.ToDictionary(x => x.Id);
        var flat = new List<SectionAssignment>();
        foreach (var group in groups)
            foreach (var item in group)
                flat.Add(new SectionAssignment(item.Id, item.ParentId, flat.Count));
        if (flat.Count != source.Count)
            throw new InvalidOperationException("При переносе потеряна система.");
        return new SectionsResult(flat, flat.Any(x =>
        {
            var before = original[x.Id];
            return before.Order != x.Order || before.ParentId != x.ParentId;
        }));
    }
}
