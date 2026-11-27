using System.Threading;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Tests;

public sealed class ShipmentReadLifecycleTests
{
    [Fact]
    public void CancelledReadDoesNotExecuteSql()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var conn = new SqlConnection();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlServerShipments.ShipmentsOf(conn, 1, token: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlServerShipments.DocumentsOf(conn, 1, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlServerShipments.IssuableRows(conn, 1, token: cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlServerShipments.LinesOf(conn, 1, token: cancellation.Token));
    }

    [Fact]
    public void NavigationCancelsOnlyOrdinaryReadsAndRetainsRequestedId()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ShipmentsPane.Loading.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var loader = File.ReadAllText(Path.Combine(app, "ShipmentsPane.Loading.cs"));
        var pane = File.ReadAllText(Path.Combine(app, "ShipmentsPane.xaml.cs"));
        Assert.Contains("bool requiredReadback = true", loader);
        Assert.Contains("requiredReadback ? null : new CancellationTokenSource()", loader);
        Assert.Contains("if (_quantityBusy || _ordinaryShipmentCancellation is not", loader);
        Assert.Contains("_resumeShipmentId = _activeShipmentReadId;", loader);
        Assert.Contains("requestedId: _resumeShipmentId, requiredReadback: false", loader);
        Assert.Contains("token.IsCancellationRequested || generation != _loadGeneration", loader);
        Assert.Contains("SqlServerDatabase.RevisionAsync(conn, token)", loader);
        Assert.Contains("SqlServerShipments.IssuableRows(conn, _objectId, token: token)", loader);
        Assert.Contains("else CancelOrdinaryShipmentRead();", pane);
        Assert.Contains("await ReloadAsync(preserveStatus: true)", pane);
        Assert.Contains("if (attachment != _attachmentGeneration) ResumeShipmentReadIfNeeded();", pane);
        Assert.Contains("SqlServerShipments.ReadChangedRows(", pane);
        Assert.Contains("var unmatched = byId.Keys.ToHashSet();", loader);
        Assert.Contains("unmatched.Remove(row.PositionId);", loader);
        Assert.Contains("lines.Where(l => unmatched.Contains(l.PositionId))", loader);
        Assert.DoesNotContain("rows.Select(r => r.PositionId).ToHashSet()", loader);
    }
}
