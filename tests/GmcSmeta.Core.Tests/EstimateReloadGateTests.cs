using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateReloadGateTests
{
    [Fact]
    public void OldNetworkReadCannotReplaceNewerRefresh()
    {
        var gate = new EstimateReloadGate();
        var old = gate.NextRequest();
        var current = gate.NextRequest();
        Assert.False(gate.CanApply(old, 0, "rev-a", "rev-a", "rev-a"));
        Assert.True(gate.CanApply(current, 0, "rev-a", "rev-a", "rev-b"));
    }

    [Fact]
    public void PendingWritesPreventOverwritingOptimisticCell()
    {
        var gate = new EstimateReloadGate();
        var ticket = gate.NextRequest();
        Assert.False(gate.CanApply(ticket, 1, "a", "a", "a"));
        Assert.False(gate.CanApply(ticket, 12, "a", "b", "b"));
    }

    [Fact]
    public void LocalSaveDuringReadInvalidatesOldServerSnapshot()
    {
        var gate = new EstimateReloadGate();
        var ticket = gate.NextRequest();
        Assert.False(gate.CanApply(ticket, 0, "rev-old", "rev-new", "rev-old"));
        Assert.True(gate.CanApply(ticket, 0, "rev-old", "rev-new", "rev-new"));
    }

    [Fact]
    public void RemoteChangeWithoutLocalSaveIsAllowed()
    {
        var gate = new EstimateReloadGate();
        var ticket = gate.NextRequest();
        Assert.True(gate.CanApply(ticket, 0, "rev-a", "rev-a", "rev-b"));
    }

    [Theory]
    [InlineData("a", "a", "a", false, true)]
    [InlineData("a", "b", "b", false, false)]
    [InlineData("a", "a", "b", false, false)]
    [InlineData("a", "b", "a", false, false)]
    [InlineData("a", "a", "a", true, false)]
    [InlineData("", "", "", false, false)]
    [InlineData(" ", " ", " ", false, false)]
    [InlineData("a", "a", "", false, false)]
    public void TabReuseRequiresActuallyPublishedCleanRevision(string published,
        string local, string observed, bool readback, bool expected) =>
        Assert.Equal(expected, EstimateReloadGate.CanReusePublishedRevision(
            published, local, observed, readback));

    [Fact]
    public void InvalidTokensNeverApply()
    {
        var gate = new EstimateReloadGate();
        Assert.False(gate.IsLatest(0));
        Assert.False(gate.CanApply(-1, 0, "a", "a", "a"));
    }
}
