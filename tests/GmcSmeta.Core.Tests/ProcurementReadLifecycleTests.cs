using System.Threading;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementReadLifecycleTests
{
    [Fact]
    public void PreCancelledReadsDoNotOpenOrExecuteSql()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var connection = new SqlConnection();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlServerProcurement.ReadBuySnapshotIfChanged("", 1, "", null, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlServerProcurement.MovementsForPosition(connection, 1, 1, cancellation.Token));
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("reordered", true)]
    [InlineData("missing", false)]
    [InlineData("wrong", false)]
    [InlineData("duplicate", false)]
    [InlineData("midnight", false)]
    [InlineData("too-large", false)]
    public void SparseReadRequiresExactIdsAndDay(string scenario, bool expected)
    {
        var day = new DateTime(2026, 11, 28);
        long[] ids = { 1, 2 }, returned = { 1, 2 };
        var after = day;
        switch (scenario)
        {
            case "reordered": returned = new long[] { 2, 1 }; break;
            case "missing": returned = new long[] { 1 }; break;
            case "wrong": returned = new long[] { 1, 3 }; break;
            case "duplicate": returned = new long[] { 1, 1 }; break;
            case "midnight": after = day.AddDays(1); break;
            case "too-large": ids = returned = Enumerable.Range(1, 257).Select(i => (long)i).ToArray(); break;
        }
        Assert.Equal(expected, SqlServerProcurement.CanPublishBuyDelta(ids, returned, day, after));
    }

    [Fact]
    public void SparseStructureTracksExactParentAndOrder()
    {
        var key = new SqlServerProcurement.BuyRowStructure(1, 2, 3, 4, 5, 6);
        Assert.Equal(key, key with { });
        Assert.NotEqual(key, key with { ParentPositionId = 7 });
        Assert.NotEqual(key, key with { ParentSystemId = 7 });
        Assert.NotEqual(key, key with { SystemOrder = 7 });
        Assert.NotEqual(key, key with { PositionOrder = 7 });
    }

    [Fact]
    public void OnlyOrdinaryReloadsAreCancellableAndHistoryUsesOneConnection()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ProcurementPane.Loading.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var loader = File.ReadAllText(Path.Combine(app, "ProcurementPane.Loading.cs"));
        var pane = File.ReadAllText(Path.Combine(app, "ProcurementPane.xaml.cs"));
        var incremental = File.ReadAllText(Path.Combine(app, "ProcurementPane.Incremental.cs"));
        Assert.Contains("bool requiredReadback = true", loader);
        Assert.Contains("requiredReadback ? null : new CancellationTokenSource()", loader);
        Assert.Contains("token.IsCancellationRequested || generation != _reloadGeneration", loader);
        Assert.Contains("token.IsCancellationRequested || generation != _historyLoadToken", loader);
        Assert.Contains("conn, _contractId, row.PositionId, token", loader);
        Assert.Contains("CancelMovementHistory();", pane);
        Assert.Contains("else SuspendBuyReads();", pane);
        Assert.Contains("TryReloadAsync(allowUnchanged: true, requiredReadback: false)", pane);
        Assert.Contains("ReloadAsync(preserveStatus: true)", incremental);
        Assert.Contains("catch (Exception) when (!requiredReadback &&", loader);
        Assert.Contains("_ = _owner.TryReloadAsync();", pane);
        Assert.Contains("_allRows = items;", loader);
        Assert.Contains("await Task.Run(() => PrepareBuyRows(accepted, token), token)", loader);
        var projection = loader.IndexOf("await Task.Run(() => PrepareBuyRows", StringComparison.Ordinal);
        var guard = loader.IndexOf("generation != _reloadGeneration", projection, StringComparison.Ordinal);
        var publish = loader.IndexOf("_allRows = items;", StringComparison.Ordinal);
        Assert.True(projection >= 0 && guard > projection && publish > guard);
        Assert.Contains("if (accepted.Day != DateTime.Today)", loader);
        var delay = loader.IndexOf("await Task.Delay(150, token)", StringComparison.Ordinal);
        var historySql = loader.IndexOf("SqlServerDatabase.OpenAsync(_connectionString, token)", delay, StringComparison.Ordinal);
        Assert.True(delay >= 0 && historySql > delay);
        Assert.Contains("TryReloadAsync(allowUnchanged: true, requiredReadback: false)", loader);
        Assert.Contains("old.SnapshotStructure != row.Structure", incremental);
        Assert.Contains("CanPublishBuyDelta(ids, rows.Select", incremental);
        Assert.Contains("_snapshotDay == DateTime.Today", pane);
        var sql = File.ReadAllText(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerProcurement.cs"));
        Assert.Contains("for (var attempt = 0; attempt < 2; attempt++)", sql);
        Assert.Contains("if (!CanReuseBuySnapshot(revision, confirmed, day, DateTime.Today)) continue;", sql);
    }
}
