using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ActQuantityPolicyTests
{
    [Theory]
    [InlineData(10, 4, 6)]
    [InlineData(10, 10, 0)]
    [InlineData(10, 12, 0)]
    [InlineData(0, 0, 0)]
    public void Remaining_SubtractsPreviouslyClosedInsteadOfAddingIt(
        decimal byKp, decimal closed, decimal expected) =>
        Assert.Equal(expected, ActQuantityPolicy.Remaining(byKp, closed));

    [Fact]
    public void ExceedsKp_AllowsOnlyUnclosedBalanceAndReductions()
    {
        Assert.False(ActQuantityPolicy.ExceedsKp(6m, 10m, 4m, 2m));
        Assert.True(ActQuantityPolicy.ExceedsKp(7m, 10m, 4m, 2m));
        Assert.False(ActQuantityPolicy.ExceedsKp(8m, 10m, 4m, 9m)); // repair old excess
        Assert.True(ActQuantityPolicy.ExceedsKp(10m, 10m, 4m, 9m)); // increase old excess
    }

    [Fact]
    public void FillRemaining_ExcludesOutsideEstimateRowsAndFullyClosedPositions()
    {
        var rows = new (long PositionId, decimal QuantityByKp, decimal ClosedInOtherActs, bool OutsideKp)[]
        {
            (1, 10m, 3m, false), (2, 5m, 7m, false),
            (3, 0m, 0m, true), (4, 1m, 0m, true),
            (5, 4m, 0m, false)
        };
        var plan = ActQuantityPolicy.PlanFillRemaining(rows);
        Assert.Equal(new long[] { 1, 5 }, plan.Select(x => x.PositionId));
        Assert.Equal(new decimal[] { 7m, 4m }, plan.Select(x => x.Quantity));
    }

    [Fact]
    public void FillRemaining_EmptyInputIsSafe()
    {
        Assert.Empty(ActQuantityPolicy.PlanFillRemaining(Array.Empty<
            (long PositionId, decimal QuantityByKp, decimal ClosedInOtherActs, bool OutsideKp)>()));
    }
}
