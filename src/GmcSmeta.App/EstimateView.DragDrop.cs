using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

/// <summary>
/// Python PosTree drag & drop for normal "Как в КП" view only.
/// Drag the № cell: rows (including multiselection) move as roots with
/// components; systems retain subsections; subsections may switch parents.
/// Cursor displays a blue insertion edge. SQL revalidates and commits once.
/// </summary>
public partial class EstimateView
{
    private enum DragKind { Rows, RootSystem, Subsection }
    private sealed record DragPayload(int ContractId, string Revision,
        DragKind Kind, long[] Ids);
    private sealed record DragDestination(long SystemId, long? AfterId, string Description);
    private const string DragFormat = "GMC-Smeta-EstimateDrag/v1";

    private Point? _dragStart;
    private EditableEstimateRow? _dragStartRow;
    private DataGridRow? _dragMarkerRow;
    private bool _dragMoving;

    // Modal position placement also works for main/dop documents. The mouse
    // drag gesture itself stays KP-only to preserve existing navigation.
    private bool CanReorderEstimateRows =>
        _estimate is { Locked: false } && !_serverConflict && !_detached &&
        Volatile.Read(ref _pendingWrites) == 0 &&
        _temporarySort == EstimateViewProjection.DocumentOrder &&
        _temporaryGroupMode is not (EstimateGroups.Same or EstimateGroups.Type or "list") &&
        !_flatTemporaryView && !_noPriceTemporaryView &&
        (!_inlineStagesOn || (_inlineStagesFilter == StageAllocationPresentation.All &&
            !_inlineStagesLoading && !_inlineStageCommandBusy && _inlineStagesData is { } data &&
            data.Revision == _serverRevision)) &&
        string.IsNullOrWhiteSpace(EstimateSearch.Text) &&
        !_refreshing && !HasActiveEstimateEdit();

    private bool CanPlaceEstimateRows => CanReorderEstimateRows && !_inlineStagesOn && IsDragPilot();

    private bool CanDragEstimate => _isKp && CanReorderEstimateRows && IsDragPilot();

    private bool IsDragPilot()
    {
        try
        {
            return new SqlConnectionStringBuilder(_sqlConnectionString).InitialCatalog
                .StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private static T? VisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (var node = source; node is not null;)
        {
            if (node is T match) return match;
            node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void EstimateGrid_DragMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _dragStartRow = null;
        if (!CanDragEstimate || e.ChangedButton != MouseButton.Left ||
            EstimateGrid.IsReadOnly || e.OriginalSource is not DependencyObject hit ||
            VisualAncestor<Button>(hit) is not null)
            return;
        var cell = VisualAncestor<DataGridCell>(hit);
        var row = VisualAncestor<DataGridRow>(hit);
        if (row?.Item is not EditableEstimateRow item || item.IsVirtual ||
            cell?.Column?.Header?.ToString() != "№") return;
        _dragStart = e.GetPosition(EstimateGrid);
        _dragStartRow = item;
        // Keep DataGrid's default selection: Shift/Ctrl multiselection still works.
    }

    private void EstimateGrid_DragMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _dragStartRow = null;
    }

    private void EstimateGrid_DragMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMoving || _dragStart is not Point start ||
            _dragStartRow is not EditableEstimateRow selected ||
            e.LeftButton != MouseButtonState.Pressed || !CanDragEstimate)
            return;
        var where = e.GetPosition(EstimateGrid);
        if (Math.Abs(where.X - start.X) < SystemParameters.MinimumHorizontalDragDistance + 3 &&
            Math.Abs(where.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance + 3)
            return;
        _dragStart = null;
        _dragStartRow = null;

        var selection = EstimateGrid.SelectedItems
            .OfType<EditableEstimateRow>().ToList();
        if (!selection.Contains(selected)) selection = new List<EditableEstimateRow> { selected };
        DragPayload? payload = null;
        if (selected.IsSystem)
        {
            if (!_inlineStagesOn && selection.Count == 1)
            {
                payload = new DragPayload(_contractId, _serverRevision,
                    selected.ParentSystemId is null ? DragKind.RootSystem : DragKind.Subsection,
                    new long[] { selected.Source.SystemId });
            }
        }
        else if (selection.All(x => !x.IsSystem && !x.IsVirtual))
        {
            // Display order, not DataGrid's SelectedItems enumeration order.
            var ids = selection.OrderBy(x => x.SortKey)
                .Select(x => (long)x.Id).Distinct().ToArray();
            if (ids.Length > 0) payload = new DragPayload(
                _contractId, _serverRevision, DragKind.Rows, ids);
        }
        if (payload is null)
        {
            SetStatus("Перетаскивай либо позиции, либо одну систему или подраздел.", true);
            return;
        }

        try
        {
            _dragMoving = true;
            var dragData = new DataObject(DragFormat, payload);
            DragDrop.DoDragDrop(EstimateGrid, dragData, DragDropEffects.Move);
        }
        finally
        {
            _dragMoving = false;
            ClearDragMarker();
        }
    }

