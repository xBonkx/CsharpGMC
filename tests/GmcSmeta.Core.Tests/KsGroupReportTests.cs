using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsGroupReportTests
{
    private static SqlServerActs.ActInfo Act(long id, string number, DateTime when,
        decimal gross, decimal net, decimal vat, long contractId) =>
        new(id,number,when,when.AddDays(-7),when,true,5,gross,
            contractId,"ДС",net,vat,VatRate:22m);

    private static SqlServerKsContractScope.Scope Example()
    {
        var acts = new[]
        {
            Act(1,"1.1",new DateTime(2025,12,25),122,100,22,10),
            Act(2,"2.1",new DateTime(2026,1,20),244,200,44,10),
            Act(3,"2.2",new DateTime(2026,1,22),366,300,66,11),
            Act(4,"3",new DateTime(2026,2,12),122,100,22,10),
        };
        return new SqlServerKsContractScope.Scope(10,"05-10/ЗБ2",
            Array.Empty<SqlServerKsContractScope.Document>(),acts);
    }

    [Fact]
    public void GroupIncludesKs2FromMultipleDocumentsWithoutDoubleCounting()
    {
        var g = KsGroupReport.Calculate(Example(),"2");
        Assert.Equal(2,g.Acts.Count);
        Assert.Equal(new long[]{10,11},g.Acts.Select(a=>a.ContractId));
        Assert.Equal(500m,g.PeriodNet);
        Assert.Equal(610m,g.PeriodGross);
        Assert.Equal(110m,g.PeriodVat);
        Assert.Equal(600m,g.SinceStartNet);
        Assert.Equal(500m,g.YearNet);
        Assert.Equal(new DateTime(2026,1,22),g.Date);
    }

    [Fact]
    public void DocumentExportKeepsOnlyLocalCurrentActsButLegalAgreementHistory()
    {
        var scope = Example();
        var group = KsGroupReport.Calculate(scope, "2", selectedDocumentOnly: true);
        Assert.Single(group.Acts);
        Assert.Equal(2L, group.Acts[0].Id);
        Assert.Equal(200m, group.PeriodNet);
        Assert.Equal(244m, group.PeriodGross);
        Assert.Equal(44m, group.PeriodVat);
        Assert.Equal(300m, group.SinceStartNet);
        Assert.Equal(200m, group.YearNet);
        Assert.Single(group.Previous);
        var later = KsGroupReport.Calculate(scope, "3", selectedDocumentOnly: true);
        Assert.Equal(700m, later.SinceStartNet); // prior legal group includes the annex
        Assert.Equal(600m, later.YearNet);
        Assert.Throws<InvalidOperationException>(() =>
            KsGroupReport.Calculate(scope with { SelectedDocumentId = 11 }, "3", selectedDocumentOnly: true));
        Assert.Equal(610m, KsGroupReport.Calculate(scope, "2").PeriodGross);
    }

    [Fact]
    public void PreviousGroupsAreNotIncludedInPeriod()
    {
        var g = KsGroupReport.Calculate(Example(),"3");
        Assert.Equal(100m,g.PeriodNet);
        Assert.Equal(100m + 500m + 100m,g.SinceStartNet);
        Assert.Equal(600m,g.YearNet);
        Assert.Equal(2,g.Previous.Count);
        Assert.Equal(122m,g.Previous[0].Gross);
    }

    [Fact]
    public void MissingGroupFailsAndDoesNotEmitEmptyKs3()
    {
        Assert.Throws<InvalidOperationException>(() =>
            KsGroupReport.Calculate(Example(),"999"));
    }

    [Fact]
    public void FirstGroupHasNoPreviousItems()
    {
        var g = KsGroupReport.Calculate(Example(),"1");
        Assert.Empty(g.Previous);
        Assert.Equal(100m,g.SinceStartNet);
    }

    [Fact]
    public void SingleActDoesNotIncludeSiblingOrOtherDocumentInCumulativeAmounts()
    {
        var scope = Example();
        var report = KsGroupReport.CalculateSingle(scope, 2);
        Assert.Equal("2.1", report.Key);
        Assert.Equal(200m, report.PeriodNet);
        Assert.Equal(200m, report.YearNet);
        Assert.Equal(300m, report.SinceStartNet);
        Assert.Single(report.Acts);
        Assert.Single(report.Previous);
        Assert.Throws<InvalidOperationException>(() => KsGroupReport.CalculateSingle(scope, 3));
    }

    [Fact]
    public void SingleActWithOpeningCountsOnlySelectedCurrentAct()
    {
        var scope = Example() with { Opening = new Ks3OpeningBalance(new DateTime(2025, 12, 31), 1000m) };
        var report = KsGroupReport.CalculateSingle(scope, 2);
        Assert.True(report.GrossCumulative);
        Assert.Equal(1244m, report.SinceStartNet);
        Assert.Equal(244m, report.YearNet);
        Assert.Equal(200m, report.PeriodNet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedSingleBatchMatchesIndependentLegacyCalculationWithMixedVat(bool opening)
    {
        var scope = Example() with
        {
            Acts = Enumerable.Range(1, 500).Select(i =>
                Act(i, $"{i / 4}.{i % 4}", new DateTime(2025, 11, 1).AddDays(i / 2),
                    i * 0.13m, i * 0.1m, i * 0.03m, i % 3 == 0 ? 11 : 10)
                    with { VatRate = i % 5 == 0 ? 0m : i % 2 == 0 ? 20m : 22m })
                .Reverse().ToArray(),
            Opening = opening ? new Ks3OpeningBalance(new DateTime(2025, 12, 31), 1234.56m) : null
        };
        var local = scope.Acts.Where(a => a.ContractId == scope.SelectedDocumentId)
            .OrderBy(a => a.Date).ThenBy(a => a.Id).ToArray();
        var groups = KsWorkspaceProjection.Build(scope.Acts);
        var prepared = new KsGroupReport.Prepared(scope);
        foreach (var act in local)
        {
            var expected = SqlServerActs.CalculateKs3ReportFromActs(local, act.Id);
            var groupIndex = groups.ToList().FindIndex(g => g.Key == KsWorkspaceProjection.GroupKey(act));
            var cumulative = scope.Opening?.Cumulative(act.Date,
                groups.Take(groupIndex).SelectMany(g => g.Acts).Select(a => (a.Date, a.Total)),
                new[] { (act.Date, act.Total) });
            var actual = prepared.Single(act.Id);
            Assert.Equal(expected.Period, actual.PeriodNet);
            Assert.Equal(expected.Vat, actual.PeriodVat);
            Assert.Equal(expected.Total, actual.PeriodGross);
            Assert.Equal(cumulative?.YearGross ?? expected.Year, actual.YearNet);
            Assert.Equal(cumulative?.SinceStartGross ?? expected.Start, actual.SinceStartNet);
            Assert.Equal(cumulative is not null, actual.GrossCumulative);
            Assert.Equal(expected.Previous.Select(a => a.Net), actual.Previous.Select(a => a.Net));
            Assert.Equal(act.Id, Assert.Single(actual.Acts).Id);
        }
        Assert.Throws<InvalidOperationException>(() => prepared.Single(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedGroupsPreservePreviousLegalHistoryAndLocalMembership(bool localOnly)
    {
        var scope = Example() with { Acts = Example().Acts.Reverse().ToArray(),
            Opening = new Ks3OpeningBalance(new DateTime(2025, 12, 31), 1000m) };
        var prepared = new KsGroupReport.Prepared(scope);
        var later = prepared.Group("3", localOnly);
        var current = prepared.Group("2", localOnly);
        Assert.Equal(100m, later.PeriodNet);
        Assert.Equal(1732m, later.SinceStartNet);
        Assert.Equal(732m, later.YearNet);
        Assert.Equal(2, later.Previous.Count);
        Assert.Equal(610m, later.Previous[1].Gross);
        Assert.Equal(localOnly ? 244m : 610m, current.PeriodGross);
        Assert.Equal(localOnly ? 1244m : 1610m, current.SinceStartNet);
        Assert.Equal(localOnly ? new long[] { 2 } : new long[] { 2, 3 },
            current.Acts.Select(a => a.Id));
        Assert.Same(later.Previous[0], current.Previous[0]);
        Assert.Throws<InvalidOperationException>(() => prepared.Group("999"));
    }

}
