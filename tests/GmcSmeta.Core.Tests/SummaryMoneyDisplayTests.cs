using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SummaryMoneyDisplayTests
{
    [Fact]
    public void MissingCostDoesNotShowProfit()
    {
        var cell = SummaryMoneyDisplay.Margin(0m, 200m);
        Assert.Equal("—", cell.Text);
        Assert.False(cell.Negative);
    }

    [Fact]
    public void ZeroAndBalancedAmountsShowZeroNotMissing()
    {
        foreach (var cost in new[] { 0m, 100m })
        {
            var cell = SummaryMoneyDisplay.Margin(cost, cost);
            Assert.Equal(GmcSmeta.Core.Money.Format(0m), cell.Text);
            Assert.False(cell.Negative);
        }
    }

    [Fact]
    public void CostOnlyAndLossAreNegative()
    {
        Assert.Equal(GmcSmeta.Core.Money.Format(-100m), SummaryMoneyDisplay.Margin(100m, 0m).Text);
        Assert.True(SummaryMoneyDisplay.Margin(100m, 0m).Negative);
        Assert.True(SummaryMoneyDisplay.Margin(100m, 99.99m).Negative);
    }

    [Fact]
    public void LargeAmountsRetainAllDigitsAndKopecks()
    {
        var cell = SummaryMoneyDisplay.Margin(10_000_000m, 29_144_180.98m);
        Assert.Equal(GmcSmeta.Core.Money.Format(19_144_180.98m), cell.Text);
        Assert.False(cell.Negative);
    }
}