    private bool TryDragTarget(DragEventArgs e, out DragPayload payload,
        out DragDestination destination, out DataGridRow? rowVisual, out bool before)
    {
        payload = null!;
        destination = null!;
        rowVisual = null;
        before = false;
        if (!CanDragEstimate || !e.Data.GetDataPresent(DragFormat) ||
            e.Data.GetData(DragFormat) is not DragPayload data ||
            data.ContractId != _contractId || data.Revision != _serverRevision ||
            data.Ids.Length == 0 || e.OriginalSource is not DependencyObject hit)
            return false;
        payload = data;
        rowVisual = VisualAncestor<DataGridRow>(hit);
        if (rowVisual?.Item is not EditableEstimateRow on ||
            on.IsVirtual || on.Id < 0) return false;
        before = e.GetPosition(rowVisual).Y < rowVisual.ActualHeight / 2;

        var sectionById = _rows.Where(x => x.IsSystem)
            .ToDictionary(x => (long)x.Source.SystemId);
        if (!sectionById.TryGetValue(on.Source.SystemId, out var section))
            return false;
        var rootSystemId = section.ParentSystemId ?? section.Source.SystemId;
        var draggingId = data.Ids[0];

        if (data.Kind == DragKind.RootSystem)
        {
            var all = _rows.Where(x => x.IsSystem && x.ParentSystemId is null)
                .Select(x => (long)x.Source.SystemId).ToArray();
            var at = Array.IndexOf(all, (long)rootSystemId);
            if (at < 0) return false;
            long? after = before
                ? at == 0 ? null : all[at - 1]
                : rootSystemId;
            if (after == draggingId) return false;
            destination = new DragDestination(rootSystemId, after,
                $"Система → {(after is long a ? "после " + sectionById[a].Name : "в начало")}");
            return true;
        }

        if (data.Kind == DragKind.Subsection)
        {
            long? after = null;
            if (section.ParentSystemId is not null)
            {
                var siblings = _rows.Where(x => x.IsSystem &&
                    x.ParentSystemId == rootSystemId)
                    .Select(x => (long)x.Source.SystemId).ToArray();
                var at = Array.IndexOf(siblings, (long)section.Source.SystemId);
                if (at < 0) return false;
                after = before
                    ? at == 0 ? null : siblings[at - 1]
                    : section.Source.SystemId;
            }
            if (after == draggingId) return false;
            destination = new DragDestination(rootSystemId, after,
                $"Подраздел → {sectionById[rootSystemId].Name}");
            return true;
        }

        // Python: drop on section header inserts immediately after header
        // (first row). On a component, normalise to its root.
        var targetSystem = (long)on.Source.SystemId;
        if (_inlineStagesOn && data.Ids.Any(id =>
            !_rows.Any(r => !r.IsSystem && r.Id == id && r.Source.SystemId == targetSystem)))
            return false;
        long? anchor = null;
        if (!on.IsSystem)
        {
            var root = on.Source.ParentPositionId ?? on.Id;
            if (before)
            {
                var roots = _rows.Where(x => !x.IsSystem && !x.IsSub &&
                    x.Source.SystemId == on.Source.SystemId)
                    .Select(x => (long)x.Id).ToArray();
                var at = Array.IndexOf(roots, (long)root);
                if (at < 0) return false;
                anchor = at == 0 ? null : roots[at - 1];
            }
            else anchor = root;
        }
        if (anchor is long onId && data.Ids.Contains(onId))
            return false;
        destination = new DragDestination(targetSystem, anchor,
            $"Позиции → {section.Name}" +
            (anchor is long id ? $" после строки {id}" : " в начало"));
        return true;
    }

