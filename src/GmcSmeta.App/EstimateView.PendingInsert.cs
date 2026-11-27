using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    // Local drafts are isolated from the authoritative snapshot, ID map and SQL.
    // Each draft has its own identity and buffered edits.
    private sealed class PendingEstimateInsert(EditableEstimateRow row, int? afterRootId)
    {
        public EditableEstimateRow Row { get; } = row;
        public int? AfterRootId { get; } = afterRootId;
        public int? ServerId { get; set; }
        public bool Failed { get; set; }
        public Dictionary<string, string> BufferedEdits { get; } = new();
        public Dictionary<string, string> EnteredEdits { get; } = new();
    }

    private readonly List<PendingEstimateInsert> _pendingEstimateInserts = new();
    private int _nextPendingInsertId = -1;
    private Task _pendingInsertTail = Task.CompletedTask;

    private PendingEstimateInsert? FindPendingInsert(int id) =>
        _pendingEstimateInserts.FirstOrDefault(p => p.Row.Id == id);

    private static long ResolvePendingAnchor(PendingEstimateInsert pending) =>
        pending is { Failed: false, ServerId: int id } ? id :
            throw new InvalidOperationException("Предыдущая позиция не подтверждена сервером.");

    private bool CanShowPendingInsert =>
        (!_inlineStagesOn || _inlineStagesFilter == StageAllocationPresentation.All) &&
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
            checked(_nextPendingInsertId--), component, "", "", "шт", 1m, 0m, 0m, null, false, "")
        {
            SystemId = anchor.Source.SystemId,
            ParentSystemId = section.Source.ParentSystemId,
            ParentPositionId = component ? rootId : null
        }) { IsPendingInsert = true };

        var pending = new PendingEstimateInsert(draft, rootId);
        var index = PendingInsertIndex(visible, pending.Row.Source.SystemId, pending.AfterRootId);
        if (index < 0) return null;
        _pendingEstimateInserts.Add(pending);
        if (_inlineStagesOn) FillInlineStageCells(draft);
        // One collection Add, not ItemsSource reassignment: keep the scroll,
        // realized rows and column layout intact.
        visible.Insert(index, draft);
        EstimateGrid.SelectedItem = draft;
        EstimateGrid.CurrentCell = new DataGridCellInfo(draft, NameColumn);
        EstimateGrid.ScrollIntoView(draft, NameColumn);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_detached || _serverConflict || !_pendingEstimateInserts.Contains(pending) ||
                !ReferenceEquals(EstimateGrid.SelectedItem, draft) ||
                !EstimateGrid.IsVisible || !EstimateGrid.Items.Contains(draft)) return;
            EstimateGrid.Focus();
            EstimateGrid.CurrentCell = new DataGridCellInfo(draft, NameColumn);
            EstimateGrid.BeginEdit();
        }), DispatcherPriority.Loaded);
        return pending;
    }

    internal static int PendingInsertIndex(IEnumerable<EditableEstimateRow> visible,
        int systemId, int? afterRootId)
    {
        var index = 0;
        var last = -1;
        foreach (var row in visible)
        {
            if (row.Source.SystemId == systemId &&
                (!row.IsVirtual || row.IsPendingInsert) &&
                (afterRootId is not int root ||
                    !row.IsSystem && (row.Id == root || row.Source.ParentPositionId == root)))
                last = index;
            index++;
        }
        return last < 0 ? -1 : last + 1;
    }

    private void IncludePendingInsert(List<EditableEstimateRow> visible)
    {
        if (!CanShowPendingInsert) return;
        foreach (var pending in _pendingEstimateInserts)
        {
            if (!visible.Any(row => row.IsSystem && row.Source.SystemId == pending.Row.Source.SystemId &&
                !row.IsCollapsed)) continue;
            var index = PendingInsertIndex(visible, pending.Row.Source.SystemId, pending.AfterRootId);
            if (index >= 0)
            {
                if (_inlineStagesOn) FillInlineStageCells(pending.Row);
                visible.Insert(index, pending.Row);
            }
        }
    }

    private bool CanEditPendingInsert(EditableEstimateRow row, string? field) =>
        FindPendingInsert(row.Id) is { Failed: false } pending &&
        ReferenceEquals(pending.Row, row) && !_serverConflict && _estimate?.Locked == false &&
        (field is "name" or "unit" or "qty" or "note" ||
            !row.IsSub && (field is "price" or "purchase_price" ||
                field == "work_price" && _estimate.SmrMode == "pos"));

    private bool CommitPendingInsertCell(EditableEstimateRow row, string field, string input)
    {
        if (FindPendingInsert(row.Id) is not { } pending || !ReferenceEquals(pending.Row, row))
            return false;
        if (field is ("qty" or "price" or "purchase_price" or "work_price") &&
            input.TrimStart().StartsWith("="))
        {
            SetStatus("Формулы доступны после сверки новой позиции с сервером.", true);
            return false;
        }
        pending.EnteredEdits[field] = input;
        if (pending.Failed || pending.ServerId is null)
            pending.BufferedEdits[field] = input;
        else
            QueueSave(pending.ServerId.Value, field, input);
        return true;
    }

    // Only the accepted SQL snapshot may replace the local draft. Transfer
    // focus by the real identity; don't steal focus if the user moved away.
    private static string PendingFieldLabel(string field) => field switch
    {
        "name" => "Наименование", "unit" => "Ед.", "qty" => "Кол-во",
        "price" => "Цена", "purchase_price" => "Закупка", "work_price" => "Работа за ед.",
        "note" => "Примечание", _ => field
    };

    private void ReconcilePendingInsert(EstimateDocument document)
    {
        if (_pendingEstimateInserts.Count == 0) return;
        var ids = document.Rows.Where(r => !r.IsSystem).Select(r => r.Id).ToHashSet();
        foreach (var pending in _pendingEstimateInserts)
        {
            if (pending.ServerId is int id && ids.Contains(id))
            {
                if (ReferenceEquals(EstimateGrid.SelectedItem, pending.Row))
                {
                    _focusAfterReload = id;
                    _focusSystemAfterReload = null;
                    _editAfterReload = false;
                }
            }
            else
                _saveError ??= "Одна или несколько новых позиций не найдены в подтверждённой смете.";
        }
        if (_saveError is not null)
        {
            var entered = _pendingEstimateInserts.Where(p => p.EnteredEdits.Count > 0)
                .Select(p => $"Строка {Math.Abs((long)p.Row.Id)}: " +
                    string.Join("; ", p.EnteredEdits.Select(x => PendingFieldLabel(x.Key) + ": " + x.Value)));
            _saveError += ". Введённые данные: " + string.Join(" | ", entered);
        }
        _pendingEstimateInserts.Clear();
    }
}
