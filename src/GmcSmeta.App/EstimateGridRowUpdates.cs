using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Controls;

namespace GmcSmeta.App;

/// <summary>
/// Publish small authoritative replacements without resetting a normal estimate
/// grid. Only same-identity, same-order projections qualify; never mutate Source.
/// </summary>
internal static class EstimateGridRowUpdates
{
    private const int MaxReplacements = 32;

    internal static ObservableCollection<EditableEstimateRow> CreateSource(
        IEnumerable<EditableEstimateRow> rows) => new(rows);

    internal static bool TryReplace(DataGrid grid, IReadOnlyList<EditableEstimateRow> next)
    {
        if (grid.ItemsSource is not ObservableCollection<EditableEstimateRow> existing ||
            existing.Count != next.Count || grid.Items.SortDescriptions.Count != 0 ||
            grid.Items.GroupDescriptions?.Count > 0 ||
            grid.Items is IEditableCollectionView { IsEditingItem: true } ||
            grid.SelectedItems.Count > MaxReplacements)
            return false;

        // Validate the entire sequence before touching the collection. A
        // change in membership, order, hierarchy or a large batch falls back
        // to the existing full rebind path.
        List<int>? changed = null;
        for (var i = 0; i < next.Count; i++)
        {
            var old = existing[i];
            var row = next[i];
            if (ReferenceEquals(old, row)) continue;
            if (!SameIdentity(old, row)) return false;
            changed ??= new List<int>();
            changed.Add(i);
            if (changed.Count > MaxReplacements) return false;
        }
        if (changed is null) return true;

        var replacements = changed.ToDictionary(i => existing[i], i => next[i]);
        EditableEstimateRow Map(EditableEstimateRow row) =>
            replacements.TryGetValue(row, out var updated) ? updated : row;

        var selection = grid.SelectedItems.OfType<EditableEstimateRow>().Select(Map).ToArray();
        var current = grid.CurrentCell;
        var currentRow = current.IsValid && current.Item is EditableEstimateRow item ? Map(item) : null;
        foreach (var i in changed)
            existing[i] = next[i]; // NotifyCollectionChangedAction.Replace, never Reset.

        // WPF may drop selected/current items when their model is replaced.
        // Restore only the lost selection and retain the original column.
        foreach (var row in selection)
            if (!grid.SelectedItems.Contains(row)) grid.SelectedItems.Add(row);
        if (currentRow is not null && current.Column is not null &&
            (!ReferenceEquals(grid.CurrentCell.Item, currentRow) ||
             !ReferenceEquals(grid.CurrentCell.Column, current.Column)))
            grid.CurrentCell = new DataGridCellInfo(currentRow, current.Column);
        return true;
    }

    private static bool SameIdentity(EditableEstimateRow a, EditableEstimateRow b) =>
        !a.IsVirtual && !b.IsVirtual && a.Id == b.Id && a.SortKey == b.SortKey &&
        a.Kind == b.Kind && a.IsSub == b.IsSub && a.Level == b.Level && a.ParentSystemId == b.ParentSystemId &&
        a.Source.SystemId == b.Source.SystemId &&
        a.Source.ParentPositionId == b.Source.ParentPositionId &&
        a.IsAreaSummary == b.IsAreaSummary;
}
