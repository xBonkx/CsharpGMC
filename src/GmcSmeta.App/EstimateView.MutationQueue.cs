using System.Data;
using System.Threading;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// All structure-mutating estimate commands share the same queue as
/// cell editing and price-like batches. Each command reads meta.rev only
/// after acquiring the lock, and reloads the committed server state.
/// </summary>
public partial class EstimateView
{
    private async Task RunEstimateMutationAsync(string working,
        Func<string, SqlServerDatabase.SaveResult> writer, string success)
    {
        if (_estimate?.Locked != false)
        {
            SetStatus("Подписанный или закрытый документ нельзя менять.", true);
            return;
        }
        if (_serverConflict)
        {
            SetStatus("Смета изменилась на сервере. Сначала обнови её.", true);
            return;
        }

        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        var count = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"{working} ({count})", false);
        await _writeLock.WaitAsync();
        var saved = false;
        try
        {
            if (_serverConflict)
            {
                _saveError ??= "Смета изменилась во время ожидания записи.";
                return;
            }
            var revision = _serverRevision;
            var result = await Task.Run(() => writer(revision));
            if (!result.Saved)
            {
                _saveError = result.Error ?? "Сервер отказал в сохранении.";
                if (!string.Equals(result.Revision, revision, StringComparison.Ordinal))
                    _serverConflict = true;
                return;
            }
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
            saved = true;
        }
        catch (DBConcurrencyException ex)
        {
            _serverConflict = true;
            _saveError = ex.Message;
        }
        catch (Exception ex)
        {
            // A lost COMMIT acknowledgement is ambiguous; don't continue
            // using an old revision until authoritative reload succeeds.
            _serverConflict = true;
            _saveError = ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? success : "Ошибка: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
        if (saved && Volatile.Read(ref _pendingWrites) == 0)
        {
            _reloadTimer.Stop();
            await ReloadDocumentAsync();
        }
    }
}