    private void EstimateGrid_DragOver(object sender, DragEventArgs e)
    {
        if (TryDragTarget(e, out _, out var destination,
            out var row, out var before))
        {
            e.Effects = DragDropEffects.Move;
            if (row is not null) MarkDragRow(row, before, destination.Description);
        }
        else
        {
            e.Effects = DragDropEffects.None;
            ClearDragMarker();
        }
        e.Handled = true;
    }

    private void MarkDragRow(DataGridRow row, bool before, string description)
    {
        ClearDragMarker();
        _dragMarkerRow = row;
        row.BorderBrush = Brushes.DodgerBlue;
        row.BorderThickness = before ? new Thickness(0, 3, 0, 0)
                                     : new Thickness(0, 0, 0, 3);
        row.ToolTip = description;
    }

    private void ClearDragMarker()
    {
        if (_dragMarkerRow is not DataGridRow row) return;
        row.ClearValue(DataGridRow.BorderBrushProperty);
        row.ClearValue(DataGridRow.BorderThicknessProperty);
        row.ClearValue(DataGridRow.ToolTipProperty);
        _dragMarkerRow = null;
    }

    private void EstimateGrid_DragLeave(object sender, DragEventArgs e)
    {
        ClearDragMarker();
    }

    private async void EstimateGrid_Drop(object sender, DragEventArgs e)
    {
        var valid = TryDragTarget(e, out var source, out var destination,
            out _, out _);
        ClearDragMarker();
        e.Handled = true;
        if (!valid) return;
        _reloadTimer.Stop();
        Interlocked.Increment(ref _pendingWrites);
        SetStatus(destination.Description + " — сохранение…", false);
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || source.Revision != _serverRevision)
            {
                SetStatus("Смета изменилась во время перетаскивания. Обнови её.", true);
                return;
            }
            var response = await Task.Run(() => source.Kind switch
            {
                DragKind.RootSystem => SqlServerDatabase.DragSystem(
                    _sqlConnectionString, _contractId, source.Ids[0],
                    destination.AfterId, source.Revision),
                DragKind.Subsection => SqlServerDatabase.DragSubsection(
                    _sqlConnectionString, _contractId, source.Ids[0],
                    destination.SystemId, destination.AfterId, source.Revision),
                _ => SqlServerDatabase.DragPositions(
                    _sqlConnectionString, _contractId, source.Ids,
                    destination.SystemId, destination.AfterId, source.Revision),
            });
            if (!response.Saved)
            {
                SetStatus("Перенос не выполнен: " + response.Error, true);
                if (response.Revision != _serverRevision) _serverConflict = true;
                return;
            }
            _serverRevision = response.Revision;
            if (source.Kind == DragKind.Rows)
                _focusAfterReload = checked((int)source.Ids[0]);
            else
                _focusSystemAfterReload = checked((int)source.Ids[0]);
            SetStatus(destination.Description + " ✓", false);
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка перетаскивания: " + ex.Message, true);
            return;
        }
        finally
        {
            _writeLock.Release();
            Interlocked.Decrement(ref _pendingWrites);
        }
        await ReloadDocumentAsync();
    }
}
