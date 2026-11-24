using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class KsPreSignAuditTests
{
    private static SqlServerActs.ActSheetRow Row(long id,
        decimal kp = 10m, decimal previouslyClosed = 0m,
        decimal inAct = 2m, decimal price = 125m,
        decimal issued = 10m, bool outside = false, bool tracked = true,
        long? lineId = null) =>
        new(id, 1, id.ToString(), "Воздуховод " + id, "м", kp, price, 0m,
            outside, "", lineId ?? id, inAct, "", price, 0m,
            previouslyClosed, issued, tracked);

    [Fact]
    public void HealthyAct_HasNoWarnings()
    {
        var report = KsPreSignAudit.Check([
            Row(1, previouslyClosed: 3m, inAct: 2m, issued: 7m),
            Row(2, previouslyClosed: 0m, inAct: 10m, issued: 10m)
        ]);
        Assert.Equal(2, report.PositionsInAct);
        Assert.Empty(report.Issues);
        Assert.Contains("Замечаний", report.Format());
    }

    [Fact]
    public void DetectsOverrunMissingPriceOutsideAndIssuedMismatch()
    {
        var report = KsPreSignAudit.Check([
            Row(1, kp: 10m, previouslyClosed: 8m, inAct: 5m,
                price: 0m, issued: 3m),
            Row(2, outside: true, inAct: 1m, tracked: false),
            Row(3, kp: 10m, inAct: 3m, issued: 0m, tracked: false),
            Row(4, kp: 10m, inAct: 0m, price: 0m, issued: 0m)
        ]);
        Assert.Equal(3, report.PositionsInAct);
        Assert.Equal(1, report.Count(KsPreSignAudit.IssueType.OverKp));
        Assert.Equal(1, report.Count(KsPreSignAudit.IssueType.MissingPrice));
        Assert.Equal(1, report.Count(KsPreSignAudit.IssueType.OutsideEstimate));
        Assert.Equal(1, report.Count(KsPreSignAudit.IssueType.MoreThanIssued));
        Assert.DoesNotContain(report.Issues, x => x.PositionId == 4);
        Assert.DoesNotContain(report.Issues, x => x.PositionId == 3);
        var text = report.Format();
        Assert.Contains("Превышен объём", text);
        Assert.Contains("№1", text);
        Assert.Contains("Позиции вне КП", text);
    }

    [Fact]
    public void ToleranceAndUntrackedIssueRowsDoNotCauseFalseWarnings()
    {
        var report = KsPreSignAudit.Check([
            Row(1, kp: 10m, previouslyClosed: 6m, inAct: 4.0000005m,
                issued: 0m, tracked: false),
            Row(2, kp: 10m, inAct: 0m, issued: 0m, tracked: true)
        ]);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public void EmptyActIsFlagged_ButZeroQuantityRowsAreIgnored()
    {
        var report = KsPreSignAudit.Check([Row(1, inAct: 0m)]);
        Assert.Equal(0, report.PositionsInAct);
        Assert.Single(report.Issues);
        Assert.Equal(KsPreSignAudit.IssueType.EmptyAct, report.Issues[0].Kind);
    }

    [Fact]
    public void LargeReportIsBoundedAndCountsAllIssues()
    {
        var rows = Enumerable.Range(1, 2500).Select(i =>
            Row(i, kp: 1m, previouslyClosed: 1m, inAct: 2m, issued: 0m))
            .ToArray();
        var report = KsPreSignAudit.Check(rows);
        Assert.Equal(2500, report.Count(KsPreSignAudit.IssueType.OverKp));
        var output = report.Format(examplesPerType: 3);
        Assert.Contains("Превышен объём по КП: 2500", output);
        Assert.Contains("…и ещё 2497 строк", output);
        Assert.True(output.Length < 3000, "Sign confirmation must not render 2500 rows");
    }
}
