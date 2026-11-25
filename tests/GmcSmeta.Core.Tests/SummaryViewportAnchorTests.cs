using GmcSmeta.Core.Domain;
using static GmcSmeta.Core.Domain.SummaryViewportAnchor;

namespace GmcSmeta.Core.Tests;

public sealed class SummaryViewportAnchorTests
{
    [Fact]
    public void CurrentVisibleRowWinsThenTopThenOffsetFallback()
    {
        var current = new Row("pos:2", 80);
        var top = new Row("pos:1", -8);
        Assert.Equal(current, Choose(current, top, new HashSet<string> { "pos:1", "pos:2" }));
        Assert.Equal(top, Choose(current, top, new HashSet<string> { "pos:1" }));
        Assert.Null(Choose(current, top, new HashSet<string> { "pos:3" }));
    }

    [Theory]
    [InlineData(500, 106, 80, 1000, 526)]
    [InlineData(500, -2, -8, 1000, 506)]
    [InlineData(10, 0, 80, 1000, 0)]
    [InlineData(990, 100, 0, 1000, 1000)]
    public void CorrectsPixelPositionWithinScrollBounds(double offset, double actual,
        double desired, double max, double expected) =>
        Assert.Equal(expected, Offset(offset, actual, desired, max));
}
