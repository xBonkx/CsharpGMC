using GmcSmeta.Core;

namespace GmcSmeta.Core.Tests;

public sealed class NumberTextTests
{
    [Theory]
    [InlineData("1234.5", 1234.5)]
    [InlineData("1 234,5", 1234.5)]
    [InlineData("12*1,15", 13.8)]
    [InlineData("=4+5", 9)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("10/4", 2.5)]
    [InlineData("1.", 1)]
    [InlineData(".5", 0.5)]
    [InlineData("1,234", 1.234)]
    [InlineData("1 000", 1000)]
    public void Parse_ValidNumbers(string text, decimal expected) =>
        Assert.Equal(expected, NumberText.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    [InlineData("1**2")]
    [InlineData("1e3")]
    [InlineData("Infinity")]
    [InlineData("1000000000001")]
    [InlineData("(1+2")]
    [InlineData("1/0")]
    [InlineData("2^3")]
    public void Parse_InvalidOrNull(string? text) =>
        Assert.Null(NumberText.Parse(text));

    [Theory]
    [InlineData("4+5", "=4+5")]
    [InlineData("=4+5", "=4+5")]
    [InlineData("12*1,15", "=12*1,15")]
    [InlineData("100", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void FormulaText_Works(string text, string? expected) =>
        Assert.Equal(expected, NumberText.FormulaText(text));

    [Theory]
    [InlineData(0d, "0")]
    [InlineData(5d, "5")]
    [InlineData(1.5d, "1,5")]
    [InlineData(1.2345d, "1,235")]
    [InlineData(1234.56789d, "1234,568")]
    public void QtyText_Works(double value, string expected) =>
        Assert.Equal(expected, NumberText.QtyText((decimal)value));

    [Fact]
    public void Parse_ZeroReturnsZeroNotNegative() =>
        Assert.Equal(0m, NumberText.Parse("-0"));

    [Fact]
    public void MaxNumber_IsOneTrillion() =>
        Assert.Equal(1_000_000_000_000m, NumberText.MaxNumber);
}