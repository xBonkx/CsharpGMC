using GmcSmeta.Core;

namespace GmcSmeta.Core.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData("4882.895", "4882.90")]   // тот самый случай из xround: полкопейки вверх
    [InlineData("0.005", "0.01")]
    [InlineData("1693.822791987", "1693.82")]
    [InlineData("-4882.895", "-4882.90")]  // возвраты считаются так же
    [InlineData("100", "100.00")]
    public void Round2_HalfUp(string input, string expected)
    {
        Assert.Equal(decimal.Parse(expected), Money.Round2(decimal.Parse(input)));
    }

    [Fact]
    public void Round2_DecimalIsExactWhereFloatIsNot()
    {
        // Python round(2.675, 2) даёт 2.67 (двоичная дробь), Excel и бухгалтерия — 2.68.
        // Ради этого в Python-версии и появился костыль xround.
        Assert.Equal(2.68m, Money.Round2(2.675m));
    }

    [Fact]
    public void Amount_IsQtyTimesPrice_ToKopecks()
    {
        Assert.Equal(1693.82m, Money.Amount(3m, 564.607597329m));
        Assert.Equal(0m, Money.Amount(0m, 999.99m));
    }

    [Fact]
    public void Vat_IncludedInPrice()
    {
        Assert.Equal(220m, Money.VatIncluded(1220m, 22m));
        Assert.Equal(0m, Money.VatIncluded(1220m, 0m));
        Assert.Equal(47.62m, Money.Round2(Money.VatIncluded(1000m, 5m)));
        Assert.Equal(1000m, Money.NetFromGross(1220m, 22m));
    }

    [Fact]
    public void Vat_OnTop_ForDnsForm()
    {
        Assert.Equal(220m, Money.VatOnTop(1000m, 22m));
    }
}
