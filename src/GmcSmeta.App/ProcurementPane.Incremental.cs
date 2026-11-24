using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
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
    private (long Id, string Label, string Identity)[] _buyDisplayKeys = Array.Empty<(long, string, string)>();

    /// <summary>Update the current view in place only when its membership, order and groups agree.
    /// Otherwise rebuild from the in-memory projection, not from SQL.</summary>
    private void ApplyBuyDisplay(List<ProcurementPreviewRow> display, bool grouped)
    {
        var selected = BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>().Select(r => r.PositionId).ToHashSet();
        var currentId = (BuyGrid.CurrentCell.Item as ProcurementPreviewRow)?.PositionId;
        var column = BuyGrid.CurrentCell.Column;
        var scroll = FindVisualChild<ScrollViewer>(BuyGrid);
        var vertical = scroll?.VerticalOffset ?? 0;
        var horizontal = scroll?.HorizontalOffset ?? 0;
        // The planner mutates group captions on reused row objects. Compare against
        // detached keys of the last applied view, not the now-mutated row references.
        var keys = display.Select(row => (row.PositionId, row.GroupLabel, row.GroupIdentity)).ToArray();
        var canReplace = _buyDisplay is not null && BuyGrid.ItemsSource is ListCollectionView &&
            _buyDisplayGrouped == grouped && _buyDisplay.Count == display.Count &&
            _buyDisplayKeys.SequenceEqual(keys);
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
            BuyGrid.SelectedItems.Clear();
            foreach (var row in display.Where(r => selected.Contains(r.PositionId)))
                BuyGrid.SelectedItems.Add(row);
            if (column is not null && BuyGrid.Columns.Contains(column) &&
                display.FirstOrDefault(r => r.PositionId == currentId) is { } current)
                BuyGrid.CurrentCell = new DataGridCellInfo(current, column);
        }
        finally { _restoringBuySelection = false; }
        BuyGrid_SelectionChanged(BuyGrid, new SelectionChangedEventArgs(Selector.SelectionChangedEvent,
            Array.Empty<object>(), Array.Empty<object>()));
        var generation = ++_buyViewportGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded || generation != _buyViewportGeneration ||
                FindVisualChild<ScrollViewer>(BuyGrid) is not { } viewer) return;
            viewer.ScrollToVerticalOffset(vertical);
            viewer.ScrollToHorizontalOffset(horizontal);
        }));
    }

    private async Task<bool> TryPatchCommittedBuyRowsAsync(IReadOnlyList<long> ids, string revision)
    {
        if (!IsLoaded || _needsReloadAfterAttach || ids.Count == 0 || ids.Count > SqlServerProcurement.MaxIncrementalBuyRows ||
            _snapshotDay != DateTime.Today ||
            BuyGrid.ItemsSource is not ListCollectionView { IsEditingItem: false }) return false;
        var attachment = _attachmentGeneration;
        var rows = await Task.Run(() =>
            SqlServerProcurement.ReadChangedBuyRows(_connectionString, _contractId, ids, revision));
        if (!IsLoaded || attachment != _attachmentGeneration || rows is null || _snapshotDay != DateTime.Today) return false;
        var index = _allRows.Select((row, i) => (row.PositionId, Index: i))
            .ToDictionary(x => x.PositionId, x => x.Index);
        var replacements = new List<(int Index, ProcurementPreviewRow Row)>();
        foreach (var row in rows)
        {
            if (row.Work || !index.TryGetValue(row.PositionId, out var i)) return false;
            var old = _allRows[i];
            if (old.SystemId != row.SystemId || old.ParentSystemId != row.ParentSystemId ||
                old.OutOfKp != row.OutOfKp || old.IsSub != (row.ParentPositionId is not null) ||
                old.SystemName != row.SystemName || old.EstimateName != row.Name) return false;
            replacements.Add((i, BuildBuyPreview(row, old.RowNumber, old.GroupNumber, old.OriginalIndex)));
        }
        if (replacements.Count != ids.Count) return false;
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
        try { await ReloadAsync(preserveStatus: true); }
        catch (Exception ex)
        {
            _revision = "";
            throw new InvalidOperationException("SQL-запись подтверждена, но не удалось перечитать закупки: " +
                ex.Message + ". Обновите таблицу, не повторяйте запись.", ex);
        }
    }
}
