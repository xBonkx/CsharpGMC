using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class ProcurementPane
{
    private DateTime _snapshotDay;
    private bool _restoringBuySelection;
    private bool _applyingBuySnapshot;
    private int _buyViewportGeneration;
    private ObservableCollection<ProcurementPreviewRow>? _buyDisplay;
    private bool _buyDisplayGrouped;
    private BuyDisplayKey[] _buyDisplayKeys = Array.Empty<BuyDisplayKey>();
    private Dictionary<long, int>? _buyRowIndex;

    internal readonly record struct BuyDisplayKey(long Id, string Label, string Identity,
        string Title, string Number)
    {
        public static BuyDisplayKey Of(ProcurementPreviewRow row) =>
            new(row.PositionId, row.GroupLabel, row.GroupIdentity,
                row.GroupTitle, row.GroupDisplayNumber);
    }

    internal static bool SameBuyRowReferences(IList<ProcurementPreviewRow> current,
        IReadOnlyList<ProcurementPreviewRow> next)
    {
        if (current.Count != next.Count) return false;
        for (var i = 0; i < next.Count; i++)
            if (!ReferenceEquals(current[i], next[i])) return false;
        return true;
    }

    /// <summary>Update the current view in place only when its membership, order and groups agree.
    /// Otherwise rebuild from the in-memory projection, not from SQL.</summary>
    private void ApplyBuyDisplay(List<ProcurementPreviewRow> display, bool grouped)
    {
        // The planner mutates group captions on reused row objects. Compare
        // detached keys (including header title/number), not the mutated rows.
        var keys = display.Select(BuyDisplayKey.Of).ToArray();
        var canReplace = _buyDisplay is not null &&
            BuyGrid.ItemsSource is ListCollectionView currentView &&
            ReferenceEquals(currentView.SourceCollection, _buyDisplay) &&
            _buyDisplayGrouped == grouped && _buyDisplay.Count == display.Count &&
            _buyDisplayKeys.SequenceEqual(keys);

        // A no-op filter or a committed update outside the visible subset
        // must not clear selection, queue scrolling or start another history
        // request. Snapshot callers still explicitly reload history at the
        // newly accepted revision after this method returns.
        if (canReplace && SameBuyRowReferences(_buyDisplay!, display))
            return;

        var selected = BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>().Select(r => r.PositionId).ToHashSet();
        var currentId = (BuyGrid.CurrentCell.Item as ProcurementPreviewRow)?.PositionId;
        var column = BuyGrid.CurrentCell.Column;
        var scroll = FindVisualChild<ScrollViewer>(BuyGrid);
        var vertical = scroll?.VerticalOffset ?? 0;
        var horizontal = scroll?.HorizontalOffset ?? 0;
        _restoringBuySelection = true;
        try
        {
            if (canReplace)
            {
                for (var i = 0; i < display.Count; i++)
                    if (!ReferenceEquals(_buyDisplay![i], display[i]))
                        _buyDisplay[i] = display[i];
            }
            else
            {
                _buyDisplay = new ObservableCollection<ProcurementPreviewRow>(display);
                _buyDisplayGrouped = grouped;
                var view = new ListCollectionView(_buyDisplay);
                if (grouped)
                    view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProcurementPreviewRow.GroupLabel)));
                BuyGrid.ItemsSource = view;
            }
            _buyDisplayKeys = keys;
            GridSelectionDelta.Restore(BuyGrid.SelectedItems,
                display.Where(r => selected.Contains(r.PositionId)));
            if (column is not null && BuyGrid.Columns.Contains(column) &&
                display.FirstOrDefault(r => r.PositionId == currentId) is { } current)
                BuyGrid.CurrentCell = new DataGridCellInfo(current, column);
        }
        finally { _restoringBuySelection = false; }
        PublishBuySelection();
        var generation = ++_buyViewportGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded || generation != _buyViewportGeneration ||
                FindVisualChild<ScrollViewer>(BuyGrid) is not { } viewer) return;
            viewer.ScrollToVerticalOffset(vertical);
            viewer.ScrollToHorizontalOffset(horizontal);
        }));
    }

    private void PublishBuySelection() =>
        BuyGrid_SelectionChanged(BuyGrid, new SelectionChangedEventArgs(Selector.SelectionChangedEvent,
            Array.Empty<object>(), Array.Empty<object>()));

    private async Task<bool> TryPatchCommittedBuyRowsAsync(IReadOnlyList<long> ids, string revision)
    {
        if (!IsLoaded || _needsReloadAfterAttach || ids.Count == 0 || ids.Count > SqlServerProcurement.MaxIncrementalBuyRows ||
            _snapshotDay != DateTime.Today ||
            BuyGrid.ItemsSource is not ListCollectionView { IsEditingItem: false }) return false;
        var attachment = _attachmentGeneration;
        var generation = _reloadGeneration;
        var day = _snapshotDay;
        using var operationTiming = StartupTrace.BeginOperation();
        using var readTiming = StartupTrace.MeasureElapsed("procurement_delta_read");
        var rows = await Task.Run(() =>
            SqlServerProcurement.ReadChangedBuyRows(_connectionString, _contractId, ids, revision));
        if (!IsLoaded || attachment != _attachmentGeneration || generation != _reloadGeneration ||
            rows is null || _snapshotDay != day ||
            !SqlServerProcurement.CanPublishBuyDelta(ids, rows.Select(row => row.PositionId).ToArray(), day, DateTime.Today) ||
            BuyGrid.ItemsSource is not ListCollectionView { IsEditingItem: false }) return false;
        readTiming?.Dispose();
        using var projectionTiming = StartupTrace.Measure("procurement_delta_prepare");
        // The full readback replaces the list and invalidates this index.
        // Incremental readback only replaces items at existing indices.
        var index = _buyRowIndex ??= _allRows.Select((row, i) => (row.PositionId, Index: i))
            .ToDictionary(x => x.PositionId, x => x.Index);
        var replacements = new List<(int Index, ProcurementPreviewRow Row)>();
        foreach (var row in rows)
        {
            if (row.Work || !index.TryGetValue(row.PositionId, out var i)) return false;
            var old = _allRows[i];
            if (old.SnapshotStructure != row.Structure ||
                old.SystemId != row.SystemId || old.ParentSystemId != row.ParentSystemId ||
                old.OutOfKp != row.OutOfKp || old.IsSub != (row.ParentPositionId is not null) ||
                old.SystemName != row.SystemName || old.EstimateName != row.Name) return false;
            replacements.Add((i, BuildBuyPreview(row, old.RowNumber, old.GroupNumber, old.OriginalIndex)));
        }
        if (replacements.Count != ids.Count) return false;
        projectionTiming?.Dispose();
        using var bindTiming = StartupTrace.Measure("procurement_delta_bind");
        _applyingBuySnapshot = true;
        try
        {
            foreach (var (i, row) in replacements) _allRows[i] = row;
            RebuildScopeFilters();
            UpdateCategoryCounts();
            ApplyProcurementFilters();
            _revision = revision;
        }
        finally { _applyingBuySnapshot = false; }
        bindTiming?.Dispose();
        StartupTrace.Sample("procurement_delta_rows", ids.Count, _allRows.Count);
        if (MovementHistoryExpander?.IsExpanded == true) _ = LoadSelectedMovementHistoryAsync();
        return true;
    }

    private async Task RefreshCommittedBuyRowsAsync(IReadOnlyList<long> ids, string revision)
    {
        if (!IsLoaded) return;
        try
        {
            if (await TryPatchCommittedBuyRowsAsync(ids.Distinct().ToArray(), revision)) return;
        }
        catch (Exception)
        {
            // The write was already committed. Never repeat it because a display refresh failed.
            _revision = "";
        }
        StartupTrace.Sample("procurement_delta_fallback", ids.Count);
        try { await ReloadAsync(preserveStatus: true); }
        catch (Exception ex)
        {
            _revision = "";
            throw new InvalidOperationException("SQL-запись подтверждена, но не удалось перечитать закупки: " +
                ex.Message + ". Обновите таблицу, не повторяйте запись.", ex);
        }
    }
}
