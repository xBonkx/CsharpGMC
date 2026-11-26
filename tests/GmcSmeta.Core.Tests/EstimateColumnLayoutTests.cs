using GmcSmeta.Core.Domain;
using static GmcSmeta.Core.Domain.EstimateColumnLayout;

namespace GmcSmeta.Core.Tests;

public class EstimateColumnLayoutTests
{
    private static Column[] Standard() =>
    [
        new("num", 64, 48, Role.Fixed),
        new("name", 460, 190, Role.Name),
        new("unit", 64, 48, Role.Fixed),
        new("qty", 80, 60, Role.Fixed),
        new("price", 118, 88, Role.Fixed),
        new("sum", 130, 97, Role.Fixed),
        new("note", 170, 36, Role.Note)
    ];

    [Fact]
    public void WideViewport_GivesSpareToNoteWithoutChangingPreferences()
    {
        var cols = Standard();
        var fitted = Fit(cols, 1400);
        Assert.Equal(1400, fitted.Values.Sum(), 6);
        Assert.Equal(460, fitted["name"]);
        Assert.True(fitted["note"] > 170);
        Assert.Equal(170, cols[^1].Preferred);
    }

    [Fact]
    public void NarrowViewport_ReducesNoteBeforeNameAndKeepsMoneyWidth()
    {
        var fitted = Fit(Standard(), 950);
        Assert.Equal(950, fitted.Values.Sum(), 6);
        Assert.Equal(36, fitted["note"]);
        Assert.True(fitted["name"] < 460);
        Assert.Equal(118, fitted["price"]);
        Assert.Equal(130, fitted["sum"]);
    }

    [Fact]
    public void VeryNarrowViewport_PreservesReadableMinimaAndAllowsScrolling()
    {
        var cols = Standard();
        var fitted = Fit(cols, 300);
        Assert.True(fitted.Values.Sum() > 300);
        foreach (var c in cols) Assert.Equal(c.Minimum, fitted[c.Key]);
    }

    [Fact]
    public void HiddenNote_UsesNameAndDoesNotIntroduceMissingColumn()
    {
        var cols = Standard().Where(c => c.Kind != Role.Note).ToArray();
        var fitted = Fit(cols, 1200);
        Assert.False(fitted.ContainsKey("note"));
        Assert.Equal(1200, fitted.Values.Sum(), 6);
        Assert.True(fitted["name"] > 460);
    }

    [Fact]
    public void RepeatedFits_DoNotReplaceManualPreferences()
    {
        var cols = Standard();
        cols[1] = cols[1] with { Preferred = 600 };
        var compact = Fit(cols, 820);
        var wide = Fit(cols, 1600);
        Assert.True(compact["name"] < 600);
        Assert.Equal(600, wide["name"]);
        Assert.Equal(600, cols[1].Preferred);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(1200)]
    [InlineData(2400)]
    public void ManualLayout_DoesNotRedistributeWidths(double viewport)
    {
        var cols = Standard();
        cols[1] = cols[1] with { Preferred = 600 };
        var fitted = Fit(cols, viewport, manual: true);
        foreach (var c in cols) Assert.Equal(c.Preferred, fitted[c.Key]);
        Assert.Equal(600, fitted["name"]);
        Assert.Equal(118, fitted["price"]);
    }

    [Fact]
    public void ManualLayout_RespectsMinimumAndResetReturnsToAutoFit()
    {
        var cols = Standard();
        cols[1] = cols[1] with { Preferred = 20 };
        Assert.Equal(190, Fit(cols, 500, manual: true)["name"]);
        Assert.Equal(1400, Fit(Standard(), 1400).Values.Sum(), 6);
    }

    [Fact]
    public void EmptyAndInvalidViewport()
    {
        Assert.Empty(Fit([], 800));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fit(Standard(), double.NaN));
    }
}
