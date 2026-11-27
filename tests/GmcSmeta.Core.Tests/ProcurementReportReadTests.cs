using System.Data;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementReportReadTests
{
    [Fact]
    public void QueryIncludesOnlyNeededReportResultSets()
    {
        var supplier = ProcurementRequestPlanner.ReportQuery(ProcurementRequestPlanner.ReportKind.Supplier);
        var shop = ProcurementRequestPlanner.ReportQuery(ProcurementRequestPlanner.ReportKind.Shop);
        var control = ProcurementRequestPlanner.ReportQuery(ProcurementRequestPlanner.ReportKind.Control);
        Assert.Contains("WHERE c.id=@id", shop);
        Assert.Contains(SqlServerProcurement.BuyRowsQuery(), shop);
        Assert.StartsWith(shop + ";\n", supplier);
        Assert.StartsWith(shop + ";\n", control);
        Assert.Contains("SELECT order_ref FROM dbo.procurement", supplier);
        Assert.DoesNotContain("SELECT order_ref FROM dbo.procurement", shop);
        Assert.DoesNotContain("SELECT order_ref FROM dbo.procurement", control);
        Assert.Contains("WHERE sy.contract_id=@id AND m.kind=N'out'", control);
        Assert.DoesNotContain("SELECT m.position_id,m.qty,m.[date]", supplier);
    }

    [Fact]
    public void IssueDatesPreserveMovementOrderPrecisionAndMissingDates()
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(long));
        table.Columns.Add("qty", typeof(decimal));
        table.Columns.Add("date", typeof(DateTime));
        table.Rows.Add(1L, 1.25m, new DateTime(2026, 11, 27));
        table.Rows.Add(1L, 0.125m, DBNull.Value);
        table.Rows.Add(2L, 3m, new DateTime(2026, 11, 28));
        using var reader = table.CreateDataReader();
        var result = ProcurementRequestPlanner.ReadIssueDates(reader);
        Assert.Equal("1,25 (27.11.26), 0,125 (б/д)", result[1]);
        Assert.Equal("3 (28.11.26)", result[2]);
    }

    [Fact]
    public void CancelledReportAndInvalidIdDoNotOpenSql()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProcurementRequestPlanner.ReadReport(
            "invalid", 1, ProcurementRequestPlanner.ReportKind.Shop, token: cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProcurementRequestPlanner.ReadReport(
            "invalid", 0, ProcurementRequestPlanner.ReportKind.Control));
    }

    [Fact]
    public void SourceKeepsRevisionFenceAndDoesNotReopenConnectionsForEachResult()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db", "ProcurementRequestPlanner.ReportRead.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db", "ProcurementRequestPlanner.ReportRead.cs"));
        Assert.Contains("expectedRevision is not null && revision != expectedRevision", source);
        Assert.Contains("using (var cmd = new SqlCommand(query, conn))", source);
        Assert.Contains("SqlServerProcurement.ReadBuyRows(reader, day, token)", source);
        Assert.Contains("if (Revision() != revision || DateTime.Today != day)", source);
        Assert.DoesNotContain("BuyRows(connectionString", source);
        Assert.DoesNotContain("ChildPositionIds(connectionString", source);
        Assert.DoesNotContain("NextNumber(connectionString", source);
    }
}
