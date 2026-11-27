using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class NameDisplayTextTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Клапан 100", "Клапан 100")]
    [InlineData("Воздуховод\r\nСостав работ:\n1. Монтаж\r2. Крепление", "Воздуховод Состав работ: 1. Монтаж 2. Крепление")]
    [InlineData("А\u0085Б\u2028В\u2029Г\vД\fЕ", "А Б В Г Д Е")]
    public void SingleLineRemovesExplicitBreaksForDisplayOnly(string? source, string expected)
    {
        Assert.Equal(expected, NameDisplayText.SingleLine(source));
    }
}
