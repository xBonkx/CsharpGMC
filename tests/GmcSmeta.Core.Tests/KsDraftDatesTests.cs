using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsDraftDatesTests
{
    [Theory]
    [InlineData(" 29.02.2024 ")]
    [InlineData("29/02/2024")]
    [InlineData("29-02-2024")]
    [InlineData("29,02,2024")]
    [InlineData("29 02 2024")]
    public void ExistingDateSeparatorsRemainAccepted(string text) =>
        Assert.Equal(new DateTime(2024,2,29),KsDraftDates.ParseRuDate(text));

    [Theory]
    [InlineData("29.02.2025")]
    [InlineData("31.04.2026")]
    [InlineData("01.01.26")]
    [InlineData("bad")]
    public void InvalidDatesRemainRejected(string text) =>
        Assert.Throws<FormatException>(()=>KsDraftDates.ParseRuDate(text));

    [Fact]
    public void ValidPeriodIsUnchanged()
    {
        var result=KsDraftDates.Normalize("30.11.2026","01.11.2026","29.11.2026");
        Assert.Equal((new DateTime(2026,11,30),new DateTime(2026,11,1),new DateTime(2026,11,29),""),result);
    }

    [Fact]
    public void EndDateClampedToActDate()
    {
        var result=KsDraftDates.Normalize("30.11.2026","01.11.2026","01.12.2026");
        Assert.Equal(result.Date,result.To);
        Assert.Contains("Конец периода",result.Message);
    }

    [Fact]
    public void StartClampedAfterEndAndLastWarningWins()
    {
        var result=KsDraftDates.Normalize("30.11.2026","02.12.2026","01.12.2026");
        Assert.Equal(result.Date,result.To);
        Assert.Equal(result.To,result.From);
        Assert.Contains("Начало периода",result.Message);
    }
}
