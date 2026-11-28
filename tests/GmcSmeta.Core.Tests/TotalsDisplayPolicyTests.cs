using GmcSmeta.Core.Domain;
using GmcSmeta.Core;

namespace GmcSmeta.Core.Tests;

public sealed class TotalsDisplayPolicyTests
{
    [Fact]
    public void NullZeroAndNonApplicableAreDistinct()
    {
        Assert.Equal("", TotalsDisplayPolicy.MoneyText(null));
        Assert.Equal("", TotalsDisplayPolicy.MoneyText(0m));
        Assert.Equal("—", TotalsDisplayPolicy.SellText(null));
        Assert.Equal("", TotalsDisplayPolicy.SellText(0m));
        Assert.Equal(Money.Format(123456789.01m), TotalsDisplayPolicy.MoneyText(123456789.01m));
    }

    [Fact]
    public void MarginMatchesPythonEmptyZeroAndCostOnlyRules()
    {
        Assert.Equal(("", "", false), TotalsDisplayPolicy.Margin(null, null));
        Assert.Equal(("", "", false), TotalsDisplayPolicy.Margin(0m, 0m));
        Assert.Equal(("—", "", false), TotalsDisplayPolicy.Margin(null, 100m));
        Assert.Equal(("—", "", false), TotalsDisplayPolicy.Margin(0m, 100m));
        Assert.Equal(("−" + Money.Format(100m), "", true), TotalsDisplayPolicy.Margin(100m, null));
        Assert.Equal((Money.Format(0m), "0,0%", false), TotalsDisplayPolicy.Margin(100m, 100m));
        Assert.Equal((Money.Format(-100m), "-100,0%", true), TotalsDisplayPolicy.Margin(100m, 0m));
        Assert.Equal((Money.Format(50m), "50,0%", false), TotalsDisplayPolicy.Margin(100m, 150m));
    }
}
