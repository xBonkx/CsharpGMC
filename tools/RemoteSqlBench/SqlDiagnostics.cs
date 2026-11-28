using System.Data;
using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.RemoteSqlBench;

internal sealed record SqlDiagnosticResult(string Name, string Status, string Detail, string File);

/// <summary>Opt-in local evidence, outside benchmark timings. No DDL or data mutation.</summary>
internal static class SqlDiagnostics
{
    // One row per index column, including heaps and tables without indexes.
    internal const string IndexInventorySql = """
        SELECT t.name AS table_name, i.index_id, i.name AS index_name, i.type_desc,
            i.is_unique, i.is_primary_key, i.is_disabled, i.is_hypothetical,
            i.has_filter, i.filter_definition, ic.key_ordinal, ic.index_column_id,
            ic.is_included_column, ic.is_descending_key, c.name AS column_name
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
        LEFT JOIN sys.indexes i ON i.object_id=t.object_id
        LEFT JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
        LEFT JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
        WHERE s.name=N'dbo' AND t.name IN
            (N'systems',N'positions',N'contracts',N'movements',N'procurement',
             N'acts',N'act_lines',N'shipment_lines',N'spec_revisions')
        ORDER BY t.name,i.index_id,ic.index_column_id;
        """;

    internal const string StatisticsInventorySql = """
        SELECT t.name AS table_name, st.stats_id, st.name AS statistics_name,
            st.auto_created, st.user_created, st.no_recompute, st.has_filter,
            STATS_DATE(st.object_id,st.stats_id) AS last_updated,
            sc.stats_column_id, c.name AS column_name
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
        JOIN sys.stats st ON st.object_id=t.object_id
        LEFT JOIN sys.stats_columns sc ON sc.object_id=st.object_id AND sc.stats_id=st.stats_id
        LEFT JOIN sys.columns c ON c.object_id=sc.object_id AND c.column_id=sc.column_id
        WHERE s.name=N'dbo' AND t.name IN
            (N'systems',N'positions',N'contracts',N'movements',N'procurement',
             N'acts',N'act_lines',N'shipment_lines',N'spec_revisions')
        ORDER BY t.name,st.stats_id,sc.stats_column_id;
        """;

    // Independent correlated reference for the movement fields only. Same
    // subtraction, NULL fallback and SQL types as the production aggregate.
    internal const string MovementReferenceSql = """
        SELECT p.id,
            ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'in'),0)
            - ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'back_supplier'),0),
            ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'out'),0)
            - ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'back'),0),
            (SELECT MAX(m.[date]) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'out')
        FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id
        WHERE sy.contract_id=@id ORDER BY sy.[order],sy.id,p.[order],p.id;
        """;

    internal static IReadOnlyList<SqlDiagnosticResult> Collect(string connectionString,
        int contractId, string directory)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        // Never return instrumented sessions to the normal connection pool.
        var cs = new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
        var results = new List<SqlDiagnosticResult>();
        void Run(string name, string file, Func<SqlConnection, string, (bool Ok, string Detail)> action)
        {
            try
            {
                using var conn = SqlServerDatabase.Open(cs);
                if (!conn.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unexpected database.");
                var result = action(conn, Path.Combine(directory, file));
                results.Add(new(name, result.Ok ? "OK" : "INCOMPLETE", result.Detail, file));
            }
            catch (Exception ex)
            {
                // SQL errors may contain private object names or server data.
                var detail = ex is SqlException se ? $"SqlException {se.Number}" : ex.GetType().Name;
                results.Add(new(name, "UNAVAILABLE", detail, ""));
            }
        }
        Run("index_inventory", "indexes.json", (conn, path) => Inventory(conn, IndexInventorySql, path));
        Run("statistics_inventory", "statistics.json", (conn, path) => Inventory(conn, StatisticsInventorySql, path));
        Run("procurement_movement_parity", "procurement-parity.json",
            (conn, path) => CompareMovements(conn, contractId, path));
        Run("procurement_io_time", "procurement-io-time.txt",
            (conn, path) => ReadIoTime(conn, contractId, path));
        Run("procurement_actual_plan", "procurement-actual-plan.sqlplan",
            (conn, path) => ReadActualPlan(conn, contractId, path));
        return results;
    }

