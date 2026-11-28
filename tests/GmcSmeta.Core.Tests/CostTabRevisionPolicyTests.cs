using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class CostTabRevisionPolicyTests
{
    [Theory]
    [InlineData("r", "r", "r", false, true)]
    [InlineData("r", "r", "foreign", false, false)]
    [InlineData("r", "old", "r", false, false)]
    [InlineData("", "", "", false, false)]
    [InlineData("r", "r", "r", true, false)]
    public void ReuseRequiresLiveObservedRevision(string expected, string cached, string observed, bool forced, bool reuse) =>
        Assert.Equal(reuse, CostTabRevisionPolicy.CanReuse(expected, cached, observed, forced));

    [Theory]
    [InlineData("r", "r", "r", 0, true, false)]
    [InlineData("r", "r", "foreign", 0, false, true)]
    [InlineData("r", "new", "r", 0, false, false)]
    [InlineData("r", "r", "r", 1, false, false)]
    [InlineData("r", "r", "foreign", 1, false, false)]
    [InlineData("", "", "", 0, false, false)]
    public void PublicationRejectsForeignOrPendingRevision(string expected, string current, string observed,
        int pending, bool publish, bool reconcile)
    {
        Assert.Equal(publish, CostTabRevisionPolicy.CanPublish(expected, current, observed, pending));
        Assert.Equal(reconcile, CostTabRevisionPolicy.NeedsParentReadback(expected, current, observed, pending));
    }
}
