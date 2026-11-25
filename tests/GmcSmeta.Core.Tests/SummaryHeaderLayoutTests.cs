using static GmcSmeta.Core.Domain.SummaryHeaderLayout;

namespace GmcSmeta.Core.Tests;

public class SummaryHeaderLayoutTests
{
    [Fact]
    public void BandsFollowActualColumnWidthsAndScrollOrigin()
    {
        var runs = Build([
            new("name", 250), new("unit", 44), new("qty", 60),
            new("m_ucost", 80), new("m_msell", 110),
            new("w_ucost", 90), new("markup", 72),
            new("t_cost", 100), new("mat_margin", 125)
        ], -50);
        Assert.Equal(new Run[] {
            new(Band.Position, -50, 354), new(Band.Materials, 304, 190),
            new(Band.Work, 494, 162), new(Band.Total, 656, 225)
        }, runs.ToArray());
    }

    [Fact]
    public void HiddenColumnsAndSeparatedGroupsDoNotOverlap()
    {
        var runs = Build([new("name", 200), new("m_ucost", 0),
            new("markup", 72), new("t_sell", 100), new("w_wsell", 80)], 1);
        Assert.Equal(new Run[] {
            new(Band.Position, 1, 200), new(Band.Work, 201, 72),
            new(Band.Total, 273, 100), new(Band.Work, 373, 80)
        }, runs.ToArray());
    }

    [Fact]
    public void ReorderingAcrossBandsKeepsHeaderRunsAlignedAndNameFirst()
    {
        string[] defaults = ["name", "m_ucost", "w_ucost", "m_msell", "t_cost"];
        var order = GmcSmeta.Core.Domain.ColumnOrderPolicy.Normalize(defaults,
            ["w_ucost", "t_cost", "m_msell", "name", "m_ucost"], new HashSet<string> { "name" });
        Assert.Equal(new[] { "name", "w_ucost", "t_cost", "m_msell", "m_ucost" }, order);
        var runs = Build(order.Select(k => new Column(k, 100)), -30);
        Assert.Equal(new Run[] {
            new(Band.Position, -30, 100), new(Band.Work, 70, 100),
            new(Band.Total, 170, 100), new(Band.Materials, 270, 200)
        }, runs.ToArray());
    }

    [Theory]
    [InlineData("mat_margin", Band.Total)]
    [InlineData("work_margin", Band.Total)]
    [InlineData("markup", Band.Work)]
    public void FinancialGroups(string key, Band expected) => Assert.Equal(expected, GroupFor(key));

    [Fact]
    public void EmptyLayoutHasNoBands() => Assert.Empty(Build([], 0));
}
