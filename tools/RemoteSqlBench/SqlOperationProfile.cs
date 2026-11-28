using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;
using System.Data;

namespace GmcSmeta.RemoteSqlBench;

// Dedicated, nonpooled guarded connection only; never records SQL text,
// parameter values, identity, document names or connection strings.
internal static class SqlOperationProfile
{
    internal sealed record SessionCounters(long CpuMs, long LogicalReads, long Reads, long Writes);
    internal sealed record WaitCounter(long Count, long Milliseconds);
    internal sealed record OperationResult(string Operation, int Rows, double ElapsedMs,
        int? CommandCount, Dictionary<string, long?> Provider, SessionCounters? ServerDelta,
        Dictionary<string, WaitCounter>? WaitDelta, bool RevisionStable);

    internal static long? Counter(IDictionary values, string name) =>
        values.Contains(name) && values[name] is not null
            ? Convert.ToInt64(values[name], CultureInfo.InvariantCulture) : null;

    internal static SessionCounters? Difference(SessionCounters? before, SessionCounters? after) =>
        before is null || after is null || after.CpuMs < before.CpuMs ||
        after.LogicalReads < before.LogicalReads || after.Reads < before.Reads || after.Writes < before.Writes
            ? null : new(after.CpuMs - before.CpuMs, after.LogicalReads - before.LogicalReads,
                after.Reads - before.Reads, after.Writes - before.Writes);

    internal static Dictionary<string, WaitCounter>? DifferenceWaits(
        IReadOnlyDictionary<string, WaitCounter>? before, IReadOnlyDictionary<string, WaitCounter>? after)
    {
        if (before is null || after is null) return null;
        var result = new Dictionary<string, WaitCounter>(StringComparer.Ordinal);
        foreach (var (name, old) in before)
            if (!after.TryGetValue(name, out var value) || value.Count < old.Count || value.Milliseconds < old.Milliseconds)
                return null; // DMV reset or lost visibility: not zero.
        foreach (var (name, value) in after)
        {
            var old = before.GetValueOrDefault(name) ?? new WaitCounter(0, 0);
            if (value.Count != old.Count || value.Milliseconds != old.Milliseconds)
                result.Add(name, new(value.Count - old.Count, value.Milliseconds - old.Milliseconds));
        }
        return result;
    }

