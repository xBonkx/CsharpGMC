using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SummaryToolbarLayoutTests
{
    [Theory]
    [InlineData(1000, 600, 376, true)]
    [InlineData(982, 600, 376, true)]
    [InlineData(981, 600, 376, false)]
    [InlineData(500, 480, 376, false)]
    public void GroupsFitOnlyWithGap(double available, double actions, double filters, bool expected) =>
        Assert.Equal(expected, SummaryToolbarLayout.Fits(available, actions, filters));
}
