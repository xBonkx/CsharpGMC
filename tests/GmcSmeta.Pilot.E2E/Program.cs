using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--verify-reopen")
            return PilotRunner.VerifyFreshProcess();
        return new PilotRunner().Run();
    }
}

internal sealed class PilotRunner
{
    private sealed record Outcome(string Scenario, string Status, string Detail);
    private sealed record Metric(string Operation, int Rows, int Samples,
        double P50Ms, double P95Ms, double P99Ms, string Scope);
    private readonly List<Outcome> _outcomes = [];
    private readonly List<Metric> _metrics = [];
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly TimeSpan _initialCpu = Process.GetCurrentProcess().TotalProcessorTime;
    internal readonly string Token = Guid.NewGuid().ToString("N");
    internal string DatabaseName => "GmcSmetaPilot_E2E_" + Token[..12];
    internal string PilotConnection = "";
    internal string ServerName = "";
    private string _version = "";
    internal long ObjectId, SmallContract, LargeContract, SmallSystem, LargeSystem, FirstPosition;
    internal string ReopenMarker = "";
    private SqlConnectionStringBuilder? _admin;
    private bool _created;

    public int Run()
    {
        try
        {
            var raw = Environment.GetEnvironmentVariable("GMC_PILOT_E2E_ADMIN");
            if (string.IsNullOrWhiteSpace(raw))
            {
                Skip("sql-server", "GMC_PILOT_E2E_ADMIN absent; SQL was NOT tested");
                return 2;
            }
            _admin = ValidateAdmin(raw);
            using (var master = ConnectWithRetry(_admin.ConnectionString))
            {
                using var info = new SqlCommand(
                    "SELECT DB_NAME(), CONVERT(nvarchar(256),@@SERVERNAME)," +
                    "CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion'))", master);
                using var r = info.ExecuteReader();
                Check(r.Read() && r.GetString(0) == "master", "Admin database must be master");
                ServerName = r.GetString(1);
                _version = r.GetString(2);
                Check(ServerName.Length > 0, "Unknown SQL Server identity");
                var expected = Environment.GetEnvironmentVariable("GMC_PILOT_E2E_EXPECTED_SERVER");
                if (!string.IsNullOrWhiteSpace(expected))
                    Check(string.Equals(ServerName, expected, StringComparison.OrdinalIgnoreCase),
                        "Wrong actual @@SERVERNAME");
                r.Close();
                Check(Scalar(master, "SELECT DB_ID(@db)", ("@db", DatabaseName)) is DBNull,
                    "Generated database name already exists");
                Execute(master, "CREATE DATABASE [" + DatabaseName + "]");
                _created = true;
            }
            PilotConnection = new SqlConnectionStringBuilder(_admin.ConnectionString)
            { InitialCatalog = DatabaseName, ApplicationName = "GmcSmetaPilot E2E" }.ConnectionString;
            using (var conn = ConnectWithRetry(PilotConnection))
            {
                using var schema = new SqlCommand(PilotSchema.Ddl, conn) { CommandTimeout = 120 };
                schema.ExecuteNonQuery();
                Execute(conn, "INSERT dbo.__pilot_provenance(token,database_name,server_name)" +
                    " VALUES(@token,DB_NAME(),@@SERVERNAME)", ("@token", Token));
                RequireOwned(conn);
            }
            Pass("sql-server-and-v28-fixture", "Real SQL Server / nonce-marked synthetic subset");
            PilotCases.RunAll(this);
        }
        catch (Exception ex) { Fail("infrastructure", Error(ex)); }
        finally
        {
            CleanupOwnedDatabase();
            Report();
        }
        return _outcomes.Any(x => x.Status == "FAIL") ? 1 : 0;
    }

