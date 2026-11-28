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
            "--sha", "abcdef0123456789", "--topology", "LAN", "--out", "test-output" });
        Assert.Equal(100, options.Runs);
        Assert.Equal(42, options.ContractId);
        Assert.Equal("LAN", options.Topology);
        Assert.Equal("abcdef0123456789", options.SourceSha);
        Assert.Equal("test-output", options.OutputDirectory);
        Assert.Throws<ArgumentException>(() => BenchmarkOptions.Parse(new[] { "--runs", "30", "--runs", "40" }));
    }
}
