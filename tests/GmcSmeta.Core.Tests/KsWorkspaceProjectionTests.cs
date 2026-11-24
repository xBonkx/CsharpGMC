using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsWorkspaceProjectionTests
{
    private static readonly IReadOnlyList<SqlServerActs.ActInfo> Sample = new[]
    {
        new SqlServerActs.ActInfo(101, "8.1", new DateTime(2026,11,1), null, null, true, 8, 120m),
        new SqlServerActs.ActInfo(102, "8.2", new DateTime(2026,11,2), null, null, false, 3, 80m),
        new SqlServerActs.ActInfo(103, "9", new DateTime(2026,11,3), null, null, false, 0, 0m),
    };

    [Theory]
    [InlineData("8.1", "8")]
    [InlineData("8.2", "8")]
    [InlineData("9", "9")]
    [InlineData(" № 10.2 ", "10")]
    [InlineData("", "Без номера")]
    public void GroupKey_FollowsPythonKs3Grouping(string number, string expected) =>
        Assert.Equal(expected, KsWorkspaceProjection.GroupKey(number));

    [Fact]
    public void ImportedGrossCorrectionGroupsDashSuffixOnlyWhenAdjusted()
    {
        var special = new SqlServerActs.ActInfo(220, "4-2",
            new DateTime(2026, 11, 21), null, null, false, 0, 1m,
            GrossAdjustmentCents: 1);
        var ordinary = special with { GrossAdjustmentCents = 0 };
        Assert.Equal("4", KsWorkspaceProjection.GroupKey(special));
        Assert.Equal("4-2", KsWorkspaceProjection.GroupKey(ordinary));
    }

    [Fact]
    public void RequestedActIdWinsOverPreviouslyDisplayedAct()
    {
        Assert.Equal(103L, KsWorkspaceProjection.ResolveActId(Sample, 103L));
        Assert.Equal(101L, KsWorkspaceProjection.ResolveActId(Sample, 101L));
        Assert.Equal(102L, KsWorkspaceProjection.ResolveActId(Sample, 102L));
        Assert.Equal(103L, KsWorkspaceProjection.ResolveActId(Sample, 999L));
        Assert.Equal(0L, KsWorkspaceProjection.ResolveActId(Array.Empty<SqlServerActs.ActInfo>(), 101L));
    }

    [Fact]
    public void OverviewFilterClearsHiddenSelectionAndRetainsVisibleAct()
    {
        var groups = KsWorkspaceProjection.Build(Sample);
        var visible = KsWorkspaceProjection.Filter(groups, "", true).SelectMany(g => g.Acts);
        Assert.Null(KsWorkspaceProjection.VisibleAct(visible, 102L));
        Assert.Equal(101L, KsWorkspaceProjection.VisibleAct(visible, 101L)!.Id);
        Assert.Null(KsWorkspaceProjection.VisibleAct(Array.Empty<SqlServerActs.ActInfo>(), 101L));
    }

    [Fact]
    public void OverviewSearchIncludesAnnexName()
    {
        var groups = KsWorkspaceProjection.Build(new[] { Sample[0] with { SourceName = "ДС № 2" }, Sample[1] });
        var result = KsWorkspaceProjection.Filter(groups, "ДС", null);
        Assert.Equal(101L, Assert.Single(Assert.Single(result).Acts).Id);
    }

    [Fact]
    public void Build_GroupsAllActsAndNeverCountsGroupRowsAsExtraMoney()
    {
        var result = KsWorkspaceProjection.Build(Sample);
        Assert.Equal(2, result.Count);
        var group = Assert.Single(result.Where(g => g.Key == "8"));
        Assert.Equal(2, group.Count);
        Assert.Equal(200m, group.Total);
        Assert.False(group.Signed);
        Assert.Equal(Sample.Sum(a => a.Total), result.Sum(g => g.Total));
        Assert.True(KsWorkspaceProjection.Build(new[] { Sample[0] })[0].Signed);
    }

    [Fact]
    public void Filter_HidesUnmatchedActsButLeavesOriginalDataUnchanged()
    {
        var original = KsWorkspaceProjection.Build(Sample);
        var signed = KsWorkspaceProjection.Filter(original, "", true);
        var only = Assert.Single(signed);
        Assert.Equal(101, Assert.Single(only.Acts).Id);
        Assert.Equal(120m, only.Total);
        var byNumber = KsWorkspaceProjection.Filter(original, "8.2", null);
        Assert.Equal(102, Assert.Single(Assert.Single(byNumber).Acts).Id);
        Assert.Equal(200m, original.Single(x=>x.Key=="8").Total);
        Assert.Empty(KsWorkspaceProjection.Filter(original, "does-not-exist", false));
    }
}
