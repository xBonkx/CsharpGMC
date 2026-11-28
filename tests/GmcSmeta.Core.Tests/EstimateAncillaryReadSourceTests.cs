using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateAncillaryReadSourceTests
{
    [Fact]
    public void PreCancelledAncillaryReadsDoNotAccessClosedConnection()
    {
        using var connection = new SqlConnection();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var token = cancel.Token;
        Assert.ThrowsAny<OperationCanceledException>(() => SqlServerUndo.Snapshot(connection, null, 1, token));
        Assert.ThrowsAny<OperationCanceledException>(() => SqlServerUndo.ReadConsistent("", 1, token: token));
        Assert.ThrowsAny<OperationCanceledException>(() => Workspace.ReadDocumentMetadata(connection, 1, token));
        Assert.ThrowsAny<OperationCanceledException>(() => SqlServerSpecUpdate.LatestRevision(connection, null, 1, token));
    }

    [Theory]
    [InlineData("src/GmcSmeta.App/EstimateView.xaml.cs", "Workspace.ReadDocumentMetadata(read, _contractId, token)")]
    [InlineData("src/GmcSmeta.App/EstimateView.xaml.cs", "SqlServerSpecUpdate.LatestRevision(read, null, _contractId, token)")]
    [InlineData("src/GmcSmeta.App/EstimateView.xaml.cs", "SqlServerUndo.Snapshot(read, null, _contractId, token)")]
    [InlineData("src/GmcSmeta.App/EstimateView.xaml.cs", "estimate_revision_before_begin")]
    [InlineData("src/GmcSmeta.App/EstimateView.xaml.cs", "estimate_revision_after_end")]
    [InlineData("src/GmcSmeta.App/EstimateView.xaml.cs", "estimate_ui_row_reconcile")]
    [InlineData("src/GmcSmeta.App/EstimateView.xaml.cs", "estimate_ui_items_source")]
    [InlineData("src/GmcSmeta.App/EstimateView.Groups.cs", "estimate_ui_group_items_source")]
    [InlineData("src/GmcSmeta.Core/Domain/Estimate.cs", "estimate_worker_dto_projection")]
    [InlineData("src/GmcSmeta.App/EstimateView.Undo.cs", "ReadConsistent(_sqlConnectionString, _contractId, expectedRevision)")]
    [InlineData("src/GmcSmeta.App/MainWindow.xaml.cs", "estimate_cache_hit")]
    [InlineData("src/GmcSmeta.App/MainWindow.xaml.cs", "estimate_cache_miss")]
    [InlineData("src/GmcSmeta.App/MainWindow.EstimateCache.cs", "estimate_cache_after_eviction")]
    [InlineData("src/GmcSmeta.App/EstimateView.MaterialsPanel.cs", "estimate_lazy_materials")]
    [InlineData("src/GmcSmeta.App/EstimateView.SummaryPanel.cs", "estimate_lazy_summary")]
    [InlineData("src/GmcSmeta.App/EstimateView.CostTotals.cs", "estimate_lazy_totals")]
    [InlineData("src/GmcSmeta.App/EstimateView.xaml.cs", "estimate_lazy_acts")]
    [InlineData("src/GmcSmeta.App/EstimateView.xaml.cs", "estimate_lazy_procurement")]
    [InlineData("src/GmcSmeta.App/EstimateView.xaml.cs", "estimate_lazy_shipments")]
    public void DiagnosticsAndTokenPathsAreConnected(string file, string expected)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, file))) root = root.Parent;
        Assert.NotNull(root);
        Assert.Contains(expected, File.ReadAllText(Path.Combine(root!.FullName, file)));
    }
}
