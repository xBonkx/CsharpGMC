using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class ProcurementPane
{
    private bool CurrentBuyingRow(ProcurementPreviewRow row) =>
        !row.Work && row.PositionId > 0 && _allRows.Contains(row) && BuyGrid.Items.Contains(row);

    private bool CurrentBuyingSelection(IReadOnlyList<ProcurementPreviewRow> selected)
    {
        if (selected.Count == 0 || selected.Count != BuyGrid.SelectedItems.Count ||
            !BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>()
                .SequenceEqual(selected, ReferenceEqualityComparer.Instance) ||
            selected.Any(row => row.Work || row.PositionId <= 0))
            return false;
        if (selected.Count <= 32) return selected.All(CurrentBuyingRow);
        var all = _allRows.ToHashSet(ReferenceEqualityComparer.Instance);
        var visible = BuyGrid.Items.OfType<ProcurementPreviewRow>().ToHashSet(ReferenceEqualityComparer.Instance);
        return selected.All(row => all.Contains(row) && visible.Contains(row));
    }

    private bool CurrentHistoryTarget(ProcurementPreviewRow row, SqlServerProcurement.MovementHistoryRow movement,
        string revision) =>
        CurrentBuyingRow(row) && ReferenceEquals(SelectedHistoryRow(), row) &&
        Equals(MovementHistoryGrid.SelectedItem, movement) && _historyRevision == revision &&
        SqlServerProcurement.MovementDeletionError(movement, row.PositionId) is null;
}