    private static SqlConnectionStringBuilder ValidateAdmin(string raw)
    {
        var b = new SqlConnectionStringBuilder(raw);
        Check(string.Equals(b.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase),
            "Only explicit Initial Catalog=master is accepted for ephemeral DB provisioning");
        var host = b.DataSource.Trim().ToLowerInvariant();
        var local = host is "." or "(local)" or "localhost" or "127.0.0.1" or "::1" ||
            host.StartsWith("localhost,") || host.StartsWith("127.0.0.1,") ||
            host.StartsWith(".\\") || host.StartsWith("(local)\\");
        if (!local)
            Check(Environment.GetEnvironmentVariable("GMC_PILOT_E2E_ALLOW_EXTERNAL") == "1" &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GMC_PILOT_E2E_EXPECTED_SERVER")),
                "Non-local server denied; explicitly opt in and specify @@SERVERNAME");
        b.ConnectTimeout = 5;
        b.ApplicationName = "GmcSmetaPilot E2E admin";
        return b;
    }

    internal static SqlConnection ConnectWithRetry(string cs)
    {
        for (var i = 0; i < 35; i++)
        {
            var conn = new SqlConnection(cs);
            try { conn.Open(); return conn; }
            catch (SqlException) when (i < 34) {
                conn.Dispose();
                Thread.Sleep(2000);
            }
            catch { conn.Dispose(); throw; }
        }
        throw new InvalidOperationException("Unreachable SQL retry state");
    }

    internal SqlConnection Conn()
    {
        var conn = SqlServerDatabase.Open(PilotConnection);
        try { RequireOwned(conn); return conn; }
        catch { conn.Dispose(); throw; }
    }

    internal void RequireOwned(SqlConnection connection)
    {
        using var cmd = new SqlCommand(
            "SELECT DB_NAME(), CONVERT(nvarchar(256),@@SERVERNAME)," +
            "(SELECT COUNT_BIG(*) FROM dbo.__pilot_provenance WHERE token=@token " +
            "AND database_name=DB_NAME() AND server_name=@@SERVERNAME)", connection);
        cmd.Parameters.Add("@token", SqlDbType.NVarChar, 40).Value = Token;
        using var r = cmd.ExecuteReader();
        Check(r.Read() && r.GetString(0) == DatabaseName &&
            string.Equals(r.GetString(1), ServerName, StringComparison.Ordinal) &&
            r.GetInt64(2) == 1, "Database/server/provenance mismatch; write rejected");
    }

    internal string Revision()
    {
        using var conn = Conn();
        return SqlServerDatabase.Revision(conn);
    }

    internal void RunCase(string name, Action action)
    {
        try { action(); Pass(name, "Verified against disposable SQL Server"); }
        catch (Exception e) { Fail(name, Error(e)); }
    }
    internal void Skip(string name, string reason)
    {
        _outcomes.Add(new Outcome(name, "SKIP", reason));
        Console.WriteLine("SKIP " + name + " -- " + reason);
    }
    private void Pass(string name, string detail)
    {
        _outcomes.Add(new Outcome(name, "PASS", detail));
        Console.WriteLine("PASS " + name);
    }
    private void Fail(string name, string detail)
    {
        _outcomes.Add(new Outcome(name, "FAIL", detail));
        Console.Error.WriteLine("FAIL " + name + " -- " + detail);
    }
    internal static void Check(bool predicate, string why)
    {
        if (!predicate) throw new InvalidOperationException(why);
    }
    internal static string Error(Exception ex) =>
        Regex.Replace(ex.GetType().Name + ": " + ex.Message,
            @"(?i)(Password|Pwd)\s*=\s*[^;\s]+", "$1=[REDACTED]");

    internal static object? Scalar(SqlConnection conn, string sql,
        params (string Key, object Value)[] values)
    {
        using var cmd = new SqlCommand(sql, conn);
        foreach (var item in values) cmd.Parameters.AddWithValue(item.Key, item.Value);
        return cmd.ExecuteScalar();
    }
    internal static long Long(SqlConnection conn, string sql,
        params (string Key, object Value)[] values) =>
        Convert.ToInt64(Scalar(conn, sql, values), CultureInfo.InvariantCulture);
    internal static double Double(SqlConnection conn, string sql,
        params (string Key, object Value)[] values) =>
        Convert.ToDouble(Scalar(conn, sql, values), CultureInfo.InvariantCulture);
    internal static string Text(SqlConnection conn, string sql,
        params (string Key, object Value)[] values) =>
        Convert.ToString(Scalar(conn, sql, values), CultureInfo.InvariantCulture) ?? "";
    internal static void Execute(SqlConnection conn, string sql,
        params (string Key, object Value)[] values)
    {
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        foreach (var item in values) cmd.Parameters.AddWithValue(item.Key, item.Value);
        cmd.ExecuteNonQuery();
    }

