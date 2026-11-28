using System.Data;
using System.Diagnostics;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

// All connections and write targets are derived from the runner's nonce-owned
// database. These are SQL integration checks, not interactive WPF/network tests.
internal static class PilotConcurrencyReadCases
{
    internal static void ThreeClients(PilotRunner p)
    {
        using var conn = p.Conn();
        p.RequireOwned(conn);
        var ids = new[]
        {
            p.FirstPosition,
            PilotRunner.Long(conn, "SELECT MIN(id) FROM dbo.positions WHERE system_id=@id", ("@id", p.LargeSystem)),
            PilotRunner.Long(conn, "SELECT MAX(id) FROM dbo.positions WHERE system_id=@id", ("@id", p.LargeSystem))
        };
        PilotRunner.Check(ids.All(id => id > 0) && ids.Distinct().Count() == 3,
            "Three distinct synthetic positions required");
        var before = ids.Select(id => Note(conn, id)).ToArray();
        var revision = p.Revision();
        var markers = Enumerable.Range(0, ids.Length)
            .Select(i => "pilot-three-client-" + p.Token + "-" + i).ToArray();
        using var ready = new CountdownEvent(ids.Length);
        using var start = new ManualResetEventSlim(false);
        var elapsed = new double[ids.Length];
        // LongRunning gives each logical client a dedicated worker so the
        // barrier itself does not depend on thread pool injection.
        var workers = ids.Select((id, i) => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            start.Wait();
            var watch = Stopwatch.StartNew();
            try
            {
                return SqlServerDatabase.SaveCell(p.PilotConnection, id, "note", markers[i], revision);
            }
            finally { elapsed[i] = watch.Elapsed.TotalMilliseconds; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var allReady = ready.Wait(TimeSpan.FromSeconds(15));
        start.Set();
        // Drain all commands before proceeding or disposing the owned fixture.
        // Provider command/login timeout bounds the existing synchronous calls.
        Task.WaitAll(workers);
        PilotRunner.Check(allReady, "Three client workers did not reach start barrier");
        var results = workers.Select(task => task.Result).ToArray();
        var winners = Enumerable.Range(0, results.Length).Where(i => results[i].Saved).ToArray();
        PilotRunner.Check(winners.Length == 1, "Exactly one global-revision writer must commit");
        var winner = winners[0];
        var after = p.Revision();
        PilotRunner.Check(after != revision && after == results[winner].Revision,
            "Winning revision must match independent readback");
        for (var i = 0; i < ids.Length; i++)
        {
            PilotRunner.Check(Note(conn, ids[i]) == (i == winner ? markers[i] : before[i]),
                "Rejected client modified a position, or winner was not persisted");
            if (i != winner)
                PilotRunner.Check(!results[i].Saved && results[i].Revision == after,
                    "Rejected client did not receive committed revision");
            p.MetricOne("three_client_save_" + (i == winner ? "winner" : "stale"), 1,
                elapsed[i], "Client elapsed time includes login, lock wait and command work; not isolated lock duration");
        }
        // Do not automatically retry any rejected or ambiguous write.
        // A subsequent read obtains the committed state explicitly.
        using var readback = p.Conn();
        PilotRunner.Check(SqlServerDatabase.Revision(readback) == after &&
            Note(readback, ids[winner]) == markers[winner], "Fresh connection did not see winning write");
    }

    internal static void GlobalRevision(PilotRunner p)
    {
        using var conn = p.Conn();
        p.RequireOwned(conn);
        var small = checked((int)p.SmallContract);
        var snapshot = SqlServerProcurement.ReadBuySnapshot(p.PilotConnection, small);
        PilotRunner.Check(snapshot.Rows.Count > 0, "Empty procurement fixture");
        PilotRunner.Check(SqlServerProcurement.ReadBuySnapshotIfChanged(p.PilotConnection, small,
            snapshot.Revision, snapshot.Day) is null, "Stable snapshot did not reuse revision");
        var other = PilotRunner.Long(conn,
            "SELECT MIN(id) FROM dbo.positions WHERE system_id=@id", ("@id", p.LargeSystem));
        PilotRunner.Check(other > 0 && other != p.FirstPosition, "Other-document position missing");
        var marker = "pilot-other-document-" + p.Token;
        var saved = SqlServerDatabase.SaveCell(p.PilotConnection, other, "note", marker, snapshot.Revision);
        PilotRunner.Check(saved.Saved && saved.Revision != snapshot.Revision, "Other-document write failed");
        var refreshed = SqlServerProcurement.ReadBuySnapshotIfChanged(p.PilotConnection, small,
            snapshot.Revision, snapshot.Day);
        PilotRunner.Check(refreshed is not null && refreshed.Revision == saved.Revision,
            "Global revision did not invalidate unrelated snapshot");
        PilotRunner.Check(refreshed!.Rows.Count == snapshot.Rows.Count &&
            refreshed.Rows.Select(r => (r.PositionId, r.Qty, r.Price, r.Received, r.Issued, r.Status))
                .SequenceEqual(snapshot.Rows.Select(r => (r.PositionId, r.Qty, r.Price, r.Received, r.Issued, r.Status))),
            "Unrelated document write changed procurement quantities, prices or status");
        PilotRunner.Check(Note(conn, other) == marker, "Other-document write not persisted");
    }

    internal static void CancelBlockedRevision(PilotRunner p)
    {
        using var blocker = p.Conn();
        p.RequireOwned(blocker);
        var original = SqlServerDatabase.Revision(blocker);
        // Force lock-based reads even on a server with RCSI enabled. Session-only
        // setting on a separate test connection; no database isolation changes.
        // RevisionAsync does not accept a transaction; use a session setting on
        // an independent nonpooled connection instead of changing its public API.
        var connectionString = new SqlConnectionStringBuilder(p.PilotConnection) { Pooling = false }.ConnectionString;
        using var cancellable = SqlServerDatabase.Open(connectionString);
        p.RequireOwned(cancellable);
        PilotRunner.Execute(cancellable, "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ");
        using var tx = blocker.BeginTransaction(IsolationLevel.Serializable);
        using (var locked = new SqlCommand(
            "SELECT [value] FROM dbo.meta WITH (XLOCK,HOLDLOCK) WHERE [key]=N'rev'", blocker, tx))
            PilotRunner.Check(Convert.ToString(locked.ExecuteScalar()) == original, "Could not hold revision row");
        using var cancel = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var pending = SqlServerDatabase.RevisionAsync(cancellable, cancel.Token);
        bool cancelled = false;
        try
        {
            PilotRunner.Check(!pending.Wait(TimeSpan.FromMilliseconds(250)),
                "Revision read was not blocked by held lock");
            cancel.Cancel();
            // The command must finish while the blocking transaction still holds its lock.
            try
            {
                _ = pending.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (SqlException ex) when (cancel.IsCancellationRequested && ex.Number == 0)
            {
                // SqlClient can surface an attention/cancel as SQL error 0.
                // Timeout (-2) and other provider failures are NOT cancellation proof.
                cancelled = true;
            }
            PilotRunner.Check(cancelled, "Blocked revision read returned data instead of cancellation");
            p.MetricOne("blocked_revision_cancel", 1, watch.Elapsed.TotalMilliseconds,
                "Includes 250ms observation and provider cancellation; real SQL lock, not TCP fault");
        }
        finally
        {
            cancel.Cancel();
            tx.Rollback();
            // Always drain the pending read before disposing its connection.
            try { pending.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch (SqlException) { }
        }
        using var fresh = p.Conn();
        PilotRunner.Check(SqlServerDatabase.Revision(fresh) == original,
            "Read cancellation changed revision or prevented fresh connection");
    }

    internal static void PreCancelledReads(PilotRunner p)
    {
        using var conn = p.Conn();
        var before = SqlServerDatabase.Revision(conn);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var token = cts.Token;
        RequireCancelled(() => SqlServerDatabase.OpenAsync(p.PilotConnection, token).GetAwaiter().GetResult().Dispose());
        RequireCancelled(() => _ = SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult());
        RequireCancelled(() => _ = SqlServerStages.FullOfAsync(conn, p.SmallContract, token).GetAwaiter().GetResult());
        RequireCancelled(() => _ = SqlServerStages.ReadStageDistribution(conn, p.SmallContract, token));
        RequireCancelled(() => _ = SqlServerProcurement.ReadBuySnapshotIfChanged(p.PilotConnection,
            checked((int)p.SmallContract), "", null, token));
        PilotRunner.Check(SqlServerDatabase.Revision(conn) == before,
            "Pre-cancelled reads changed database revision");
    }

    private static void RequireCancelled(Action action)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("A pre-cancelled read did not throw cancellation");
    }

    private static string Note(SqlConnection conn, long id) =>
        PilotRunner.Text(conn, "SELECT note FROM dbo.positions WHERE id=@id", ("@id", id));
}
