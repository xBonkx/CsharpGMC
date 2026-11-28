using System.Globalization;
using System.Text.Json;

namespace GmcSmeta.RemoteSqlBench;

// Format 2 adds explicit dataset/profile labels. No connection is opened.
internal sealed record BenchmarkRun
{
    public BenchmarkRun() { } // Explicit public constructor for System.Text.Json.

    public int FormatVersion { get; init; }
    public string RunId { get; init; } = "";
    public string SourceSha { get; init; } = "";
    public string Topology { get; init; } = "";
    public string Dataset { get; init; } = "";
    public string MachineProfile { get; init; } = "";
    public int Runs { get; init; }
    public int ContractId { get; init; }
    public int PositionCount { get; init; }
    public string Runtime { get; init; } = "";
    public string Os { get; init; } = "";
    public string Architecture { get; init; } = "";
    public int LogicalProcessors { get; init; }
    public string SqlClientVersion { get; init; } = "";
    public string SqlServerVersion { get; init; } = "";
    public string SchemaVersion { get; init; } = "";
    public string BuildConfiguration { get; init; } = "";
    public string CacheMode { get; init; } = "";
    public bool RevisionStable { get; init; }
    public int ExitCode { get; init; } = -1;
    public bool DiagnosticsRequested { get; init; }
    public int FailedSamples { get; init; } = -1;
    public int TimeoutSamples { get; init; } = -1;
    public int CancelledSamples { get; init; } = -1;
    public IReadOnlyList<BenchmarkCase> Cases { get; init; } = Array.Empty<BenchmarkCase>();
}

internal sealed record BenchmarkDelta(double Before, double After, double? Percent);
internal sealed record BenchmarkComparisonRow(string Case, int? SecondContractId, int? Rows,
    int BeforeSamples, int AfterSamples, string Status, string Reason,
    BenchmarkDelta? P50, BenchmarkDelta? P95, BenchmarkDelta? P99,
    long? BeforeMedianAllocatedBytes, long? AfterMedianAllocatedBytes,
    BenchmarkResources? BaselineStart, BenchmarkResources? BaselineEnd,
    BenchmarkResources? CurrentStart, BenchmarkResources? CurrentEnd);

internal sealed record BenchmarkComparisonReport(string BaselineRunId, string CurrentRunId,
    string BaselineSha, string CurrentSha, string Dataset, string MachineProfile,
    string Topology, string CacheMode, int ContractId, int PositionCount, int Runs,
    double MaxRegressionPct, string Scope, IReadOnlyList<BenchmarkComparisonRow> Rows)
{
    internal int ResultCode => Rows.Any(r => r.Status == "REGRESSION") ? 4 :
        Rows.Count == 0 || Rows.Any(r => r.Status != "MEASURED") ? 3 : 0;
}

