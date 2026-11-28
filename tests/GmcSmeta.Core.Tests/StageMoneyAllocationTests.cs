using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class StageMoneyAllocationTests
{
    [Fact]
    public void ParentAndStageRoundingRemainDistinctAtHalfKopeck()
    {
        Assert.Equal(1.00m, StageMoneyAllocation.RoundParent(1.005m));
        Assert.Equal(-1.00m, StageMoneyAllocation.RoundParent(-1.005m));
        Assert.Equal(1.01m, StageMoneyAllocation.Proportional(2.01m, 0.5m));
        Assert.Equal(-1.01m, StageMoneyAllocation.Proportional(-2.01m, 0.5m));
        Assert.Equal(1.00m, StageMoneyAllocation.ParentPercent(10.05m, 10m));
    }

    [Fact]
    public void SharePreservesZeroThresholdAndDoesNotClampOverAllocation()
    {
        Assert.Equal(0m, StageMoneyAllocation.Share(0m, 10m));
        Assert.Equal(0m, StageMoneyAllocation.Share(-10m, 5m));
        Assert.Equal(0m, StageMoneyAllocation.Share(0.000001m, 1m));
        Assert.Equal(0.25m, StageMoneyAllocation.Share(100m, 25m));
        Assert.Equal(1.2m, StageMoneyAllocation.Share(100m, 120m));
        Assert.Equal(-0.2m, StageMoneyAllocation.Share(100m, -20m));
    }

    [Fact]
    public void MaterialLineHonorsFormRoundingBeforeAggregation()
    {
        Assert.Equal(0.006m, StageMoneyAllocation.MaterialLine(1m, 0.006m, false));
        Assert.Equal(0.01m, StageMoneyAllocation.MaterialLine(1m, 0.006m, true));
        Assert.Equal(-0.01m, StageMoneyAllocation.MaterialLine(-1m, 0.005m, true));
    }
}
