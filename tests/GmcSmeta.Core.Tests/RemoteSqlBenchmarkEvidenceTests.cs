using GmcSmeta.RemoteSqlBench;

namespace GmcSmeta.Core.Tests;

public sealed class RemoteSqlBenchmarkEvidenceTests
{
    [Fact]
    public void WarmupAndFailuresAreNotIncludedAndSmallSampleHasNoP99()
    {
        var rows = new[] { new BenchmarkSample(0, "WARMUP", 5, 999, 999, ""),
            new BenchmarkSample(4, "FAIL", null, 999, 999, "SqlException") }
            .Concat(Enumerable.Range(1, 3).Select(i => new BenchmarkSample(i, "OK", 5, i, i * 10, "")));
        var s = BenchmarkStatistics.Summarize(rows);
        Assert.Equal(3, s.Count);
        Assert.Equal(2d, s.P50);
        Assert.Equal(3d, s.P95);
        Assert.Null(s.P99);
        Assert.Equal(20L, s.MedianAllocatedBytes);
        Assert.True(s.RowsStable);
    }

    [Fact]
    public void HundredSamplesReportNearestRankP99()
    {
        var s = BenchmarkStatistics.Summarize(Enumerable.Range(1, 100)
            .Select(i => new BenchmarkSample(i, "OK", 25000, i, i, "")));
        Assert.Equal(50.5d, s.P50);
        Assert.Equal(95d, s.P95);
        Assert.Equal(99d, s.P99);
        Assert.Equal(100d, s.Max);
    }

    [Fact]
    public void ChangedRowCountIsNotReportedAsStable()
    {
        var s = BenchmarkStatistics.Summarize(new[] {
            new BenchmarkSample(1, "OK", 300, 1, 0, ""),
            new BenchmarkSample(2, "OK", 301, 1, 0, "") });
        Assert.False(s.RowsStable);
        Assert.Null(s.Rows);
    }

    [Theory]
    [InlineData("--runs", "19")]
    [InlineData("--runs", "29")]
    [InlineData("--dataset", "UNSPECIFIED")]
    [InlineData("--machine-profile", "name with spaces")]
    [InlineData("--runs", "301")]
    [InlineData("--contract-id", "0")]
    [InlineData("--sha", "not-a-sha")]
    [InlineData("--topology", "unknown")]
    [InlineData("--unknown", "value")]
    public void InvalidOptionsFailBeforeConnecting(string key, string value) =>
        Assert.Throws<ArgumentException>(() => BenchmarkOptions.Parse(new[] { key, value }));

    [Fact]
    public void OptionsPreserveEvidenceIdentity()
    {
        var options = BenchmarkOptions.Parse(new[] { "--runs", "100", "--contract-id", "42",
            "--sha", "abcdef0123456789", "--topology", "LAN", "--out", "test-output",
            "--dataset", "seed-v1", "--machine-profile", "pc-lan-1" });
        Assert.Equal(100, options.Runs);
        Assert.Equal(42, options.ContractId);
        Assert.Equal("LAN", options.Topology);
        Assert.Equal("abcdef0123456789", options.SourceSha);
        Assert.Equal("test-output", options.OutputDirectory);
        Assert.Equal("seed-v1", options.Dataset);
        Assert.Equal("pc-lan-1", options.MachineProfile);
        Assert.Equal("SLOW", BenchmarkOptions.Parse(new[] { "--topology", "SLOW" }).Topology);
        Assert.Throws<ArgumentException>(() => BenchmarkOptions.Parse(new[] { "--runs", "30", "--runs", "40" }));
    }

    [Fact]
    public void DiagnosticsAreExplicitlyOptIn()
    {
        Assert.False(BenchmarkOptions.Parse(Array.Empty<string>()).Diagnostics);
        Assert.True(BenchmarkOptions.Parse(new[] { "--diagnostics", "yes" }).Diagnostics);
        Assert.False(BenchmarkOptions.Parse(new[] { "--diagnostics", "no" }).Diagnostics);
        Assert.Throws<ArgumentException>(() => BenchmarkOptions.Parse(new[] { "--diagnostics", "true" }));
    }

    [Fact]
    public void DiagnosticsRejectUnsafeDatabaseBeforeOpeningSql()
    {
        Assert.ThrowsAny<Exception>(() => SqlDiagnostics.Collect(
            "Server=localhost;Database=master;Integrated Security=true", 1, "unused"));
    }

    [Fact]
    public void MetadataQueriesAreScopedAndReferenceUsesAllFourMovementKinds()
    {
        foreach (var table in new[] { "systems", "positions", "contracts", "movements",
            "procurement", "acts", "act_lines", "shipment_lines", "spec_revisions" })
        {
            Assert.Contains("N'" + table + "'", SqlDiagnostics.IndexInventorySql);
            Assert.Contains("N'" + table + "'", SqlDiagnostics.StatisticsInventorySql);
        }
        Assert.Contains("WHERE sy.contract_id=@id", SqlDiagnostics.MovementReferenceSql);
        foreach (var kind in new[] { "in", "out", "back", "back_supplier" })
            Assert.Contains("N'" + kind + "'", SqlDiagnostics.MovementReferenceSql);
    }

    [Fact]
    public void MovementParityRequiresExactMappedValuesAndPositionIdentity()
    {
        var item = new SqlDiagnostics.MovementFields(17, 7m, 2m, new DateTime(2026, 11, 28));
        Assert.True(SqlDiagnostics.SameMovement(item, item with { }));
        Assert.False(SqlDiagnostics.SameMovement(item, item with { PositionId = 18 }));
        Assert.False(SqlDiagnostics.SameMovement(item, item with { Received = 7.000001m }));
        Assert.False(SqlDiagnostics.SameMovement(item, item with { Issued = 1m }));
        Assert.False(SqlDiagnostics.SameMovement(item, item with { LastIssue = null }));
    }

    [Theory]
    [InlineData("Table 'positions'. Scan count 1, logical reads 10", true)]
    [InlineData("SQL Server Execution Times: CPU time = 0 ms, elapsed time = 2 ms.", true)]
    [InlineData("Changed database context to 'private'.", false)]
    [InlineData("Unrelated logical reads message", false)]
    public void InfoMessageFilterKeepsOnlyStatistics(string message, bool expected) =>
        Assert.Equal(expected, SqlDiagnostics.IsStatisticsMessage(message));
}