internal static class BenchmarkComparison
{
    internal static BenchmarkComparisonReport Compare(BenchmarkRun before, BenchmarkRun after, double maxRegressionPct)
    {
        if (!double.IsFinite(maxRegressionPct) || maxRegressionPct is < 0 or > 1000)
            throw new ArgumentException("Invalid regression threshold.");
        ValidateRun(before);
        ValidateRun(after);
        Require(before.RunId != after.RunId, "Run IDs must differ.");
        Require(before.Dataset == after.Dataset && before.MachineProfile == after.MachineProfile &&
            before.Topology == after.Topology && before.CacheMode == after.CacheMode &&
            before.ContractId == after.ContractId && before.PositionCount == after.PositionCount &&
            before.Runs == after.Runs && before.Runtime == after.Runtime && before.Os == after.Os &&
            before.Architecture == after.Architecture && before.LogicalProcessors == after.LogicalProcessors &&
            before.SqlClientVersion == after.SqlClientVersion && before.SqlServerVersion == after.SqlServerVersion &&
            before.SchemaVersion == after.SchemaVersion && before.BuildConfiguration == after.BuildConfiguration &&
            before.DiagnosticsRequested == after.DiagnosticsRequested, "Run metadata mismatch.");

        var first = before.Cases.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var second = after.Cases.ToDictionary(c => c.Name, StringComparer.Ordinal);
        var rows = new List<BenchmarkComparisonRow>();
        foreach (var name in first.Keys.Union(second.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            first.TryGetValue(name, out var a);
            second.TryGetValue(name, out var b);
            var baselineCount = a?.Samples?.Count(s => s is not null && s.Iteration > 0 && s.Status == "OK") ?? 0;
            var currentCount = b?.Samples?.Count(s => s is not null && s.Iteration > 0 && s.Status == "OK") ?? 0;
            BenchmarkComparisonRow Incomplete(string reason, string status = "INCOMPLETE") =>
                new(name, a?.SecondContractId, null, baselineCount, currentCount, status, reason,
                    null, null, null, null, null, a?.ResourcesBefore, a?.ResourcesAfter,
                    b?.ResourcesBefore, b?.ResourcesAfter);
            if (a is null || b is null) { rows.Add(Incomplete("missing_case")); continue; }
            if (a.SecondContractId != b.SecondContractId) { rows.Add(Incomplete("second_contract_mismatch")); continue; }
            if (a.Status == "SKIP" && b.Status == "SKIP" && a.Reason == b.Reason)
            { rows.Add(Incomplete("both_skipped", "SKIP")); continue; }
            var x = VerifiedSummary(a, before.Runs);
            var y = VerifiedSummary(b, after.Runs);
            if (x is null || y is null) { rows.Add(Incomplete("invalid_or_incomplete_samples")); continue; }
            if (x.Rows != y.Rows) { rows.Add(Incomplete("row_count_mismatch")); continue; }
            var p50 = Delta(x.P50, y.P50);
            var p95 = Delta(x.P95, y.P95);
            var p99 = x.P99 is double xp && y.P99 is double yp ? Delta(xp, yp) : null;
            var deltas = new[] { p50, p95, p99 }.OfType<BenchmarkDelta>().ToArray();
            var invalid = deltas.Any(d => d.Percent is null);
            var slower = deltas.Any(d => d.Percent is double p && p > maxRegressionPct);
            rows.Add(new(name, a.SecondContractId, x.Rows, x.Count, y.Count,
                slower ? "REGRESSION" : invalid ? "INCOMPLETE" : "MEASURED",
                slower ? "latency_threshold_exceeded" : invalid ? "zero_baseline" : "",
                p50, p95, p99, x.MedianAllocatedBytes, y.MedianAllocatedBytes,
                a.ResourcesBefore, a.ResourcesAfter, b.ResourcesBefore, b.ResourcesAfter));
        }
        return new(before.RunId, after.RunId, before.SourceSha, after.SourceSha, before.Dataset,
            before.MachineProfile, before.Topology, before.CacheMode, before.ContractId, before.PositionCount,
            before.Runs, maxRegressionPct,
            "SQL/client read only; matching row counts, not content parity. Resource snapshots, not peaks. WPF stalls, SQL command count, writes, retention and full correctness NOT MEASURED.",
            rows);
    }

    private static void ValidateRun(BenchmarkRun run)
    {
        Require(run.FormatVersion == 2 && run.ExitCode == 0 && run.RevisionStable &&
            run.FailedSamples == 0 && run.TimeoutSamples == 0 && run.CancelledSamples == 0,
            "Failed, unstable or unsupported evidence.");
        Require(BenchmarkOptions.ValidLabel(run.RunId) &&
            BenchmarkOptions.ValidLabel(run.Dataset) && BenchmarkOptions.ValidLabel(run.MachineProfile) &&
            run.SourceSha is { Length: 40 or 64 } && run.SourceSha.All(Uri.IsHexDigit) &&
            run.Topology is ("LAN" or "WIFI" or "LOCAL" or "VPN" or "SLOW" or "OTHER") &&
            run.Runs is >= 30 and <= 300 && run.ContractId > 0 && run.PositionCount >= 0 &&
            run.LogicalProcessors > 0 && run.BuildConfiguration is ("Release" or "Debug") &&
            new[] { run.CacheMode, run.Runtime, run.Os, run.Architecture, run.SqlClientVersion,
                run.SqlServerVersion, run.SchemaVersion }.All(s => !string.IsNullOrWhiteSpace(s)),
            "Missing or invalid comparison metadata.");
        Require(run.Cases is { Count: > 0 } && run.Cases.All(c => c is not null &&
            BenchmarkOptions.ValidLabel(c.Name)) &&
            run.Cases.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() == run.Cases.Count,
            "Missing or duplicate benchmark cases.");
    }

    private static BenchmarkSummary? VerifiedSummary(BenchmarkCase item, int runs)
    {
        if (item.Status != "OK" || item.Samples is null || item.Samples.Count != runs + 1)
            return null;
        for (var i = 0; i <= runs; i++)
        {
            var s = item.Samples[i];
            if (s is null || s.Iteration != i || s.Status != (i == 0 ? "WARMUP" : "OK") ||
                s.Rows is not >= 0 || !double.IsFinite(s.ElapsedMs) || s.ElapsedMs < 0 ||
                s.AllocatedBytes < 0 || s.ErrorType != "") return null;
        }
        var summary = BenchmarkStatistics.Summarize(item.Samples);
        return summary.RowsStable && summary == item.Summary ? summary : null;
    }

    private static BenchmarkDelta Delta(double before, double after)
    {
        double? pct = before > 0 ? (after - before) / before * 100 : before == after ? 0 : null;
        return new(before, after, pct is double p && !double.IsFinite(p) ? null : pct);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}

internal static class BenchmarkComparisonCli
{
    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length < 3 || (args.Length - 3) % 2 != 0)
                throw new ArgumentException("Invalid comparison options.");
            double threshold = 10;
            string? reportPath = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 3; i < args.Length; i += 2)
            {
                if (!seen.Add(args[i])) throw new ArgumentException("Duplicate comparison option.");
                switch (args[i])
                {
                    case "--max-regression-pct" when double.TryParse(args[i + 1], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var value): threshold = value; break;
                    case "--report" when !string.IsNullOrWhiteSpace(args[i + 1]): reportPath = args[i + 1]; break;
                    default: throw new ArgumentException("Invalid comparison option.");
                }
            }
            BenchmarkRun Read(string path) => JsonSerializer.Deserialize<BenchmarkRun>(File.ReadAllText(path))
                ?? throw new ArgumentException("Missing evidence.");
            var report = BenchmarkComparison.Compare(Read(args[1]), Read(args[2]), threshold);
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (reportPath is not null)
            {
                using var output = new StreamWriter(new FileStream(reportPath, FileMode.CreateNew));
                output.WriteLine(json);
            }
            Console.WriteLine(json);
            return report.ResultCode;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Console.Error.WriteLine("Comparison rejected: " + ex.GetType().Name);
            return 2;
        }
    }
}
