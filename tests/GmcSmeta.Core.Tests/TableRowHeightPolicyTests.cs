using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class TableRowHeightPolicyTests
{
    [Theory]
    [InlineData(0.8, 24)]
    [InlineData(1.0, 27)]
    [InlineData(1.2, 31)]
    [InlineData(1.5, 36)]
    [InlineData(1.8, 42)]
    public void CompactHeightTracksFontAndWrapUsesAutomaticHeight(double zoom, int height)
    {
        var font = 40d / 3d * zoom;
        Assert.Equal((double)height, TableRowHeightPolicy.Minimum(font));
        Assert.Equal((double)height, TableRowHeightPolicy.RowHeight(font, false));
        Assert.True(double.IsNaN(TableRowHeightPolicy.RowHeight(font, true)));
    }

    [Fact]
    public void InvalidFontFallsBackToNormalHeight()
    {
        Assert.Equal(27d, TableRowHeightPolicy.Minimum(double.NaN));
        Assert.Equal(27d, TableRowHeightPolicy.Minimum(0));
    }
}
