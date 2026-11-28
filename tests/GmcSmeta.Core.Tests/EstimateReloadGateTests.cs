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
        Assert.False(gate.CanApply(ticket, 0, "rev-old", "rev-new", "rev-new"));
        var retry = gate.NextRequest();
        Assert.True(gate.CanApply(retry, 0, "rev-new", "rev-new", "rev-new"));
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

    [Theory]
    [InlineData(true, true, false, false, false, true)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(true, true, true, false, false, false)]
    [InlineData(true, true, false, true, false, false)]
    [InlineData(true, true, false, false, true, false)]
    public void OnlyRedundantCleanReadsCanCoalesce(bool ordinary, bool active,
        bool pending, bool editing, bool attach, bool expected) =>
        Assert.Equal(expected, EstimateReloadGate.CanCoalesceOrdinaryRead(ordinary, active, pending, editing, attach));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingServerRevisionCannotBePublished(string revision)
    {
        var gate = new EstimateReloadGate();
        Assert.False(gate.CanApply(gate.NextRequest(), 0, "a", "a", revision));
    }
}
