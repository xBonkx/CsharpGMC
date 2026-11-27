using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class EstimateView
{
    private bool _saveAwaitingReadback;
    private Window? _saveClosingHost;

    // Invoked by ContractCellSaveQueue on a background worker; all estimate
    // mutations, not just cell writes, share the same per-contract semaphore.
    private async Task<ContractCellSaveQueue.Receipt> WriteCellOnBackgroundAsync(
        ContractCellSaveQueue.Edit edit)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_serverConflict)
                return ContractCellSaveQueue.Receipt.Reject(
                    "Смета устарела. Обнови документ прежде чем повторять правку.");

            // Fail closed even if this method is invoked without the main-window
            // pilot guard. No production writes, regardless of credentials.
            SqlServerWorkspace.RequirePilotDatabase(_sqlConnectionString);
            var result = SqlServerDatabase.SaveCell(
                _sqlConnectionString, edit.PositionId, edit.Field, edit.Input, _serverRevision);
            if (!result.Saved)
                return ContractCellSaveQueue.Receipt.Reject(
                    result.Error ?? "Сервер отказал в сохранении ячейки.", result.Revision);

            _serverRevision = result.Revision;
            return ContractCellSaveQueue.Receipt.Success(result.Revision);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// UI never displays a SQL success before its receipt; a second authoritative
    /// read completes reconciliation. Errors stop subsequent queued writes.
    /// </summary>
    private void QueueSave(int rowId, string field, string input) =>
        _ = QueueSaveAsync(rowId, field, input);

    /// <summary>Returns the SQL receipt so multi-cell paste cannot claim success early.</summary>
    private async Task<bool> QueueSaveAsync(int rowId, string field, string input)
    {
        // Temporary UI rows must never reach SQL, even via a bulk/paste shortcut.
        if (rowId <= 0)
        {
            SetStatus("Дождитесь подтверждения новой позиции сервером.", true);
            return false;
        }
        var saved = false;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var count = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Не сохранено: {count} правок в очереди…", false);
        try
        {
            var receipt = await _cellSaveQueue.Enqueue(
                new ContractCellSaveQueue.Edit(rowId, field, input));
            if (!receipt.Saved)
            {
                _serverConflict = true;
                _saveAwaitingReadback = false;
                _saveError = receipt.UnknownCommit
                    ? "Исход SQL COMMIT неизвестен: " + receipt.Error
                    : receipt.Error ?? "Сервер отказал в записи.";
            }
            else
            {
                saved = true;
                _saveAwaitingReadback = true;
            }
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = false;
            _saveError = "Исход COMMIT неизвестен: " + ex.Message;
        }
        finally
        {
            var remaining = Interlocked.Decrement(ref _pendingWrites);
            if (remaining == 0)
            {
                SetStatus(_saveError is null
                    ? "Записано в SQL Server, выполняется сверка…"
                    : "Ошибка записи: " + _saveError + ". Автоповтор отключён.",
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
                if (_saveError is null && !_saveAwaitingReadback &&
                    !_cellSaveQueue.Blocked)
                    DetachSaveCloseGuard();
            }
        }
        return saved;
    }

    // The editor can be removed from the navigation tree while a write is in
    // flight. Keep the window guard attached until SQL work/readback is done.
    private void AttachSaveCloseGuard()
    {
        var host = Window.GetWindow(this);
        if (host is null || ReferenceEquals(host, _saveClosingHost)) return;
        DetachSaveCloseGuard();
        _saveClosingHost = host;
        host.Closing += SaveHost_Closing;
    }

    private void DetachSaveCloseGuard()
    {
        if (_saveClosingHost is null) return;
        _saveClosingHost.Closing -= SaveHost_Closing;
        _saveClosingHost = null;
    }

    private void SaveHost_Closing(object? sender, CancelEventArgs e)
    {
        if (Volatile.Read(ref _pendingWrites) > 0 || _cellSaveQueue.PendingCount > 0)
        {
            e.Cancel = true;
            SetStatus("Закрытие отложено: SQL Server ещё сохраняет правки.", true);
            return;
        }

        if (_cellSaveQueue.Blocked || _saveAwaitingReadback || _saveError is not null || _serverConflict)
        {
            var result = MessageBox.Show(
                "Есть неподтверждённые правки или не завершена сверка с SQL Server. " +
                "Закрыть без повторной отправки этих правок?",
                "GMC-Смета — проверка сохранения",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        DetachSaveCloseGuard();
    }
}
