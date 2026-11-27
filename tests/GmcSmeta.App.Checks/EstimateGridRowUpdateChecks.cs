using System.Collections.Specialized;
using System.Windows.Controls;
using GmcSmeta.App;
using GmcSmeta.Core.Domain;

internal static class EstimateGridRowUpdateChecks
{
    internal static void Run()
    {
        CheckPendingInsertPlacement();
        foreach (var count in new[] { 0, 300, 2500, 10000, 25000 })
        {
            var rows = Enumerable.Range(1, count).Select(id =>
                new EditableEstimateRow(new EstimateRow(RowKind.Position, 1, id, id,
                    false, id.ToString(), "Воздуховод", "шт", 2m, 10m, 20m,
                    5m, false, "") { SystemId = 1 })).ToArray();
            var source = EstimateGridRowUpdates.CreateSource(rows);
            var grid = new DataGrid
            {
                AutoGenerateColumns = false, CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.FullRow
            };
            var column = new DataGridTextColumn();
            grid.Columns.Add(column);
            grid.ItemsSource = source;
            var actions = new List<NotifyCollectionChangedAction>();
            source.CollectionChanged += (_, e) => actions.Add(e.Action);

            if (!EstimateGridRowUpdates.TryReplace(grid, rows) || actions.Count != 0)
                throw new Exception("Unchanged estimate emitted a collection notification.");
            if (count == 0) continue;

            var next = rows.ToArray();
            next[0] = new EditableEstimateRow(rows[0].Source with { Qty = 3m, Amount = 30m });
            next[^1] = new EditableEstimateRow(rows[^1].Source with { Name = "Изменено" });
            grid.SelectedItems.Add(rows[0]);
            grid.SelectedItems.Add(rows[1]);
            grid.CurrentCell = new DataGridCellInfo(rows[0], column);
            if (!EstimateGridRowUpdates.TryReplace(grid, next) ||
                !ReferenceEquals(grid.ItemsSource, source) ||
                actions.Count != 2 || actions.Any(a => a != NotifyCollectionChangedAction.Replace) ||
                !source.SequenceEqual(next, ReferenceEqualityComparer.Instance) ||
                !grid.SelectedItems.Contains(next[0]) || !grid.SelectedItems.Contains(next[1]) ||
                !ReferenceEquals(grid.CurrentCell.Item, next[0]) ||
                !ReferenceEquals(grid.CurrentCell.Column, column))
                throw new Exception("Small estimate update reset grid or lost selection/current cell.");

            actions.Clear();
            var reordered = next.ToArray();
            (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
            var many = next.Select(row => new EditableEstimateRow(row.Source)).ToArray();
            if (EstimateGridRowUpdates.TryReplace(grid, reordered) ||
                EstimateGridRowUpdates.TryReplace(grid, next.Skip(1).ToArray()) ||
                EstimateGridRowUpdates.TryReplace(grid, many) ||
                actions.Count != 0 || !source.SequenceEqual(next, ReferenceEqualityComparer.Instance))
                throw new Exception("Ineligible projection mutated before fallback.");
        }
        Console.WriteLine("Estimate grid incremental readback checks OK (0–25000 rows).");
    }

    private static void CheckPendingInsertPlacement()
    {
        static EditableEstimateRow Row(int id, int system, bool heading = false, int? parent = null) =>
            new(new EstimateRow(heading ? RowKind.System : RowKind.Position,
                heading ? 0 : parent.HasValue ? 2 : 1, id, id,
                parent.HasValue, "", "", "шт", 1m, 0m, 0m, null, false, "")
            { SystemId = system, ParentPositionId = parent });

        var heading = Row(1, 1, true);
        var root = Row(10, 1);
        var component = Row(11, 1, parent: 10);
        var next = Row(12, 1);
        var otherHeading = Row(2, 2, true);
        var other = Row(20, 2);
        var visible = new[] { heading, root, component, next, otherHeading, other };
        if (EstimateView.PendingInsertIndex(visible, 1, 10) != 3 ||
            EstimateView.PendingInsertIndex(visible, 1, null) != 4 ||
            EstimateView.PendingInsertIndex(visible, 2, null) != 6 ||
            EstimateView.PendingInsertIndex(visible, 3, null) != -1 ||
            EstimateView.PendingInsertIndex(visible, 1, 20) != -1)
            throw new Exception("Pending estimate insertion misplaced a row across component/section boundaries.");

        var pending = new EditableEstimateRow(root.Source with { Id = int.MinValue })
            { IsPendingInsert = true };
        if (!pending.IsVirtual || pending.Id >= 0)
            throw new Exception("A pending position was not isolated from persisted IDs.");
    }
}
