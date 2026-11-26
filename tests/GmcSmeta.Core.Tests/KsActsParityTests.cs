using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsActsParityTests
{
    [Fact]
    public void CancelledProgressReadDoesNotUseClosedConnection()
    {
        using var conn = new Microsoft.Data.SqlClient.SqlConnection(
            "Server=invalid;Database=GmcSmetaPilot_Progress;Integrated Security=true");
        var token = new CancellationToken(canceled: true);
        Assert.Throws<OperationCanceledException>(() => SqlServerActs.SummaryWithExtras(conn, 1, token));
        Assert.Throws<OperationCanceledException>(() => SqlServerActs.ActsOf(conn, 1, token: token));
        Assert.Throws<OperationCanceledException>(() => SqlServerActs.ReadIssued(conn, 1, token: token));
        Assert.Throws<OperationCanceledException>(() => SqlServerActs.ActSheet(conn, 1, 1, token));
        Assert.Throws<OperationCanceledException>(() => SqlServerActs.ReadDetails(conn, 1, token));
        Assert.Throws<OperationCanceledException>(() => SqlServerActs.ClosedByActs(conn, 1, 1, token: token));
        Assert.Throws<OperationCanceledException>(() => TagRules.Load(conn, token));
        Assert.Throws<OperationCanceledException>(() => KsActSheetProjection.NumberRows(
            Array.Empty<(long, string, long)>(), Array.Empty<SqlServerActs.ActSheetRow>(), false, token));
        Assert.Throws<OperationCanceledException>(() => Totals.ReadOne(conn, 1, token));
        Assert.Equal(System.Data.ConnectionState.Closed, conn.State);
    }

    [Fact]
    public void SystemSmrSplitPreservesExactCentsAndLastSystemGetsRemainder()
    {
        var rows=new[]
        {
            new KsActAllocation.Line(1,1m,1m,0m),
            new KsActAllocation.Line(2,1m,1m,0m),
            new KsActAllocation.Line(3,1m,1m,0m)
        };
        var split=KsActAllocation.Split(rows,100.00m,false);
        Assert.Equal(33.33m,split[1]);
        Assert.Equal(33.33m,split[2]);
        Assert.Equal(33.34m,split[3]);
        Assert.Equal(100m,split.Values.Sum());
    }

    [Fact]
    public void PositionModeUsesOnlyWorkPricesAndIgnoresManualSmr()
    {
        var rows=new[]
        {
            new KsActAllocation.Line(1,2m,100m,10m),
            new KsActAllocation.Line(1,3m,200m,2m),
            new KsActAllocation.Line(2,1m,300m,7m)
        };
        var split=KsActAllocation.Split(rows,1000m,true);
        Assert.Equal(26m,split[1]);
        Assert.Equal(7m,split[2]);
        Assert.Equal(33m,split.Values.Sum());
    }

    [Fact]
    public void WorkInPositionModeRoundsEachLineBeforeGroupSum()
    {
        var lines=new[] { new KsActAllocation.Line(1,1m,100m,0.335m),
                          new KsActAllocation.Line(1,1m,100m,0.335m) };
        var split=KsActAllocation.Split(lines,0m,true);
        Assert.Equal(0.68m,split[1]);
    }

    [Fact]
    public void RoundRowsUsesHalfUpForMaterialsAndAssignsResidualToLastSystem()
    {
        var lines=new[] { new KsActAllocation.Line(1,1m,0.335m,0m),
                          new KsActAllocation.Line(2,1m,0.334m,0m) };
        var split=KsActAllocation.Split(lines,10m,false,true);
        Assert.Equal(10m,split.Values.Sum());
        Assert.Equal(5.07m,split[1]); // 0.34 / (0.34 + 0.33)
        Assert.Equal(4.93m,split[2]);
    }

    [Fact]
    public void NoLinesOrNoSmrProducesNoSyntheticSystemWork()
    {
        Assert.Empty(KsActAllocation.Split(Array.Empty<KsActAllocation.Line>(),100m,false));
        Assert.Empty(KsActAllocation.Split(new[] { new KsActAllocation.Line(2,1m,100m,2m) },0m,false));
    }

    [Fact]
    public void DraftDatesClampInPythonOrderAndMonetaryValuesAreNotChanged()
    {
        var v=new SqlServerActs.ActDraftEdit(" 2.1 ",
            new DateTime(2026,11,21),new DateTime(2026,12,1),
            new DateTime(2026,12,10),12.34m,23.45m,34.56m,true);
        var normalized=SqlServerActs.NormalizeDraft(v);
        Assert.Equal("2.1",normalized.Number);
        Assert.Equal(new DateTime(2026,11,21),normalized.PeriodFrom);
        Assert.Equal(new DateTime(2026,11,21),normalized.PeriodTo);
        Assert.Equal(12.34m,normalized.Smr);
        Assert.Equal(23.45m,normalized.Transport);
        Assert.Equal(34.56m,normalized.Pnr);
    }

    [Fact]
    public void CombinedBatchRejectsInvalidChargesBeforeOpeningSql()
    {
        var rows = new[] { (PositionId: 1L, Qty: 1m) };
        var result = SqlServerActs.SetQuantitiesAndChargesBatch("not a connection string",
            1, 1, rows, new SqlServerActs.ActBatchCharges(-1m, 0m, 0m), "rev");
        Assert.False(result.Saved);
        result = SqlServerActs.SetQuantitiesAndChargesBatch("not a connection string",
            1, 1, new[] { (PositionId: 1L, Qty: 1m), (PositionId: 1L, Qty: 2m) },
            new SqlServerActs.ActBatchCharges(0m, 0m, 0m), "rev");
        Assert.False(result.Saved);
    }

    [Fact]
    public void InvalidDraftCannotBeSilentlyResetToZero()
    {
        var v=new SqlServerActs.ActDraftEdit("1",DateTime.Today,DateTime.Today,DateTime.Today,
            -1m,0m,0m,false);
        Assert.Throws<ArgumentOutOfRangeException>(()=>SqlServerActs.NormalizeDraft(v));
        Assert.Throws<ArgumentException>(()=>
            SqlServerActs.NormalizeDraft(v with { Smr=1m,Number=" " }));
    }
}
