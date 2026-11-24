using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ActQuantityInputTests
{
    [Theory]
    [InlineData("1,5", "1.5")]
    [InlineData("2.25", "2.25")]
    [InlineData("", "0")]
    [InlineData("0", "0")]
    public void Quantity_ParsesUsingCommaOrDot(string raw, string expected)
    {
        var parsed = ActQuantityInput.Parse(raw, new Dictionary<long, int>());
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            parsed.Quantity);
        Assert.Equal("", parsed.Formula);
    }

    [Fact]
    public void Formula_ReferencesVisibleKAndStableIdBeforeNumberParser()
    {
        var parsed = ActQuantityInput.Parse("=К2*1,5",
            new Dictionary<long, int> { [42] = 2 });
        Assert.Equal(0m, parsed.Quantity);
        Assert.Equal("@42*1,5", parsed.Formula);
        var alreadyStable = ActQuantityInput.Parse("@42*2", new Dictionary<long, int>());
        Assert.Equal("@42*2", alreadyStable.Formula);
    }

    [Theory]
    [InlineData("К99*2")]
    [InlineData("=2*3")]
    [InlineData("-1")]
    [InlineData("абв")]
    public void InvalidQuantity_DoesNotReachSqlServer(string text)
    {
        Assert.ThrowsAny<Exception>(() => ActQuantityInput.Parse(text,
            new Dictionary<long, int>()));
    }
}
