using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class MetalWarehouseReadPerformanceTests
{
    [Fact]
    public void OnePassPlaceAggregationMatchesFormerScansIncludingSignedBalances()
    {
        var rows = Enumerable.Range(0, 25000).Select(i => new MetalPlaceView(
            i % 251, i % 3 == 0 ? MetalWarehouseRules.SupplierPlace : "Склад",
            i % 31 - 15, i % 11 - 5)).ToArray();
        var totals = MetalPlaceTotals.Aggregate(rows);
        foreach (var group in rows.GroupBy(r => r.MaterialId))
        {
            var own = group.Where(r => r.Place != MetalWarehouseRules.SupplierPlace);
            var supplier = group.Where(r => r.Place == MetalWarehouseRules.SupplierPlace);
            Assert.Equal(new MetalPlaceTotals(own.Sum(r => r.Area), own.Sum(r => r.Pieces),
                supplier.Sum(r => r.Area), supplier.Sum(r => r.Pieces)), totals[group.Key]);
        }
        Assert.Empty(MetalPlaceTotals.Aggregate(Array.Empty<MetalPlaceView>()));
        Assert.False(totals.ContainsKey(999));
    }

    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", file));
    }

    [Fact]
    public void JournalLoadsOnlyWhenVisibleAndUsesLookAhead()
    {
        var code = Source("GmcSmeta.App/MetalWarehouseWindow.xaml.cs");
        Assert.Contains("!JournalVisible || _busy || _journalLoading || !_journalDirty", code);
        Assert.Contains("if (_snapshot is not null && JournalVisible) _ = LoadJournal();", code);
        Assert.Contains("version != _journalVersion", code);
        Assert.Contains("rows.HasNext", code);
        Assert.Contains("_placesByMaterial?[selected.MaterialId]", code);
        Assert.Contains("_objectsByMaterial?[selected.MaterialId]", code);
        var sql = Source("GmcSmeta.Core/Db/SqlServerMetalWarehouse.Reads.cs");
        Assert.Contains("pageSize + (lookAhead ? 1 : 0)", sql);
        Assert.Contains("rows.Count > pageSize", sql);
        Assert.Contains("SqlDbType.NVarChar, 302", sql);
    }

    [Fact]
    public void ReportUsesKeysetAndChecksRevisionAfterStaging()
    {
        var code = Source("GmcSmeta.App/MetalWarehouseReport.cs");
        Assert.Contains("source.ReadJournalBefore(beforeId,1000)", code);
        Assert.Contains("beforeId = batch[^1].Id;", code);
        var start = code.IndexOf("using var prepared = ExportFilePublication.Stage", StringComparison.Ordinal);
        var check = code.IndexOf("RequireSameRevision(source, snapshot);", start, StringComparison.Ordinal);
        var publish = code.IndexOf("prepared.Publish();", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && check > start && publish > check);
    }
}
