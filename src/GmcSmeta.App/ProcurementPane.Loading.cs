using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class ProcurementPane
{
    private CancellationTokenSource? _ordinaryReloadCancellation;
    private CancellationTokenSource? _historyCancellation;
    private bool _historyNeedsResume;

    private void CancelOrdinaryBuyReload()
    {
        if (_ordinaryReloadCancellation is not { } cancellation) return;
        _ordinaryReloadCancellation = null;
        ++_reloadGeneration;
        cancellation.Cancel();
    }

    private void CancelMovementHistory()
    {
        ++_historyLoadToken;
        _historyRevision = "";
        var cancellation = _historyCancellation;
        _historyCancellation = null;
        cancellation?.Cancel();
    }

    private void SuspendBuyReads()
    {
        if (_ordinaryReloadCancellation is not null)
        {
            _needsReloadAfterAttach = true;
            CancelOrdinaryBuyReload();
        }
        _historyNeedsResume = MovementHistoryExpander?.IsExpanded == true;
        CancelMovementHistory();
    }

    private void ResumeVisibleBuyReads()
    {
        if (!IsLoaded || !IsVisible || _batchBusy || _ordinaryReloadCancellation is not null ||
            BuyGrid.ItemsSource is ListCollectionView { IsEditingItem: true }) return;
        if (_needsReloadAfterAttach)
            _ = TryReloadAsync(requiredReadback: false);
        else if (_historyNeedsResume)
            // Revalidate the published revision before resuming history on a visible tab.
            // The tab-return handler observes the same ordinary-reload guard.
            _ = TryReloadAsync(allowUnchanged: true, requiredReadback: false);
    }

    // Default remains non-cancellable for every existing post-write caller.
    private async Task<bool> ReloadAsync(bool preserveStatus = false, bool allowUnchanged = false,
        bool requiredReadback = true)
    {
        if (!IsLoaded) return false;
        if (!requiredReadback && !IsVisible)
        {
            _needsReloadAfterAttach = true;
            return false;
        }
        CancelOrdinaryBuyReload();
        CancelMovementHistory();
        using var cancellation = requiredReadback ? null : new CancellationTokenSource();
        _ordinaryReloadCancellation = cancellation;
        var token = cancellation?.Token ?? CancellationToken.None;
        // All network I/O is outside the WPF dispatcher. A later refresh
        // supersedes an earlier response if they finish out of order.
        using var operationTiming = StartupTrace.BeginOperation();
        var generation = ++_reloadGeneration;
        var cachedRevision = allowUnchanged && _buySnapshotMayReuse && !_batchBusy &&
            !_needsReloadAfterAttach && _snapshotDay == DateTime.Today &&
            BuyGrid.ItemsSource is ListCollectionView { IsEditingItem: false }
            ? _revision : "";
        var cachedDay = _snapshotDay;
        _revision = "";
        _historyRevision = "";
        _buySnapshotMayReuse = false;
        UpdateHistoryButtons();
        try
        {
            using var readTiming = StartupTrace.MeasureElapsed("procurement_snapshot_read");
            var snapshot = await Task.Run(() =>
                SqlServerProcurement.ReadBuySnapshotIfChanged(_connectionString, _contractId,
                    cachedRevision, cachedDay, token), token);
            if (token.IsCancellationRequested || generation != _reloadGeneration || !IsLoaded) return false;
            if (BuyGrid.ItemsSource is ListCollectionView { IsEditingItem: true })
                throw new InvalidOperationException("Завершите редактирование строки и обновите закупки.");
            if (snapshot is null)
            {
                // A midnight rollover during the await invalidates due-date flags.
                // No write is allowed while _revision is empty.
                if (_batchBusy || _needsReloadAfterAttach || cachedDay != DateTime.Today)
                {
                    snapshot = await Task.Run(() => SqlServerProcurement.ReadBuySnapshotIfChanged(
                        _connectionString, _contractId, "", null, token), token);
                    if (token.IsCancellationRequested || generation != _reloadGeneration || !IsLoaded) return false;
                    if (snapshot is null) throw new InvalidOperationException("Не получен снимок закупок.");
                    if (BuyGrid.ItemsSource is ListCollectionView { IsEditingItem: true })
                        throw new InvalidOperationException("Завершите редактирование строки и обновите закупки.");
                }
                else
                {
                    _revision = cachedRevision;
                    _buySnapshotMayReuse = true;
                    if (MovementHistoryExpander?.IsExpanded == true) _ = LoadSelectedMovementHistoryAsync();
                    if (!preserveStatus) FooterText.Text = "";
                    return true;
                }
            }
            readTiming?.Dispose();
            var accepted = snapshot;
            using var projectionTiming = StartupTrace.MeasureElapsed("procurement_snapshot_prepare");
            var items = await Task.Run(() => PrepareBuyRows(accepted, token), token);
            projectionTiming?.Dispose();
            if (token.IsCancellationRequested || generation != _reloadGeneration || !IsLoaded) return false;
            if (accepted.Day != DateTime.Today)
                throw new InvalidOperationException("Дата изменилась во время загрузки. Обновите закупки.");
            if (BuyGrid.ItemsSource is ListCollectionView { IsEditingItem: true })
                throw new InvalidOperationException("Завершите редактирование строки и обновите закупки.");
            using var bindTiming = StartupTrace.Measure("procurement_snapshot_bind");
            _applyingBuySnapshot = true;
            try
            {
                _allRows = items;
                _buyRowIndex = null;
                _allSystems = accepted.Systems.ToList();
                RebuildScopeFilters();
                UpdateCategoryCounts();
                ApplyProcurementFilters();
                _snapshotDay = accepted.Day;
                _snapshotCanWrite = accepted.CanWrite;
                BuyGrid.IsReadOnly = !_snapshotCanWrite;
                _revision = accepted.Revision;
                _buySnapshotMayReuse = true;
                _needsReloadAfterAttach = false;
            }
            finally { _applyingBuySnapshot = false; }
            bindTiming?.Dispose();
            if (MovementHistoryExpander?.IsExpanded == true) _ = LoadSelectedMovementHistoryAsync();
            if (!preserveStatus) FooterText.Text = "";
            return true;
        }
        catch (Exception) when (!requiredReadback &&
            (token.IsCancellationRequested || generation != _reloadGeneration))
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_ordinaryReloadCancellation, cancellation)) _ordinaryReloadCancellation = null;
        }
    }

    // Only plain, unbound rows are created on the worker. All collection
    // views, control state and publication remain on the Dispatcher.
    private static List<ProcurementPreviewRow> PrepareBuyRows(
        SqlServerProcurement.BuySnapshot snapshot, CancellationToken token)
    {
        var rows = snapshot.Rows;
        var items = new List<ProcurementPreviewRow>(rows.Count);
        var originalIndex = 0;
        var number = 1;
        var systemNumbers = new Dictionary<long, string>();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            // Python procurement omits purely work rows from buying and warehouse views.
            if (row.Work) continue;
            if (!systemNumbers.ContainsKey(row.SystemId))
                systemNumbers[row.SystemId] = row.SystemName == "Без системы" ? "" : (number++).ToString(CultureInfo.CurrentCulture);
            var rowNumber = row.ParentPositionId is null && !row.OutOfKp ? (number++).ToString(CultureInfo.CurrentCulture) : "";
            items.Add(BuildBuyPreview(row, rowNumber, systemNumbers[row.SystemId], originalIndex++));
        }
        token.ThrowIfCancellationRequested();
        return items;
    }

    private async Task TryReloadAsync(bool preserveStatus = false, bool allowUnchanged = false,
        bool requiredReadback = true)
    {
        try { await ReloadAsync(preserveStatus, allowUnchanged, requiredReadback); }
        catch (Exception ex) { SetStatus("Ошибка загрузки закупок: " + ex.Message); }
    }

    private async Task LoadSelectedMovementHistoryAsync()
    {
        if (!IsLoaded || !IsVisible || MovementHistoryExpander?.IsExpanded != true ||
            MovementHistoryGrid is null || MovementDetailsText is null) return;
        CancelMovementHistory();
        _historyNeedsResume = false;
        var generation = _historyLoadToken;
        var revision = _revision;
        var row = SelectedHistoryRow();
        MovementHistoryGrid.ItemsSource = null;
        MovementDeleteButton.IsEnabled = false;
        MovementEditButton.IsEnabled = false;
        if (row is null || string.IsNullOrEmpty(revision)) return;
        using var cancellation = new CancellationTokenSource();
        _historyCancellation = cancellation;
        var token = cancellation.Token;
        MovementDetailsText.Text = "Загрузка истории из SQL Server…";
        try
        {
            // Keyboard/group selection may emit a burst of events. No SQL opens until
            // the target has remained selected for 150 ms. Cancellation also reaches SQL.
            await Task.Delay(150, token);
            if (token.IsCancellationRequested || generation != _historyLoadToken || !IsLoaded || !IsVisible ||
                MovementHistoryExpander?.IsExpanded != true || _revision != revision ||
                SelectedHistoryRow()?.PositionId != row.PositionId) return;
            using var historyTiming = StartupTrace.MeasureElapsed("procurement_history_read");
            var rows = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.OpenAsync(_connectionString, token).GetAwaiter().GetResult();
                if (SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult() != revision) return null;
                var history = SqlServerProcurement.MovementsForPosition(
                    conn, _contractId, row.PositionId, token);
                return SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult() == revision ? history : null;
            }, token);
            if (token.IsCancellationRequested || generation != _historyLoadToken || !IsLoaded || !IsVisible || MovementHistoryExpander?.IsExpanded != true || _revision != revision ||
                SelectedHistoryRow()?.PositionId != row.PositionId) return;
            if (rows is null)
            {
                MovementDetailsText.Text = "История изменилась. Обновите закупки.";
                return;
            }
            historyTiming?.Dispose();
            _historyRevision = revision;
            MovementHistoryGrid.ItemsSource = rows;
            MovementDetailsText.Text =
                $"По КП: {row.Qty} {row.Unit} · пришло {row.Received} · выдано {row.Issued}" +
                $" · записей: {rows.Count}" +
                (row.Flags.Length > 0 ? " · ⚠ " + row.Flags : "");
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || generation != _historyLoadToken || !IsLoaded || !IsVisible) return;
            MovementDetailsText.Text = "Не удалось прочитать историю: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_historyCancellation, cancellation)) _historyCancellation = null;
        }
    }

}
