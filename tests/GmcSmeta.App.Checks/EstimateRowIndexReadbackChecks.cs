using GmcSmeta.App;
using GmcSmeta.Core.Domain;

internal static class EstimateRowIndexReadbackChecks
{
    internal static void Run()
    {
        foreach (var count in new[] { 0, 300, 2500, 10000, 25000 })
        {
            var sources = Enumerable.Range(1, count).Select(id =>
                new EstimateRow(RowKind.Position, 1, id, id, false, id.ToString(),
                    "Воздуховод", "шт", 2m, 10m, 20m, 5m, false, "")
                { SystemId = 7, IsAreaSummary = id == 10 }).ToList();
            sources.Insert(0, new EstimateRow(RowKind.System, 0, 0, 0, false, "",
                "П1", "", 0m, 0m, 0m, null, false, "") { SystemId = 7 });
            var rows = EstimateRowViewIndex.Reconcile(new(), sources, true);
            var index = new EstimateRowViewIndex(rows);
            if (!ReferenceEquals(index.Project(sources, sources), rows))
                throw new Exception("Document order must use existing editor rows directly.");
            var keys = index.BySortKey;
            var ids = index.ByPositionId;
            var areas = index.AreaSummaryIds;
            _ = index.SectionName(7, "?");

            rows[0].Name = "Optimistic";
            var equal = EstimateRowViewIndex.Reconcile(rows, sources.Select(s => s with { }).ToArray(), true);
            if (!ReferenceEquals(equal, rows) || equal[0].Name != "П1" || !index.TryAdvance(equal))
                throw new Exception("Equal readback should reuse list and restore optimistic text.");

            var changed = sources.ToArray();
            changed[0] = changed[0] with { Name = "П2" };
            if (count != 0) changed[^1] = changed[^1] with { Qty = 3m, Amount = 30m };
            var next = EstimateRowViewIndex.Reconcile(rows, changed, true);
            if (ReferenceEquals(next, rows) || rows[0].Name != "П1" ||
                !index.TryAdvance(next) || !ReferenceEquals(index.Rows, next) ||
                !ReferenceEquals(keys, index.BySortKey) || !ReferenceEquals(ids, index.ByPositionId) ||
                !ReferenceEquals(areas, index.AreaSummaryIds) || index.SectionName(7, "?") != "П2" ||
                !ReferenceEquals(index.FindSection(7), next[0]) || index.FindSection(99) is not null ||
                !ReferenceEquals(keys[0], next[0]))
                throw new Exception("Small readback did not advance existing indexes.");
            if (count != 0 && (!ReferenceEquals(ids[count], next[^1]) ||
                !ReferenceEquals(keys[count], next[^1]) || next[^1].Source.Amount != 30m))
                throw new Exception("Position lookup retained the previous authoritative model.");
            if (!index.Project(changed.Reverse().ToArray(), changed).SequenceEqual(next.AsEnumerable().Reverse()))
                throw new Exception("Reordered projection lost SortKey mapping.");

            var structural = changed.ToArray();
            structural[^1] = structural[^1] with { IsAreaSummary = !structural[^1].IsAreaSummary };
            var rejected = EstimateRowViewIndex.Reconcile(next, structural, true);
            if (index.TryAdvance(rejected) || !ReferenceEquals(index.Rows, next) ||
                !ReferenceEquals(keys[changed[^1].SortKey], next[^1]))
                throw new Exception("Incompatible readback mutated an existing index.");
            var rebuilt = new EstimateRowViewIndex(rejected);
            if (!rebuilt.AreaSummaryIds.SetEquals(rejected.Where(r => r.IsAreaSummary).Select(r => r.Id)))
                throw new Exception("Rebuilt index has incorrect area membership.");

            var forced = EstimateRowViewIndex.Reconcile(next, changed, false);
            if (forced.Where((r, i) => ReferenceEquals(r, next[i])).Any() ||
                index.TryAdvance(next.Skip(1).ToArray()))
                throw new Exception("Forced rebuild or count-change guard failed.");
            if (count > 32)
            {
                var many = EstimateRowViewIndex.Reconcile(next,
                    changed.Select(s => s with { Name = s.Name + "!" }).ToArray(), true);
                if (index.TryAdvance(many) || !ReferenceEquals(index.Rows, next))
                    throw new Exception("Large readback must fall back to a fresh index.");
            }
        }
        CheckSelectionIdentity();
        CheckSelectionDelta();
        Console.WriteLine("Estimate row index incremental readback OK (0–25000 positions).");
    }

    private static void CheckSelectionIdentity()
    {
        EditableEstimateRow Position(int id, int sort) => new(new EstimateRow(
            RowKind.Position, 1, sort, id, false, "", "Позиция", "шт", 1, 1, 1, null, false, ""));
        EditableEstimateRow Section(int id, int sort) => new(new EstimateRow(
            RowKind.System, 0, sort, 0, false, "", "Система", "", 0, 0, 0, null, false, "")
            { SystemId = id });
        EditableEstimateRow Group(string key, int id) => new(new EstimateRow(
            RowKind.Position, 0, id, id, false, "", "Группа", "шт", 2, 1, 2, null, false, ""))
            { GroupKey = key, GroupMemberIds = new long[] { 1, 2 } };

        var position = Position(1, 1);
        var system = Section(7, 0);
        var group = Group("same", -1);
        var removed = Position(3, 3);
        var nextPosition = Position(1, 20);
        var nextSystem = Section(7, 9);
        var nextGroup = Group("same", -9);
        var unrelated = Group("other", -1);
        var result = EstimateSelectionRestore.Find(
            new[] { unrelated, nextSystem, nextGroup, nextPosition },
            new[] { position, system, group, removed }, group, position);
        if (!result.Selected.SequenceEqual(new[] { nextPosition, nextSystem, nextGroup }) ||
            !ReferenceEquals(result.Primary, nextGroup) || !ReferenceEquals(result.Current, nextPosition))
            throw new Exception("Full estimate rebind lost or misidentified the selected rows.");
    }


    private static void CheckSelectionDelta()
    {
        var a = new object();
        var b = new object();
        var c = new object();
        var d = new object();
        var selected = new System.Collections.ObjectModel.ObservableCollection<object> { a, b, c };
        var events = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        selected.CollectionChanged += (_, e) => events.Add(e.Action);
        GridSelectionDelta.Restore(selected, new[] { a, b, c });
        if (events.Count != 0) throw new Exception("Unchanged selection must not notify.");
        GridSelectionDelta.Restore(selected, new[] { b, c, d, d });
        if (!selected.SequenceEqual(new[] { b, c, d }) || events.Count != 2 ||
            events.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Reset))
            throw new Exception("Selection delta must preserve surviving row instances.");
        events.Clear();
        GridSelectionDelta.Restore(selected, new[] { a });
        if (!selected.SequenceEqual(new[] { a }) || events.Count != 2)
            throw new Exception("Disjoint selection must clear once and add the target.");
        GridSelectionDelta.Restore(selected, Array.Empty<object>());
        if (selected.Count != 0) throw new Exception("Empty selection must clear.");
    }

}
