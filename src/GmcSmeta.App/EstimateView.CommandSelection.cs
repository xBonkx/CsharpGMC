using System.Windows;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    // Commands routed from the menu, toolbar and keyboard share the same
    // real-row rules. Never silently filter a mixed selection into a write.
    private bool IsRealCommandRow(EditableEstimateRow row) =>
        !row.IsVirtual && !row.IsAreaSummary && row.Source.SystemId > 0 &&
        (row.IsSystem || row.Id > 0) && _rows.Contains(row);

    private bool IsRealCommandSelection(IReadOnlyList<EditableEstimateRow> rows)
    {
        if (rows.Count == 0 || rows.Any(row => row.IsVirtual || row.IsAreaSummary ||
            row.Source.SystemId <= 0 || (!row.IsSystem && row.Id <= 0))) return false;
        // A 2500-row selection should not perform 2500 full-list scans.
        if (rows.Count <= 32) return rows.All(_rows.Contains);
        var current = _rows.ToHashSet(ReferenceEqualityComparer.Instance);
        return rows.All(current.Contains);
    }

    private bool CommandSelectionCurrent(IReadOnlyList<EditableEstimateRow> rows) =>
        IsRealCommandSelection(rows) && EstimateGrid.SelectedItems.Count == rows.Count &&
        EstimateGrid.SelectedItems.OfType<EditableEstimateRow>()
            .SequenceEqual(rows, ReferenceEqualityComparer.Instance);

    private bool ActiveEstimateWriter() => !_detached && IsLoaded && IsEnabled && !_doc.StageRest &&
        Window.GetWindow(this) is MainWindow main &&
        main.IsCurrentEstimatePage(_contractId, _objectId, this) &&
        main.CanEditEstimateHeader(_contractId, _objectId);

    private bool CommandWriteCurrent(EstimateDocument snapshot, string revision) =>
        ActiveEstimateWriter() && !_serverConflict && !_cellSaveQueue.Blocked &&
        _saveError is null && ReferenceEquals(snapshot, _estimate) && !snapshot.Locked &&
        revision == _serverRevision && revision == _publishedEstimateRevision &&
        !string.IsNullOrWhiteSpace(revision);
}
