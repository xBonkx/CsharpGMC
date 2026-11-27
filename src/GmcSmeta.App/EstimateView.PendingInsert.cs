using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    // One optimistic structural insertion at a time. This view-only object
    // must not enter the authoritative snapshot, ID map, formulas or SQL.
    private sealed class PendingEstimateInsert(EditableEstimateRow row, int? afterRootId)
    {
        public EditableEstimateRow Row { get; } = row;
        public int? AfterRootId { get; } = afterRootId;
        public int? ServerId { get; set; }
        public bool Failed { get; set; }
        public Dictionary<string, string> BufferedEdits { get; } = new();
    }

    private PendingEstimateInsert? _pendingEstimateInsert;

    private bool CanShowPendingInsert => !_inlineStagesOn &&
        _temporaryGroupMode == EstimateViewProjection.DocumentOrder &&
        _temporarySort == EstimateViewProjection.DocumentOrder &&
        !_flatTemporaryView && !_noPriceTemporaryView &&
        string.IsNullOrWhiteSpace(EstimateSearch.Text);

    private PendingEstimateInsert? TryShowPendingInsert(EditableEstimateRow anchor, bool component)
    {
        if (!CanShowPendingInsert || anchor.IsAreaSummary ||
            EstimateGrid.ItemsSource is not ObservableCollection<EditableEstimateRow> visible)
            return null;

        var section = _rows.FirstOrDefault(row => row.IsSystem &&
            row.Source.SystemId == anchor.Source.SystemId);
        if (section is null || section.IsCollapsed) return null;
        var rootId = anchor.IsSystem ? (int?)null : anchor.Source.ParentPositionId ?? anchor.Id;
        var draft = new EditableEstimateRow(new EstimateRow(
            RowKind.Position, section.Level + (component ? 2 : 1), anchor.SortKey,
            int.MinValue, component, "…", "", "шт", 1m, 0m, 0m, null, false, "")
        {
            SystemId = anchor.Source.SystemId,
            ParentSystemId = section.Source.ParentSystemId,
            ParentPositionId = component ? rootId : null
        }) { IsPendingInsert = true };

        var pending = new PendingEstimateInsert(draft, rootId);
        var index = PendingInsertIndex(visible, pending);
        if (index < 0) return null;
        _pendingEstimateInsert = pending;
        // One collection Add, not ItemsSource reassignment: keep the scroll,
        // realized rows and column layout intact.
        visible.Insert(index, draft);
        EstimateGrid.SelectedItem = draft;
        EstimateGrid.CurrentCell = new DataGridCellInfo(draft, NameColumn);
        EstimateGrid.ScrollIntoView(draft, NameColumn);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_detached || _serverConflict || !ReferenceEquals(_pendingEstimateInsert, pending) ||
                !ReferenceEquals(EstimateGrid.SelectedItem, draft) ||
                !EstimateGrid.IsVisible || !EstimateGrid.Items.Contains(draft)) return;
            EstimateGrid.Focus();
            EstimateGrid.CurrentCell = new DataGridCellInfo(draft, NameColumn);
            EstimateGrid.BeginEdit();
        }), DispatcherPriority.Loaded);
        return pending;
    }

    private static int PendingInsertIndex(IEnumerable<EditableEstimateRow> visible, PendingEstimateInsert pending)
    {
        var index = 0;
        var last = -1;
        foreach (var row in visible)
        {
            if (row.Source.SystemId == pending.Row.Source.SystemId &&
                !row.IsVirtual &&
                (pending.AfterRootId is not int root ||
                    !row.IsSystem && (row.Id == root || row.Source.ParentPositionId == root)))
                last = index;
            index++;
        }
        return last < 0 ? -1 : last + 1;
    }

    private void IncludePendingInsert(List<EditableEstimateRow> visible)
    {
        if (_pendingEstimateInsert is not { } pending || !CanShowPendingInsert ||
            !visible.Any(row => row.IsSystem && row.Source.SystemId == pending.Row.Source.SystemId &&
                !row.IsCollapsed)) return;
        var index = PendingInsertIndex(visible, pending);
        if (index >= 0) visible.Insert(index, pending.Row);
    }

    private bool CanEditPendingInsert(EditableEstimateRow row, string? field) =>
        _pendingEstimateInsert is { Failed: false } pending &&
        ReferenceEquals(pending.Row, row) && !_serverConflict && _estimate?.Locked == false &&
        (field is "name" or "unit" or "qty" or "note" ||
            !row.IsSub && (field is "price" or "purchase_price" ||
                field == "work_price" && _estimate.SmrMode == "pos"));

    private bool CommitPendingInsertCell(EditableEstimateRow row, string field, string input)
    {
        if (_pendingEstimateInsert is not { } pending || !ReferenceEquals(pending.Row, row))
            return false;
        // Cell-picking formulas require the authoritative row/revision. Plain
        // text and numeric inputs can safely be buffered while SQL creates it.
        if (field is ("qty" or "price" or "purchase_price" or "work_price") &&
            input.TrimStart().StartsWith("="))
        {
            SetStatus("Формулы доступны после сверки новой позиции с сервером.", true);
            return false;
        }
        if (pending.Failed || pending.ServerId is null)
            pending.BufferedEdits[field] = input;
        else
            QueueSave(pending.ServerId.Value, field, input);
        return true;
    }

    // Only the accepted SQL snapshot may replace the local draft. Transfer
    // focus by the real identity; don't steal focus if the user moved away.
    private void ReconcilePendingInsert(EstimateDocument document)
    {
        if (_pendingEstimateInsert is not { } pending) return;
        if (pending.ServerId is int id && document.Rows.Any(r => !r.IsSystem && r.Id == id))
        {
            if (ReferenceEquals(EstimateGrid.SelectedItem, pending.Row))
            {
                _focusAfterReload = id;
                _focusSystemAfterReload = null;
                _editAfterReload = false;
            }
        }
        else if (pending.Failed && pending.BufferedEdits.Count > 0)
        {
            _saveError = (_saveError ?? "Новая позиция не подтверждена") +
                ". Введённые данные: " + string.Join("; ", pending.BufferedEdits.Select(x => x.Key + ": " + x.Value));
        }
        _pendingEstimateInsert = null;
    }
}
