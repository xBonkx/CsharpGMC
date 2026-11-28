using System.Windows;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class EstimateView
{
    // Transfer uses the same contract writer as cell edits. SQL returns atomic
    // before/after snapshots, so another client's prices cannot enter Undo.
    private async Task<SqlServerRateTransfer.Receipt> ApplyRateTransferAsync(
        EstimateDocument snapshot, string revision, Func<SqlServerRateTransfer.Receipt> writer)
    {
        if (!CanChangeHeader(snapshot, revision, false))
            throw new InvalidOperationException("Документ изменился. Обновите смету.");
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        Interlocked.Increment(ref _pendingWrites);
        InvalidateSparseReadback(); // структурная правка: только полный reload
        var acquired = false;
        try
        {
            await _writeLock.WaitWithoutBaselineAsync();
            acquired = true;
            if (_serverConflict || _detached || !ReferenceEquals(snapshot, _estimate) ||
                revision != _serverRevision || revision != _publishedEstimateRevision ||
                Window.GetWindow(this) is not MainWindow main ||
                !main.IsCurrentEstimatePage(_contractId, _objectId, this) ||
                !main.CanEditEstimateHeader(_contractId, _objectId))
                throw new InvalidOperationException("Документ изменился до переноса.");

            var receipt = await Task.Run(writer);
            _serverRevision = receipt.Revision;
            _saveAwaitingReadback = true;
            MainWindow.Undo?.RecordChange(_contractId, receipt.Before, receipt.After);
            Interlocked.Exchange(ref _undoCheckpointNeeded, 0);
            return receipt;
        }
        catch
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            MainWindow.Undo?.Forget(_contractId);
            throw;
        }
        finally
        {
            if (acquired) _writeLock.Release();
            Interlocked.Decrement(ref _pendingWrites);
        }
    }

    internal void RequireRateTransferReadback()
    {
        _serverConflict = true;
        _saveAwaitingReadback = true;
        _saveError = "Перенос расценок не подтверждён.";
        MainWindow.Undo?.Forget(_contractId);
        AttachSaveCloseGuard();
        RequestRefresh();
    }
}