    private static (bool Ok, string Detail) Inventory(SqlConnection conn, string sql, string path)
    {
        using var command = new SqlCommand(sql, conn);
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(row);
        }
        WriteJson(path, rows);
        // Missing visibility or a missing table is not proof of no indexes.
        var visible = rows.Select(r => r["table_name"] as string).Distinct().Count();
        return (visible == 9, $"rows={rows.Count}; visible_tables={visible}/9; metadata_visibility_not_proven");
    }

    internal sealed record MovementFields(long PositionId, decimal Received, decimal Issued, DateTime? LastIssue);
    internal static bool SameMovement(MovementFields expected, MovementFields actual) => expected == actual;

    private static (bool Ok, string Detail) CompareMovements(SqlConnection conn, int id, string path)
    {
        var revision = SqlServerDatabase.Revision(conn);
        var expected = new List<MovementFields>();
        using (var command = Command(conn, MovementReferenceSql, id))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                expected.Add(new MovementFields(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), Amount(reader, 1),
                    Amount(reader, 2), Date(reader, 3)));
        }
        var actual = new List<MovementFields>();
        var statusMismatches = 0;
        var today = DateTime.Today;
        using (var command = Command(conn, SqlServerProcurement.BuyRowsQuery(), id))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var mapped = SqlServerProcurement.MapBuyRow(reader, today);
                if (actual.Count < expected.Count && expected[actual.Count].PositionId == mapped.PositionId)
                {
                    var reference = expected[actual.Count];
                    if (mapped.Status != SqlServerProcurement.StatusOf(mapped.Qty,
                        reference.Received, reference.Issued, mapped.Stage))
                        statusMismatches++;
                }
                actual.Add(new MovementFields(mapped.PositionId, mapped.Received,
                    mapped.Issued, mapped.LastIssueDate));
            }
        }
        var mismatches = Math.Abs(expected.Count - actual.Count);
        for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
            if (!SameMovement(expected[i], actual[i])) mismatches++;
        var stable = !string.IsNullOrWhiteSpace(revision) && revision == SqlServerDatabase.Revision(conn);
        WriteJson(path, new { ExpectedRows = expected.Count, ActualRows = actual.Count,
            MovementOrOrderMismatches = mismatches, StatusMismatches = statusMismatches, RevisionStable = stable });
        return (stable && mismatches == 0 && statusMismatches == 0,
            $"movement_or_order_mismatches={mismatches}; status_mismatches={statusMismatches}; stable={stable}");
    }

    private static decimal Amount(IDataRecord reader, int i) => reader.IsDBNull(i) ? 0m :
        Money.FromDouble(Convert.ToDouble(reader.GetValue(i), CultureInfo.InvariantCulture));

    private static DateTime? Date(IDataRecord reader, int i) => reader.IsDBNull(i) ? null :
        Convert.ToDateTime(reader.GetValue(i), CultureInfo.InvariantCulture);

    private static SqlCommand Command(SqlConnection conn, string sql, int id)
    {
        var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        return cmd;
    }

    private static void Enable(SqlConnection conn, string settings)
    {
        using var command = new SqlCommand(settings, conn);
        command.ExecuteNonQuery();
    }

    private static (bool Ok, string Detail) ReadIoTime(SqlConnection conn, int id, string path)
    {
        var messages = new List<string>();
        // InfoMessage is raised by the provider while the results are drained.
        // Capture server statistics only (English message text); no row data.
        SqlInfoMessageEventHandler handler = (_, e) =>
        {
            foreach (SqlError error in e.Errors)
                if (error.Class <= 10 && IsStatisticsMessage(error.Message))
                    messages.Add(error.Message);
        };
        conn.InfoMessage += handler;
        int rows = 0;
        try
        {
            Enable(conn, "SET STATISTICS IO ON; SET STATISTICS TIME ON;");
            using var command = Command(conn, SqlServerProcurement.BuyRowsQuery(), id);
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                {
                    for (var i = 0; i < reader.FieldCount; i++) _ = reader.GetValue(i);
                    rows++;
                }
            } while (reader.NextResult());
        }
        finally { conn.InfoMessage -= handler; }
        File.WriteAllLines(path, messages);
        var io = messages.Any(m => m.Contains("logical reads", StringComparison.OrdinalIgnoreCase));
        var time = messages.Any(m => m.Contains("CPU time", StringComparison.OrdinalIgnoreCase));
        return (io && time, $"rows={rows}; io_messages={io}; time_messages={time}; English output only");
    }

    internal static bool IsStatisticsMessage(string message) =>
        (message.StartsWith("Table '", StringComparison.Ordinal) &&
            message.Contains("logical reads", StringComparison.OrdinalIgnoreCase)) ||
        (message.StartsWith("SQL Server ", StringComparison.Ordinal) &&
            message.Contains("CPU time", StringComparison.OrdinalIgnoreCase));

    private static (bool Ok, string Detail) ReadActualPlan(SqlConnection conn, int id, string path)
    {
        Enable(conn, "SET STATISTICS XML ON;");
        using var command = Command(conn, SqlServerProcurement.BuyRowsQuery(), id);
        using var reader = command.ExecuteReader();
        string? plan = null;
        while (reader.Read()) { /* drain the actual SELECT without recording row values */ }
        while (reader.NextResult())
        {
            while (reader.Read())
            {
                if (reader.FieldCount != 1 || reader.IsDBNull(0)) continue;
                var value = reader.GetValue(0);
                var xml = value is System.Data.SqlTypes.SqlXml sqlXml ? sqlXml.Value :
                    Convert.ToString(value, CultureInfo.InvariantCulture);
                if (xml is not null && xml.Contains("<ShowPlanXML", StringComparison.Ordinal))
                    plan = xml;
            }
        }
        if (plan is null) return (false, "Actual plan not returned.");
        File.WriteAllText(path, plan);
        return (true, "Actual plan collected; review operators manually.");
    }

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