    private static SessionCounters? Session(SqlConnection observer, int session)
    {
        try
        {
            using var cmd = new SqlCommand("""
                SELECT cpu_time,logical_reads,reads,writes FROM sys.dm_exec_sessions
                WHERE session_id=@id AND database_id=DB_ID()
                """, observer) { CommandTimeout = 10 };
            cmd.Parameters.Add("@id", SqlDbType.Int).Value = session;
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)),
                Convert.ToInt64(reader.GetValue(2)), Convert.ToInt64(reader.GetValue(3))) : null;
        }
        catch (SqlException) { return null; }
    }

    private static Dictionary<string, WaitCounter>? Waits(SqlConnection observer, int session)
    {
        try
        {
            using var cmd = new SqlCommand("""
                SELECT wait_type,waiting_tasks_count,wait_time_ms FROM sys.dm_exec_session_wait_stats
                WHERE session_id=@id
                """, observer) { CommandTimeout = 10 };
            cmd.Parameters.Add("@id", SqlDbType.Int).Value = session;
            using var reader = cmd.ExecuteReader();
            var result = new Dictionary<string, WaitCounter>(StringComparer.Ordinal);
            while (reader.Read()) result.Add(reader.GetString(0),
                new(Convert.ToInt64(reader.GetValue(1)), Convert.ToInt64(reader.GetValue(2))));
            // Empty is ambiguous (no permissions/no rows). Do not report zero waits.
            return result.Count == 0 ? null : result;
        }
        catch (SqlException) { return null; }
    }

    // SqlClient diagnostic events count provider Execute calls, not result
    // sets or network round trips. Missing/incompatible event is unknown.
    private sealed class CommandCounter : IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly SqlConnection _connection;
        private readonly List<IDisposable> _listeners = new();
        private readonly IDisposable _all;
        private int _count;
        public CommandCounter(SqlConnection connection)
        {
            _connection = connection;
            _all = DiagnosticListener.AllListeners.Subscribe(this);
        }
        public int? Count => _count == 0 ? null : _count;
        public void OnNext(DiagnosticListener value)
        {
            if (value.Name.Contains("SqlClient", StringComparison.Ordinal))
                _listeners.Add(value.Subscribe(this, name => name.EndsWith(".WriteCommandBefore", StringComparison.Ordinal)));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (!value.Key.EndsWith(".WriteCommandBefore", StringComparison.Ordinal)) return;
            if (value.Value?.GetType().GetProperty("Command")?.GetValue(value.Value) is SqlCommand command &&
                ReferenceEquals(command.Connection, _connection))
                Interlocked.Increment(ref _count);
        }
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void Dispose()
        {
            _all.Dispose();
            foreach (var listener in _listeners) listener.Dispose();
        }
    }

    internal static (bool Ok, string Detail) Collect(SqlConnection conn, string connectionString, string path, int contractId)
    {
        SqlDiagnostics.RequireIsolatedCatalog(conn.Database);
        using var observer = SqlServerDatabase.Open(new SqlConnectionStringBuilder(connectionString)
            { Pooling = false }.ConnectionString);
        SqlDiagnostics.RequireIsolatedCatalog(observer.Database);
        var revision = SqlServerDatabase.Revision(conn);
        var rows = new List<OperationResult>();
        void Run(string name, Func<int> read)
        {
            var before = Session(observer, conn.ServerProcessId);
            var waits = Waits(observer, conn.ServerProcessId);
            conn.StatisticsEnabled = true;
            conn.ResetStatistics();
            int count;
            double elapsed;
            int? commands;
            IDictionary stats;
            using (var counter = new CommandCounter(conn))
            {
                var watch = Stopwatch.StartNew();
                try { count = read(); }
                finally { watch.Stop(); }
                elapsed = watch.Elapsed.TotalMilliseconds;
                commands = counter.Count;
                stats = conn.RetrieveStatistics();
            }
            conn.StatisticsEnabled = false;
            var server = Difference(before, Session(observer, conn.ServerProcessId));
            var waitDelta = DifferenceWaits(waits, Waits(observer, conn.ServerProcessId));
            var names = new[] { "ServerRoundtrips", "SelectCount", "SelectRows", "SumResultSets",
                "BytesReceived", "BytesSent", "ExecutionTime", "NetworkServerTime" };
            rows.Add(new(name, count, elapsed, commands, names.ToDictionary(x => x, x => Counter(stats, x)),
                server, waitDelta, !string.IsNullOrWhiteSpace(revision) && revision == SqlServerDatabase.Revision(conn)));
        }
        Run("select1", () => { using var cmd = new SqlCommand("SELECT 1", conn); return Convert.ToInt32(cmd.ExecuteScalar()); });
        Run("estimate", () => EstimateReader.Load(conn, contractId).Rows.Count);
        Run("totals", () => Totals.ReadSelected(conn, new[] { contractId }).Count);
        Run("cost_summary", () => SqlServerCostSummary.Summary(conn, contractId).Rows.Count);
        Run("navigation", () => Workspace.ReadConsistentNavigation(conn).Documents.Count);
        Run("procurement", () =>
        {
            using var cmd = new SqlCommand(SqlServerProcurement.BuyRowsQuery(), conn);
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var reader = cmd.ExecuteReader();
            var count = 0;
            while (reader.Read()) { _ = SqlServerProcurement.MapBuyRow(reader, DateTime.Today); count++; }
            return count;
        });
        File.WriteAllText(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        var complete = rows.All(x => x.RevisionStable && x.CommandCount.HasValue && x.ServerDelta is not null &&
            x.WaitDelta is not null && x.Provider.Values.All(v => v.HasValue));
        return (complete, $"operations={rows.Count}; missing provider/DMV visibility is INCOMPLETE");
    }
}
