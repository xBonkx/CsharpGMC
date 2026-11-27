using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ScopedExportSourceTests
{
    [Fact]
    public void InvalidPrintIdIsRejectedBeforeConnectionAccess()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Workspace.ReadDocumentMetadata(null!, 0));
    }

    [Fact]
    public void ExportUsesScopedHeaderAndCapturedProfile()
    {
        var source = Source("MainWindow.ContractExport.cs");
        Assert.Contains("Workspace.ReadDocumentMetadata(conn, contractId)", source);
        Assert.DoesNotContain("Workspace.Documents(conn)", source);
        Assert.Contains("LoadKpPrintData(connectionString, doc.Id)", source);
        Assert.Contains("if (_kpExportInProgress ||", source);
        Assert.Contains("finally { _kpExportInProgress = false; }", source);
    }

    [Fact]
    public void ControlReportChecksRevisionAfterReaderDisposalAndGuardsLateDialog()
    {
        var source = Source("ProcurementControlExport.cs");
        Assert.Contains("ProcurementRequestPlanner.ReadReport(connectionString, contractId,", source);
        Assert.Contains("ProcurementRequestPlanner.ReportKind.Control", source);
        var pane = Source("ProcurementPane.Exports.cs");
        Assert.Contains("if (_controlExportBusy || !IsLoaded) return;", pane);
        Assert.Contains("if (!IsCurrent()) return;", pane);
        Assert.Contains("finally { _controlExportBusy = false; }", pane);
    }

    [Fact]
    public void SummaryExportCancelledOnDetachAndAuthoritativeInvalidation()
    {
        Assert.Contains("_summaryExcelCancellation?.Cancel();", Source("EstimateView.xaml.cs"));
        Assert.Contains("_summaryExcelCancellation?.Cancel();", Source("EstimateView.CostSummary.cs"));
    }

    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }
}
