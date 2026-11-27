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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisibleSummaryGroups_MatchOriginalScanOnLargeSnapshot(bool onlyRest)
    {
        var systems = new List<(SqlServerActs.SummarySystem System, bool SectionHeader)>();
        for (var i = 0; i < 250; i++)
        {
            long parent = 1000 + i;
            systems.Add((new(parent, $"Группа {i}", false), true));
            systems.Add((new(i * 2 + 1, "П1", true, parent), false));
            systems.Add((new(i * 2 + 2, "П1", true, parent), false));
        }
        var perAct = new Dictionary<long, decimal> { [101] = 2m, [102] = 3m };
        var positions = Enumerable.Range(1, 25000).Select(i =>
        {
            long systemId = (i - 1) % 500 + 1;
            var left = systemId % 5 == 0 ? 0m :
                i % 4 == 0 ? -2m : i % 3 == 0 ? 0.000001m : 3m;
            return new SqlServerActs.SummaryRow(i, systemId, i.ToString(), "Позиция", "шт",
                5m + left, 10m, 5m, 50m, left, left * 10m, 1m, perAct);
        }).ToArray();
        // Empty section and empty child must not become visible.
        systems.Add((new(2000, "Пустая", false), true));
        systems.Add((new(999, "Пустая", true, 2000), false));
        var summary = new SqlServerActs.SummaryData(Sample, systems, positions);

        bool Visible(SqlServerActs.SummaryRow p) => !onlyRest || Math.Abs(p.Left) > 0.000001m;
        var expected = new List<(long Id, bool Header, long Position)>();
        foreach (var (system, header) in systems)
        {
            if (header)
            {
                if (systems.Any(s => !s.SectionHeader && s.System.ParentId == system.SystemId &&
                    positions.Any(p => p.SystemId == s.System.SystemId && Visible(p))))
                    expected.Add((system.SystemId, true, 0));
                continue;
            }
            foreach (var p in positions.Where(p => p.SystemId == system.SystemId && Visible(p)))
                expected.Add((system.SystemId, false, p.PositionId));
        }
        var groups = KsWorkspaceProjection.VisibleSummaryGroups(summary, onlyRest).ToArray();
        var actual = groups.SelectMany(g => g.SectionHeader
            ? new[] { (g.System.SystemId, true, 0L) }
            : g.Positions.Select(p => (g.System.SystemId, false, p.PositionId))).ToArray();
        Assert.Equal(expected.ToArray(), actual);
        Assert.All(groups.SelectMany(g => g.Positions), p =>
            Assert.Same(positions[checked((int)p.PositionId - 1)], p));
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

    [Fact]
    public void ExportSummaryGroupsRetainEmptyHeadersAndEveryPositionInSourceOrder()
    {
        var systems = new List<(SqlServerActs.SummarySystem System, bool SectionHeader)>();
        for (var i = 1; i <= 500; i++)
            systems.Add((new(i, "Система " + i, false), false));
        systems.Insert(0, (new(1000, "Пустая группа", false), true));
        systems.Add((new(1001, "Пустая система", false), false));
        var perAct = new Dictionary<long, decimal> { [101] = 2m, [102] = 3m };
        var rows = Enumerable.Range(1, 25000).Select(i =>
            new SqlServerActs.SummaryRow(i, i % 500 + 1, i.ToString(), "Позиция", "шт",
                5m, 10m, 5m, 50m, 0m, 0m, 1m, perAct)).Reverse().ToArray();
        var summary = new SqlServerActs.SummaryData(Sample, systems, rows);
        var groups = KsWorkspaceProjection.ExportSummaryGroups(summary).ToArray();
        Assert.Equal(systems.Count, groups.Length);
        Assert.Equal(systems.Select(s => (s.System, s.SectionHeader)),
            groups.Select(g => (g.System, g.SectionHeader)));
        Assert.Empty(groups[0].Positions);
        Assert.Empty(groups[^1].Positions);
        Assert.Equal(rows.Length, groups.Sum(g => g.Positions.Count));
        foreach (var group in groups)
            Assert.Equal(rows.Where(r => r.SystemId == group.System.SystemId), group.Positions);
        Assert.Equal(rows.Sum(r => r.QtyByKp * r.Price),
            groups.SelectMany(g => g.Positions).Sum(r => r.QtyByKp * r.Price));
        Assert.Empty(KsWorkspaceProjection.VisibleSummaryGroups(summary, true));
    }


    [Theory]
    [InlineData("", null)]
    [InlineData("", true)]
    [InlineData("КС-3 ДС", false)]
    [InlineData("дс 2026", null)]
    [InlineData("не найдено", null)]
    public void PreparedOverviewSearchMatchesLegacyFilterOnLargeList(string search, bool? signed)
    {
        var acts = Enumerable.Range(1, 5000).Select(i => Sample[i % 3] with
        {
            Id = i, Number = $"{i / 10}.{i % 10}",
            SourceName = i % 2 == 0 ? "ДС № 2" : "Приложение 1",
            Total = i * 0.13m,
            Signed = i % 3 == 0
        }).ToArray();
        var groups = KsWorkspaceProjection.Build(acts);
        var index = new KsOverviewSearchIndex(groups);
        var words = search.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expected = groups.Select(g => (g.Key, Acts: g.Acts.Where(a =>
            (signed is null || a.Signed == signed) &&
            words.All(w => ($"{g.Title} {a.Number} {a.Date:dd.MM.yyyy} {a.SourceName}")
                .Contains(w, StringComparison.OrdinalIgnoreCase))).ToArray()))
            .Where(g => g.Acts.Length != 0).ToArray();
        var actual = index.Filter(search, signed);
        Assert.Equal(expected.Select(g => g.Key), actual.Select(g => g.Key));
        for (var i = 0; i < actual.Count; i++)
        {
            Assert.Equal(expected[i].Acts.Select(a => a.Id), actual[i].Acts.Select(a => a.Id));
            Assert.Equal(expected[i].Acts.Sum(a => a.Total), actual[i].Total);
            Assert.Equal(expected[i].Acts.All(a => a.Signed), actual[i].Signed);
        }
        Assert.Same(groups, index.Filter("", null));
        Assert.Equal(acts.Sum(a => a.Total), groups.Sum(g => g.Total));
    }

}
