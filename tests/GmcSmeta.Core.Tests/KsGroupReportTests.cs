using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsGroupReportTests
{
    private static SqlServerActs.ActInfo Act(long id, string number, DateTime when,
        decimal gross, decimal net, decimal vat, long contractId) =>
        new(id,number,when,when.AddDays(-7),when,true,5,gross,
            contractId,"ДС",net,vat);

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
}
