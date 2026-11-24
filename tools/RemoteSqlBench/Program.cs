using System.Data;
using System.Diagnostics;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

// Read-only LAN measurements. Never changes rows, schema, or meta.rev.
// SQL Server lives on another PC; this tool needs only the .NET 10 SDK.
var cs = Environment.GetEnvironmentVariable("GMC_SMETA_SQL_CONNECTION");
if (string.IsNullOrWhiteSpace(cs))
{
    Console.Error.WriteLine("Set GMC_SMETA_SQL_CONNECTION to an isolated GmcSmetaPilot_* test database.");
    return 2;
}
SqlServerWorkspace.RequirePilotDatabase(cs);
var runs = 30;
var at = Array.IndexOf(args, "--runs");
if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var count) && count >= 20)
    runs = Math.Min(count, 300);
using var conn = SqlServerDatabase.Open(cs);
var documents = Workspace.Documents(conn).OrderByDescending(d => d.Positions).ToArray();
if (documents.Length == 0)
{
    Console.Error.WriteLine("The test database has no contracts. Generate synthetic pilot data first.");
    return 3;
}
var doc = documents[0];
at = Array.IndexOf(args, "--contract-id");
if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out var id))
    doc = documents.FirstOrDefault(d => d.Id == id) ?? throw new ArgumentException("Unknown contract id.");
Console.WriteLine($"Read-only remote SQL benchmark. Contract={doc.Id}, positions={doc.Positions}, runs={runs}.");
Console.WriteLine("All times include SQL client and LAN round-trip; WPF/UI rendering is NOT measured.");
Console.WriteLine("case,rows,median_ms,p95_ms");

Measure("sql_roundtrip_select1", runs, () =>
{
    using var cmd = new SqlCommand("SELECT 1", conn);
    return Convert.ToInt32(cmd.ExecuteScalar());
});

foreach (var limit in new[] { 300, 2500, 10000 })
{
    var actual = CountPositions(conn, doc.Id);
    if (actual < limit)
    {
        Console.WriteLine($"sample_{limit},SKIP_need_{limit}_rows_found_{actual},,");
        continue;
    }
    Measure($"sample_{limit}", runs, () =>
    {
        using var cmd = new SqlCommand(
            "SELECT TOP (@limit) p.id,p.name,p.qty,p.price,p.purchase_price,p.work_price " +
            "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@contract ORDER BY p.id", conn);
        cmd.Parameters.Add("@limit", SqlDbType.Int).Value = limit;
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = doc.Id;
        using var reader = cmd.ExecuteReader();
        var rows = 0;
        while (reader.Read())
        {
            // Force materialization, including text/network payload.
            _ = reader.GetValue(0);
            _ = reader.GetValue(1);
            _ = reader.GetValue(2);
            _ = reader.GetValue(3);
            rows++;
        }
        return rows;
    });
}

// This actually runs the production-like C# estimate reader (not WPF rendering).
Measure("full_estimate_refresh", runs, () =>
{
    var estimate = EstimateReader.Load(conn, doc.Id);
    return estimate.Rows.Count;
});

Measure("procurement_load", runs, () => SqlServerProcurement.BuyRows(cs, doc.Id).Count);

if (documents.Length >= 2)
{
    var second = documents.First(d => d.Id != doc.Id);
    var next = false;
    Measure("switch_contract", runs, () =>
    {
        next = !next;
        var current = next ? doc.Id : second.Id;
        return EstimateReader.Load(conn, current).Rows.Count;
    });
}
else Console.WriteLine("switch_contract,SKIP_only_one_contract,,");

Console.WriteLine("NOTE: SaveCell, 100 writes, conflict recovery and WPF cell-to-total latency require");
Console.WriteLine("a separate disposable GmcSmetaPilot_Bench_* database and manual two-PC measurement.");
return 0;

int CountPositions(SqlConnection c, int contractId)
{
    using var cmd = new SqlCommand(
        "SELECT COUNT(*) FROM dbo.positions p JOIN dbo.systems sy ON p.system_id=sy.id WHERE sy.contract_id=@id", c);
    cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
    return Convert.ToInt32(cmd.ExecuteScalar());
}

void Measure(string label, int repeats, Func<int> action)
{
    var observations = new List<double>(repeats);
    var rows = 0;
    // Warm up query compilation/JIT/connection pool; it is excluded from measurements.
    action();
    for (var i = 0; i < repeats; i++)
    {
        var watch = Stopwatch.StartNew();
        rows = action();
        watch.Stop();
        observations.Add(watch.Elapsed.TotalMilliseconds);
    }
    observations.Sort();
    var median = (observations[(repeats - 1) / 2] + observations[repeats / 2]) / 2d;
    var p95 = observations[(int)Math.Ceiling(repeats * 0.95d) - 1];
    Console.WriteLine($"{label},{rows},{median:F2},{p95:F2}");
}
