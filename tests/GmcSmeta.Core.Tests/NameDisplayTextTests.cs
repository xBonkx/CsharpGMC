using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class NameDisplayTextTests
{
    [Fact]
    public void AlreadySingleLineKeepsOriginalInstance()
    {
        var source = new string('А', 2000);
        Assert.Same(source, NameDisplayText.SingleLine(source));
    }

    [Fact]
    public void MixedBreaksMatchFormerReplacementChain()
    {
        var alphabet = new[] { 'А', ' ', '\t', '\r', '\n', '\u0085', '\u2028', '\u2029', '\v', '\f', '\uD83D', '\uDE00' };
        var random = new Random(4401);
        for (var trial = 0; trial < 5000; trial++)
        {
            var source = new string(Enumerable.Range(0, random.Next(0, 100))
                .Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var expected = source.Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ').Replace('\n', ' ')
                .Replace('\u0085', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ')
                .Replace('\v', ' ').Replace('\f', ' ');
            Assert.Equal(expected, NameDisplayText.SingleLine(source));
        }
    }

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