    internal void MetricSamples(string operation, int rows, List<double> samples, string scope)
    {
        Check(samples.Count > 0, "Cannot calculate percentiles from empty timing sample");
        samples.Sort();
        double Q(double p) => samples[Math.Clamp((int)Math.Ceiling(samples.Count * p) - 1,
            0, samples.Count - 1)];
        _metrics.Add(new Metric(operation, rows, samples.Count, Q(.5), Q(.95), Q(.99), scope));
    }

    internal void MetricOne(string operation, int rows, double ms, string scope)
    {
        _metrics.Add(new Metric(operation, rows, 1, ms, ms, ms,
            scope + " (single sample; p50/p95/p99 are NOT statistically meaningful)"));
    }

    internal void VerifyViaNewProcess()
    {
        Check(ReopenMarker.Length > 0, "Lost acknowledgement scenario was not executed");
        var info = new ProcessStartInfo("dotnet") {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--verify-reopen");
        info.Environment["GMC_PILOT_E2E_CHILD_CONNECTION"] = PilotConnection;
        info.Environment["GMC_PILOT_E2E_CHILD_POSITION"] =
            FirstPosition.ToString(CultureInfo.InvariantCulture);
        info.Environment["GMC_PILOT_E2E_CHILD_EXPECTED"] = ReopenMarker;
        using var child = Process.Start(info) ??
            throw new InvalidOperationException("Unable to create new worker process");
        if (!child.WaitForExit(60000))
        {
            child.Kill(entireProcessTree: true);
            throw new TimeoutException("Child worker exceeded 60 seconds");
        }
        Check(child.ExitCode == 0, "Fresh worker failed persistence check, exit=" +
            child.ExitCode.ToString(CultureInfo.InvariantCulture));
    }

    internal static int VerifyFreshProcess()
    {
        try
        {
            var cs = Environment.GetEnvironmentVariable("GMC_PILOT_E2E_CHILD_CONNECTION") ?? "";
            var db = new SqlConnectionStringBuilder(cs).InitialCatalog;
            Check(db.StartsWith("GmcSmetaPilot_E2E_", StringComparison.Ordinal),
                "Not an isolated pilot database");
            var id = long.Parse(Environment.GetEnvironmentVariable("GMC_PILOT_E2E_CHILD_POSITION")
                ?? "", CultureInfo.InvariantCulture);
            var expected = Environment.GetEnvironmentVariable("GMC_PILOT_E2E_CHILD_EXPECTED") ?? "";
            using var conn = SqlServerDatabase.Open(cs);
            Check(Text(conn, "SELECT note FROM dbo.positions WHERE id=@id", ("@id", id)) ==
                expected, "Restarted worker lost persisted data");
            Check(SqlServerDatabase.Revision(conn).Length > 0, "Missing revision after restart");
            return 0;
        }
        catch { return 1; }
    }

    private void CleanupOwnedDatabase()
    {
        if (!_created || _admin is null) return;
        try
        {
            // Fail closed: if schema/provenance was not written, leave DB for manual inspection.
            using (var verify = new SqlConnection(PilotConnection))
            {
                verify.Open();
                RequireOwned(verify);
            }
            SqlConnection.ClearAllPools();
            using var master = new SqlConnection(_admin.ConnectionString);
            master.Open();
            Check(Text(master, "SELECT DB_NAME()") == "master" &&
                Text(master, "SELECT CONVERT(nvarchar(256),@@SERVERNAME)") == ServerName,
                "Cleanup redirected to another SQL server");
            Execute(master, "ALTER DATABASE [" + DatabaseName + "] " +
                "SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + DatabaseName + "]");
            Pass("owned-database-cleanup", "Only this process' nonce-marked DB was dropped");
        }
        catch (Exception ex)
        {
            Fail("owned-database-cleanup", Error(ex) +
                "; check isolated runner manually, unowned DB must never be dropped");
        }
    }

    private void Report()
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("GMC_PILOT_E2E_REPORT_DIR")
                ?? Path.Combine("artifacts", "sql-pilot-e2e");
            Directory.CreateDirectory(dir);
            var sha = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? LocalSha();
            var pass = _outcomes.Count(o => o.Status == "PASS");
            var fail = _outcomes.Count(o => o.Status == "FAIL");
            var skip = _outcomes.Count(o => o.Status == "SKIP");
            _elapsed.Stop();
            var cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - _initialCpu).TotalMilliseconds;
            var output = new {
                git_sha = sha, at_utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                elapsed_ms = Math.Round(_elapsed.Elapsed.TotalMilliseconds, 2),
                process_cpu_ms = Math.Round(Math.Max(0, cpuMs), 2),
                network_bytes = (long?)null,
                network_note = "Network byte counters unavailable; SQL API latency includes round trips",
                dotnet = Environment.Version.ToString(),
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                cpus = Environment.ProcessorCount,
                sql_server = ServerName, sql_version = _version,
                database = _created ? DatabaseName : "(not created)",
                fixture_kind = "synthetic partial schema v28, not production migration",
                pass, fail, skip, scenarios = _outcomes, measurements = _metrics
            };
            File.WriteAllText(Path.Combine(dir, "report.json"),
                JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
            static string Esc(string s) => s.Replace("|", "\\|").Replace('\n', ' ').Replace('\r', ' ');
            var lines = new List<string> {
                "# GMC SQL Pilot E2E", "",
                "Git SHA: " + sha, "SQL Server: " + ServerName + " (" + _version + ")",
                "Disposable database: " + (_created ? DatabaseName : "(not created)"),
                "Wall elapsed ms: " + _elapsed.Elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture),
                "Process CPU ms: " + Math.Max(0, cpuMs).ToString("F2", CultureInfo.InvariantCulture),
                "Network bytes: not instrumented (no claim)", "",
                "PASS=" + pass + "; FAIL=" + fail + "; SKIP=" + skip, "",
                "| Scenario | Status | Evidence / limitation |",
                "|---|---|---|"
            };
            foreach (var item in _outcomes)
                lines.Add("| " + Esc(item.Scenario) + " | " + item.Status +
                    " | " + Esc(item.Detail) + " |");
            lines.AddRange(["", "| Operation | Rows | Samples | p50 ms | p95 ms | p99 ms | Scope |",
                "|---|---:|---:|---:|---:|---:|---|"]);
            foreach (var m in _metrics)
                lines.Add("| " + Esc(m.Operation) + " | " + m.Rows + " | " + m.Samples +
                    " | " + m.P50Ms.ToString("F2", CultureInfo.InvariantCulture) +
                    " | " + m.P95Ms.ToString("F2", CultureInfo.InvariantCulture) +
                    " | " + m.P99Ms.ToString("F2", CultureInfo.InvariantCulture) +
                    " | " + Esc(m.Scope) + " |");
            File.WriteAllLines(Path.Combine(dir, "report.md"), lines);
            Console.WriteLine("RESULT pass=" + pass + " fail=" + fail + " skip=" + skip);
        }
        catch (Exception ex) { Fail("report-write", Error(ex)); }
    }

    private static string LocalSha()
    {
        try {
            var info = new ProcessStartInfo("git") {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.ArgumentList.Add("rev-parse");
            info.ArgumentList.Add("HEAD");
            using var process = Process.Start(info);
            if (process is null) return "unknown";
            var result = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(3000);
            return process.ExitCode == 0 ? result : "unknown";
        }
        catch { return "unknown"; }
    }
}
