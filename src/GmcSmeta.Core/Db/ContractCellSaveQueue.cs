using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// A user edit remains unconfirmed until its SQL writer returns success.
/// Each queue is FIFO, has one consumer, and stops after the first rejection
/// or ambiguous COMMIT. It NEVER retries an edit automatically.
/// The queue is an ordering layer, NOT an atomic multi-cell SQL transaction.
/// </summary>
public sealed class ContractCellSaveQueue
{
    public readonly record struct Edit(long PositionId, string Field, string Input);

    public readonly record struct Receipt(bool Saved, string Revision, string? Error, bool UnknownCommit = false)
    {
        public static Receipt Success(string revision) => new(true, revision, null);
        public static Receipt Reject(string error, string revision = "", bool unknownCommit = false) =>
            new(false, revision, error, unknownCommit);
    }

    private sealed record Entry(Edit Edit, TaskCompletionSource<Receipt> Completion);

    private readonly object _sync = new();
    private readonly Queue<Entry> _items = new();
    private readonly Func<Edit, Task<Receipt>> _write;
    private readonly TimeSpan _debounce;
    private TaskCompletionSource<bool>? _idle;
    private bool _running;
    private bool _blocked;
    private int _outstanding;
    private string? _lastError;

    public ContractCellSaveQueue(Func<Edit, Task<Receipt>> write, TimeSpan? debounce = null)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _debounce = debounce ?? TimeSpan.FromMilliseconds(85);
        if (_debounce < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce));
    }

    public int PendingCount { get { lock (_sync) return _outstanding; } }
    public bool Blocked { get { lock (_sync) return _blocked; } }
    public string? LastError { get { lock (_sync) return _lastError; } }

    public Task<Receipt> Enqueue(Edit edit)
    {
        lock (_sync)
        {
            if (_blocked)
                return Task.FromResult(Receipt.Reject(
                    _lastError ?? "Запись заблокирована до повторного чтения SQL Server."));
            var completion = new TaskCompletionSource<Receipt>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _items.Enqueue(new Entry(edit, completion));
            _outstanding++;
            _idle ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_running)
            {
                _running = true;
                _ = Task.Run(DrainAsync);
            }
            return completion.Task;
        }
    }

    /// <summary>Wait for writes already queued; does not cancel a possible SQL COMMIT.</summary>
    public Task WhenIdleAsync()
    {
        lock (_sync)
            return _outstanding == 0 ? Task.CompletedTask : _idle!.Task;
    }

    /// <summary>
    /// Call ONLY after an authoritative SQL reload has replaced optimistic
    /// rows. Discarded edits are never replayed by this method.
    /// </summary>
    public void ResumeAfterReadback()
    {
        lock (_sync)
        {
            if (_outstanding != 0 || _running)
                throw new InvalidOperationException("Нельзя возобновлять очередь до завершения записи.");
            _blocked = false;
            _lastError = null;
        }
    }

    private async Task DrainAsync()
    {
        // Delay the FIRST edit in a burst, not every subsequent edit.
        // Distinct cells remain separate SaveCell transactions until a
        // verified atomic SQL batch API exists; no unsafe pseudo-batching.
        await Task.Delay(_debounce).ConfigureAwait(false);
        while (true)
        {
            Entry entry;
            lock (_sync)
            {
                if (_items.Count == 0)
                {
                    _running = false;
                    SignalIdleIfNeeded();
                    return;
                }
                entry = _items.Dequeue();
            }

            Receipt receipt;
            try
            {
                receipt = await _write(entry.Edit).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A network fault can occur after the server committed.
                // Conservatively require readback and never blind-retry.
                receipt = Receipt.Reject(ex.Message, unknownCommit: true);
            }

            lock (_sync)
            {
                entry.Completion.TrySetResult(receipt);
                _outstanding--;
                if (!receipt.Saved)
                {
                    _blocked = true;
                    _lastError = receipt.Error ?? "SQL Server отклонил запись.";
                    while (_items.TryDequeue(out var skipped))
                    {
                        _outstanding--;
                        skipped.Completion.TrySetResult(Receipt.Reject(
                            "Правка не отправлена: предыдущая операция не подтверждена SQL Server.",
                            receipt.Revision, receipt.UnknownCommit));
                    }
                    _running = false;
                }
                SignalIdleIfNeeded();
                if (!receipt.Saved) return;
            }
        }
    }

    private void SignalIdleIfNeeded()
    {
        // Must be invoked under _sync.
        if (_outstanding != 0) return;
        _idle?.TrySetResult(true);
        _idle = null;
    }
}

/// <summary>
/// One in-process writer for the same SQL Server/database/contract, including
/// other EstimateView instances. Other clients are guarded by SQL meta.rev.
/// </summary>
public static class ContractSqlWriteLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim For(string connectionString, long contractId)
    {
        if (contractId <= 0) throw new ArgumentOutOfRangeException(nameof(contractId));
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
            throw new InvalidOperationException("Не указана база SQL Server.");
        var key = $"{builder.DataSource.Trim()}|{builder.InitialCatalog.Trim()}|{contractId}";
        return Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }
}
