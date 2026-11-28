namespace GmcSmeta.Core.Db;

/// <summary>Reserve local write order at the user action, not after worker
/// debounce. A discarded ticket cannot open the next slot before its
/// predecessor has finished. SQL revision checks remain the cross-client fence.</summary>
public sealed class OrderedWriteGate
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;

    public Ticket Reserve()
    {
        lock (_sync)
        {
            var ticket = new Ticket(_tail);
            _tail = ticket.Completion;
            return ticket;
        }
    }

    public sealed class Ticket : IDisposable
    {
        private readonly Task _predecessor;
        private readonly TaskCompletionSource<bool> _done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _claimed;
        private int _released;

        internal Ticket(Task predecessor) => _predecessor = predecessor;
        internal Task Completion => _done.Task;

        public Task WaitAsync()
        {
            if (Volatile.Read(ref _released) != 0 || Interlocked.Exchange(ref _claimed, 1) != 0)
                throw new InvalidOperationException("Write reservation is already used or discarded.");
            return _predecessor;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            if (_predecessor.IsCompletedSuccessfully) _done.TrySetResult(true);
            else _ = CompleteAfterPredecessorAsync();
        }

        private async Task CompleteAfterPredecessorAsync()
        {
            await _predecessor.ConfigureAwait(false);
            _done.TrySetResult(true);
        }
    }
}
