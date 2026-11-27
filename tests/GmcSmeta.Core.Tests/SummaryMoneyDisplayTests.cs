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

    [Theory]
    [InlineData(null, false, false, false, false)]
    [InlineData(null, false, false, true, false)]
    [InlineData(0, false, true, false, false)]
    [InlineData(0, false, true, true, false)]
    [InlineData(0, false, false, true, false)]
    [InlineData(12, false, true, true, false)]
    [InlineData(12, false, false, false, true)]
    [InlineData(12, true, false, true, false)]
    [InlineData(12, true, false, false, true)]
    [InlineData(-12, false, false, true, false)]
    public void UnitCellMatchesFormerTextRawAndMarkerRules(int? number,
        bool differs, bool isPrice, bool markMissing, bool forceMissing)
    {
        decimal? value = number;
        var oldText = differs || value is null ? "" :
            value.Value != 0m || !isPrice ? GmcSmeta.Core.Money.Format(value.Value) : "";
        var oldRaw = differs || value is null ? "" :
            value.Value != 0m || !isPrice ? GmcSmeta.Core.NumberText.QtyText(value.Value) : "";
        var oldMarked = forceMissing || (markMissing && oldText.Length == 0);
        var result = SummaryMoneyDisplay.Unit(value, differs, isPrice, markMissing, forceMissing);
        Assert.Equal(oldMarked ? "—" : oldText, result.Text);
        Assert.Equal(oldRaw, result.Raw);
        Assert.Equal(oldMarked, result.Marked);
    }

}
