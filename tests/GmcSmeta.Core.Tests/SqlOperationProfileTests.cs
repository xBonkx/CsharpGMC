using System.Collections;
using GmcSmeta.RemoteSqlBench;

namespace GmcSmeta.Core.Tests;

public sealed class SqlOperationProfileTests
{
    [Fact]
    public void MissingProviderCounterIsUnknownNotZero()
    {
        var counters = new Hashtable { ["BytesReceived"] = 17L, ["SelectCount"] = 0L };
        Assert.Equal(17L, SqlOperationProfile.Counter(counters, "BytesReceived"));
        Assert.Equal(0L, SqlOperationProfile.Counter(counters, "SelectCount"));
        Assert.Null(SqlOperationProfile.Counter(counters, "ServerRoundtrips"));
    }

    [Fact]
    public void SessionDeltaRejectsCounterReset()
    {
        var a = new SqlOperationProfile.SessionCounters(1, 2, 3, 4);
        var b = new SqlOperationProfile.SessionCounters(5, 6, 7, 8);
        Assert.Equal(new SqlOperationProfile.SessionCounters(4, 4, 4, 4), SqlOperationProfile.Difference(a, b));
        Assert.Null(SqlOperationProfile.Difference(b, a));
        Assert.Null(SqlOperationProfile.Difference(null, b));
    }

    [Fact]
    public void WaitDeltaRequiresStableVisibleCounters()
    {
        var a = new Dictionary<string, SqlOperationProfile.WaitCounter> { ["LCK_M_S"] = new(1, 20) };
        var b = new Dictionary<string, SqlOperationProfile.WaitCounter> { ["LCK_M_S"] = new(2, 50), ["ASYNC_NETWORK_IO"] = new(1, 5) };
        var delta = SqlOperationProfile.DifferenceWaits(a, b)!;
        Assert.Equal(new SqlOperationProfile.WaitCounter(1, 30), delta["LCK_M_S"]);
        Assert.Equal(new SqlOperationProfile.WaitCounter(1, 5), delta["ASYNC_NETWORK_IO"]);
        Assert.Null(SqlOperationProfile.DifferenceWaits(b, a));
        Assert.Null(SqlOperationProfile.DifferenceWaits(null, b));
    }

    [Fact]
    public void EvenAllocationMedianUsesBothMiddleValues()
    {
        var data = new[] { new BenchmarkSample(1, "OK", 1, 1, 20, ""), new BenchmarkSample(2, "OK", 1, 2, 40, "") };
        Assert.Equal(30L, BenchmarkStatistics.Summarize(data).MedianAllocatedBytes);
    }

    [Fact]
    public void InvalidCompletedMetricsAreNotSummarized()
    {
        Assert.Throws<ArgumentException>(() => BenchmarkStatistics.Summarize(new[] {
            new BenchmarkSample(1, "OK", 1, double.NaN, 0, "") }));
    }
}
