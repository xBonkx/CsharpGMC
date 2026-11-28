using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using GmcSmeta.RemoteSqlBench;
using Microsoft.Data.SqlClient;

// Read-only LAN measurements. Never changes rows, schema, or meta.rev.
var cs = Environment.GetEnvironmentVariable("GMC_SMETA_SQL_CONNECTION");
if (string.IsNullOrWhiteSpace(cs))
{
    Console.Error.WriteLine("Set GMC_SMETA_SQL_CONNECTION to an isolated GmcSmetaPilot_* test database.");
    return 2;
}

BenchmarkOptions options;
try
{
    options = BenchmarkOptions.Parse(args);
    SqlServerWorkspace.RequirePilotDatabase(cs);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Invalid options or unsafe database: " + ex.GetType().Name);
    return 2;
}

var runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
    Guid.NewGuid().ToString("N")[..8];
var directory = Path.Combine(options.OutputDirectory, runId);
Directory.CreateDirectory(directory);
var started = DateTimeOffset.UtcNow;
var cases = new List<BenchmarkCase>();
var exitCode = 0;
var contractId = 0;
var positionCount = 0;
var databaseStable = false;
var failure = "";
var schemaVersion = "";
var sqlServerVersion = "";
IReadOnlyList<SqlDiagnosticResult> diagnostics = Array.Empty<SqlDiagnosticResult>();
try
{
    using var conn = SqlServerDatabase.Open(cs);
    if (!conn.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Unexpected database.");
    var revision = SqlServerDatabase.Revision(conn);
    if (string.IsNullOrWhiteSpace(revision))
        throw new InvalidOperationException("Missing revision.");
    sqlServerVersion = conn.ServerVersion;
    using (var schema = new SqlCommand("SELECT [value] FROM dbo.meta WHERE [key]=N'schema_version'", conn))
        schemaVersion = Convert.ToString(schema.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    var documents = Workspace.Documents(conn).OrderByDescending(d => d.Positions).ToArray();
    if (documents.Length == 0) throw new InvalidOperationException("No pilot documents.");
    var doc = options.ContractId is int requested
        ? documents.FirstOrDefault(d => d.Id == requested) ?? throw new ArgumentException("Unknown contract.")
        : documents[0];
    contractId = doc.Id;
    positionCount = CountPositions(conn, doc.Id);
    Console.WriteLine($"Read-only benchmark: contract={contractId}, positions={positionCount}, runs={options.Runs}.");
    Console.WriteLine("Warm SQL/client timings only, not WPF rendering. Evidence: " + directory);
    Console.WriteLine("case,status,rows,min_ms,p50_ms,p95_ms,p99_ms,max_ms");
    Measure("sql_roundtrip_select1", () =>
    {
        using var cmd = new SqlCommand("SELECT 1", conn);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    });
    Measure("guarded_open_warm_pool", () =>
    {
        using var fresh = SqlServerDatabase.Open(cs);
        return 1;
    });
    // A nonpooled login is not a cold server/cache measurement. Never clear
    // shared SQL caches or normal client pools merely to benchmark a connection.
    var nonPooled = new SqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString;
    Measure("guarded_open_nonpooled", () =>
    {
        using var fresh = SqlServerDatabase.Open(nonPooled);
        return 1;
    });
    foreach (var limit in new[] { 300, 2500, 10000, 25000 })
    {
        if (positionCount < limit)
        {
            Skip($"sample_{limit}", $"need_{limit}_found_{positionCount}");
            continue;
        }
        Measure($"sample_{limit}", () =>
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
                for (var field = 0; field < reader.FieldCount; field++) _ = reader.GetValue(field);
                rows++;
            }
            return rows;
        });
    }
    Measure("full_estimate_refresh", () => EstimateReader.Load(conn, doc.Id).Rows.Count);
    Measure("selected_totals", () => Totals.ReadSelected(conn, new[] { doc.Id }).Count);
    Measure("procurement_load", () => SqlServerProcurement.BuyRows(cs, doc.Id).Count);
    if (SqlServerStages.FullOf(conn, doc.Id) == doc.Id)
        Measure("stage_distribution", () => SqlServerStages.ReadStageDistribution(conn, doc.Id).Rows.Count);
    else Skip("stage_distribution", "selected_document_is_stage");
    if (documents.Length >= 2)
    {
        var second = documents.First(d => d.Id != doc.Id);
        Measure("switch_contract_a_to_b", () =>
        {
            _ = EstimateReader.Load(conn, doc.Id);
            return EstimateReader.Load(conn, second.Id).Rows.Count;
        }, second.Id);
        Measure("document_compare", () => SqlServerCompare.Compare(cs, doc.Id, second.Id).Rows.Count, second.Id);
    }
    else
    {
        Skip("switch_contract_a_to_b", "only_one_contract");
        Skip("document_compare", "only_one_contract");
    }
    // Diagnostic instrumentation is deliberately outside timed iterations.
    if (options.Diagnostics)
        diagnostics = SqlDiagnostics.Collect(cs, doc.Id, directory);
    databaseStable = revision == SqlServerDatabase.Revision(conn);
    if (!databaseStable) exitCode = 4;
    else if (diagnostics.Any(d => d.Status != "OK")) exitCode = 6;
}
catch (Exception ex)
{
    // Do not log provider messages or connection strings.
    failure = ex.GetType().Name;
    exitCode = 5;
    Console.Error.WriteLine("Benchmark stopped: " + failure);
}
var evidence = new
{
    FormatVersion = 1, RunId = runId, StartedUtc = started, FinishedUtc = DateTimeOffset.UtcNow,
    options.SourceSha, options.Topology, options.Runs, ContractId = contractId, PositionCount = positionCount,
    Runtime = RuntimeInformation.FrameworkDescription, Os = RuntimeInformation.OSDescription,
    Architecture = RuntimeInformation.ProcessArchitecture.ToString(), LogicalProcessors = Environment.ProcessorCount,
    SqlClientVersion = typeof(SqlConnection).Assembly.GetName().Version?.ToString() ?? "",
    SqlServerVersion = sqlServerVersion, SchemaVersion = schemaVersion,
    BuildConfiguration =
#if DEBUG
        "Debug",
#else
        "Release",
#endif
    RevisionStable = databaseStable, ExitCode = exitCode, FailureType = failure,
    CacheMode = "warm process; one warmup per case; existing server cache; no cache clearing",
    DiagnosticsRequested = options.Diagnostics, Diagnostics = diagnostics,
    Cases = cases
};
File.WriteAllText(Path.Combine(directory, "evidence.json"),
    JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
using (var csv = new StreamWriter(Path.Combine(directory, "samples.csv")))
{
    csv.WriteLine("case,contract_id,second_contract_id,iteration,status,rows,elapsed_ms,allocated_bytes,error_type");
    foreach (var item in cases)
        foreach (var sample in item.Samples)
            csv.WriteLine(FormattableString.Invariant(
                $"{item.Name},{contractId},{item.SecondContractId},{sample.Iteration},{sample.Status},{sample.Rows},{sample.ElapsedMs:F6},{sample.AllocatedBytes},{sample.ErrorType}"));
}
Console.WriteLine(databaseStable ? "Revision unchanged across run." : "UNCONFIRMED: revision changed or run failed.");
Console.WriteLine("No WPF, write/COMMIT, cold cache or concurrency measurements in this read-only run.");
return exitCode;

static int CountPositions(SqlConnection c, int contractId)
{
    using var cmd = new SqlCommand(
        "SELECT COUNT(*) FROM dbo.positions p JOIN dbo.systems sy ON p.system_id=sy.id WHERE sy.contract_id=@id", c);
    cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
    return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
}

void Skip(string label, string reason) =>
    cases.Add(new BenchmarkCase(label, null, "SKIP", reason, Array.Empty<BenchmarkSample>(), null));

void Measure(string label, Func<int> action, int? secondId = null)
{
    var observations = new List<BenchmarkSample>(options.Runs + 1);
    // Iteration 0 is warmup, recorded separately and excluded from statistics.
    for (var i = 0; i <= options.Runs; i++)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        try
        {
            var rows = action();
            watch.Stop();
            observations.Add(new BenchmarkSample(i, i == 0 ? "WARMUP" : "OK", rows,
                watch.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - allocated, ""));
        }
        catch (Exception ex)
        {
            watch.Stop();
            observations.Add(new BenchmarkSample(i, "FAIL", null, watch.Elapsed.TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocated, ex.GetType().Name));
            cases.Add(new BenchmarkCase(label, secondId, "FAIL", "stopped_on_error", observations, null));
            throw;
        }
    }
    var summary = BenchmarkStatistics.Summarize(observations);
    var status = summary.RowsStable ? "OK" : "UNSTABLE";
    cases.Add(new BenchmarkCase(label, secondId, status, "", observations, summary));
    Console.WriteLine(FormattableString.Invariant(
        $"{label},{status},{summary.Rows},{summary.Min:F2},{summary.P50:F2},{summary.P95:F2},{summary.P99:F2},{summary.Max:F2}"));
    if (!summary.RowsStable) throw new InvalidOperationException("Row counts changed during measurement.");
}
