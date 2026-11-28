using GmcSmeta.RemoteSqlBench;

namespace GmcSmeta.Core.Tests;

public sealed class RemoteSqlBenchmarkComparisonTests
{
    private static BenchmarkCase Case(double elapsed, int runs = 30, int rows = 300)
    {
        var samples = Enumerable.Range(0, runs + 1).Select(i =>
            new BenchmarkSample(i, i == 0 ? "WARMUP" : "OK", rows, elapsed, 120, "")).ToArray();
        return new("full_estimate_refresh", null, "OK", "", samples, BenchmarkStatistics.Summarize(samples));
    }

    private static BenchmarkRun Run(string id, BenchmarkCase item, int runs = 30) => new()
    {
        FormatVersion = 2, RunId = id, SourceSha = new string('a', 40), Topology = "LAN",
        Dataset = "seed-1", MachineProfile = "pc-1", Runs = runs, ContractId = 42, PositionCount = 300,
        Runtime = ".NET 10", Os = "Windows", Architecture = "X64", LogicalProcessors = 8,
        SqlClientVersion = "1", SqlServerVersion = "2", SchemaVersion = "32", BuildConfiguration = "Release",
        CacheMode = "warm", RevisionStable = true, ExitCode = 0,
        FailedSamples = 0, TimeoutSamples = 0, CancelledSamples = 0, Cases = new[] { item }
    };

    [Fact]
    public void ComparesRawSamplesAndDoesNotInventP99()
    {
        var r = BenchmarkComparison.Compare(Run("a", Case(100)), Run("b", Case(80)), 10);
        var row = Assert.Single(r.Rows);
        Assert.Equal("MEASURED", row.Status);
        Assert.Equal(-20d, row.P50!.Percent);
        Assert.Equal(-20d, row.P95!.Percent);
        Assert.Null(row.P99);
        Assert.Equal(0, r.ResultCode);
    }

    [Fact]
    public void HundredSamplesFlagRegressionIncludingP99()
    {
        var r = BenchmarkComparison.Compare(Run("a", Case(100, 100), 100),
            Run("b", Case(125, 100), 100), 10);
        Assert.Equal(25d, Assert.Single(r.Rows).P99!.Percent);
        Assert.Equal(4, r.ResultCode);
    }

    [Theory]
    [InlineData("dataset")]
    [InlineData("machine")]
    [InlineData("network")]
    [InlineData("build")]
    [InlineData("schema")]
    [InlineData("runs")]
    [InlineData("unstable")]
    [InlineData("timeout")]
    [InlineData("sha")]
    [InlineData("old-format")]
    [InlineData("same-run")]
    public void RejectsIncomparableRunMetadata(string change)
    {
        var a = Run("a", Case(100));
        var b = Run("b", Case(100));
        b = change switch
        {
            "dataset" => b with { Dataset = "other" },
            "machine" => b with { MachineProfile = "other" },
            "network" => b with { Topology = "WIFI" },
            "build" => b with { BuildConfiguration = "Debug" },
            "schema" => b with { SchemaVersion = "31" },
            "runs" => b with { Runs = 40 },
            "unstable" => b with { RevisionStable = false },
            "timeout" => b with { TimeoutSamples = 1 },
            "sha" => b with { SourceSha = "UNSPECIFIED" },
            "old-format" => b with { FormatVersion = 1 },
            "same-run" => b with { RunId = "a" },
            _ => throw new ArgumentException()
        };
        Assert.Throws<ArgumentException>(() => BenchmarkComparison.Compare(a, b, 10));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("summary")]
    [InlineData("iteration")]
    [InlineData("rows")]
    [InlineData("second-contract")]
    [InlineData("skip")]
    [InlineData("nan")]
    public void IncompleteCasesCannotPass(string change)
    {
        var a = Run("a", Case(100));
        var item = Case(80);
        item = change switch
        {
            "count" => item with { Samples = item.Samples.Take(30).ToArray() },
            "summary" => item with { Summary = item.Summary! with { P95 = 1 } },
            "iteration" => item with { Samples = item.Samples.Reverse().ToArray() },
            "rows" => Case(80, rows: 301),
            "second-contract" => item with { SecondContractId = 8 },
            "skip" => item with { Status = "SKIP", Samples = Array.Empty<BenchmarkSample>(), Summary = null },
            "nan" => item with { Samples = item.Samples.Select(s => s with { ElapsedMs = double.NaN }).ToArray() },
            _ => throw new ArgumentException()
        };
        var r = BenchmarkComparison.Compare(a, Run("b", item), 10);
        Assert.Equal("INCOMPLETE", Assert.Single(r.Rows).Status);
        Assert.Equal(3, r.ResultCode);
    }

    [Fact]
    public void BothSkippedAndMissingCaseAreReportedWithoutPass()
    {
        var skip = new BenchmarkCase("sample_25000", null, "SKIP", "insufficient_rows",
            Array.Empty<BenchmarkSample>(), null);
        var a = Run("a", Case(100)) with { Cases = new[] { Case(100), skip } };
        var b = Run("b", skip);
        var r = BenchmarkComparison.Compare(a, b, 10);
        Assert.Contains(r.Rows, row => row.Status == "SKIP");
        Assert.Contains(r.Rows, row => row.Status == "INCOMPLETE");
        Assert.Equal(3, r.ResultCode);
    }

    [Fact]
    public void ZeroBaselineDoesNotClaimAcceleration()
    {
        var r = BenchmarkComparison.Compare(Run("a", Case(0)), Run("b", Case(1)), 10);
        Assert.Null(Assert.Single(r.Rows).P50!.Percent);
        Assert.Equal(3, r.ResultCode);
    }

    [Fact]
    public void DuplicateCasesRejectRun()
    {
        var a = Run("a", Case(100)) with { Cases = new[] { Case(100), Case(100) } };
        Assert.Throws<ArgumentException>(() => BenchmarkComparison.Compare(a, Run("b", Case(100)), 10));
    }

    [Fact]
    public void EvidenceJsonRoundTripAndOfflineCliRefusesOverwrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "gmc-bench-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var before = Path.Combine(root, "a.json");
            var after = Path.Combine(root, "b.json");
            var report = Path.Combine(root, "report.json");
            File.WriteAllText(before, System.Text.Json.JsonSerializer.Serialize(Run("a", Case(100))));
            File.WriteAllText(after, System.Text.Json.JsonSerializer.Serialize(Run("b", Case(90))));
            var args = new[] { "--compare", before, after, "--report", report };
            Assert.Equal(0, BenchmarkComparisonCli.Run(args));
            var original = File.ReadAllText(report);
            Assert.Contains("MEASURED", original);
            Assert.Equal(2, BenchmarkComparisonCli.Run(args));
            Assert.Equal(original, File.ReadAllText(report));
        }
        finally { Directory.Delete(root, true); }
    }
}
