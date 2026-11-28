using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementShipmentAcceptanceTests
{
    [Fact]
    public void FixedShipmentLedgerGuardsBothIdempotentFixAndDestructiveUnfix()
    {
        var source = Read("GmcSmeta.Core/Db/SqlServerShipments.cs");
        var fix = Slice(source, "SaveResult Fix(", "SaveResult Unfix(");
        var unfix = Slice(source, "SaveResult Unfix(", "private static string? FixedLedgerProblem(");
        var ledger = Slice(source, "private static string? FixedLedgerProblem(", "private static bool IsActiveShipment(");
        InOrder(fix, "BeginTransaction(IsolationLevel.Serializable)", "if (alreadyFixed)",
            "FixedLedgerProblem(conn, tx, shipmentId)", "SaveResult.Fail(problem, rev)",
            "SaveResult(true, null, rev)");
        InOrder(unfix, "BeginTransaction(IsolationLevel.Serializable)", "LockRevision(conn, tx)",
            "FixedLedgerProblem(conn, tx, shipmentId)", "SaveResult.Fail(problem, rev)",
            "DELETE FROM dbo.movements", "fixed=0, fixed_at=NULL,fixed_by=N''", "tx.Commit()");
        foreach (var guard in new[] { "FULL OUTER JOIN", "l.n IS NULL", "m.n IS NULL", "l.n<>1",
                     "m.n<>1", "m.bad<>0", "l.qty IS NULL", "m.qty IS NULL", "l.qty<=0.000001",
                     "ABS(l.qty-m.qty)>0.000001", "c.id IS NULL", "c.object_id<>sh.object_id",
                     "SqlInteger.RequireInt64(count.ExecuteScalar()) == 0" })
            Assert.Contains(guard, ledger);
    }

    [Fact]
    public void ShipmentExportCapturesRevisionAndRejectsIncompleteSnapshot()
    {
        var source = Read("GmcSmeta.Core/Db/SqlServerShipmentPrintData.cs");
        InOrder(source, "var before = SqlServerDatabase.Revision(conn)",
            "before != expectedRevision", "var snapshot = ReadCore(conn, objectId, shipmentId)",
            "before == SqlServerDatabase.Revision(conn)", "return snapshot");
        Assert.Contains("if (expectedRevision is not null) break;", source);
        InOrder(source, "c.object_id=@object", "SELECT COUNT_BIG(*) FROM dbo.shipment_lines",
            "SqlInteger.RequireInt64(count.ExecuteScalar()) != lines.Count", "return new Snapshot");
        var ui = Read("GmcSmeta.App/ShipmentsPane.xaml.cs");
        var xlsx = Slice(ui, "private async void Export_Click(", "private async void ExportPdf_Click(");
        var pdf = ui[ui.IndexOf("private async void ExportPdf_Click(", StringComparison.Ordinal)..];
        InOrder(xlsx, "var revision = _revision;", "ContractDocumentSave.XlsxAsync",
            "SqlServerShipmentPrintData.Read(conn, _objectId, current.Id, revision)", "ShipmentExcelForms.Create(snapshot)");
        InOrder(pdf, "var revision = _revision;", "ContractDocumentSave.PdfAsync",
            "SqlServerShipmentPrintData.Read(conn, _objectId, current.Id, revision)");
    }

    [Fact]
    public void DirectorCoverageDoesNotDropCompletelyUnpricedContract()
    {
        var combined = ContractFinance.Sum(new[]
        {
            new ContractFinance(Cost: 0m, CoveredRevenue: 100m) { MaterialRevenue = 100m },
            new ContractFinance(MissingPrices: 2) { MaterialRevenue = 300m },
            new ContractFinance(OutsideCost: 10m, SmrCost: 20m)
        });
        Assert.Equal(400m, combined.MaterialRevenue);
        Assert.Equal(100m, combined.CoveredRevenue);
        Assert.Equal(2, combined.MissingPrices);
        Assert.Equal((decimal?)25m, ContractFinance.CoveragePercent(combined.MaterialRevenue, combined.CoveredRevenue));
        var director = Read("GmcSmeta.Core/Db/SqlServerDirector.cs");
        InOrder(director, "revenue += f.MaterialRevenue;", "covered += f.CoveredRevenue;",
            "ContractFinance.CoveragePercent(revenue, covered)");
    }

    private static string Read(string relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GmcSmeta.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", relative));
    }

    private static string Slice(string source, string start, string end)
    {
        var first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, start);
        var last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, end);
        return source[first..last];
    }

    private static void InOrder(string source, params string[] fragments)
    {
        var offset = 0;
        foreach (var fragment in fragments)
        {
            var index = source.IndexOf(fragment, offset, StringComparison.Ordinal);
            Assert.True(index >= 0, "Missing or out of order: " + fragment);
            offset = index + fragment.Length;
        }
    }
}
