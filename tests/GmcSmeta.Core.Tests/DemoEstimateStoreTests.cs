using GmcSmeta.Core.Demo;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class DemoEstimateStoreTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(2500)]
    [InlineData(10000)]
    [InlineData(50000)]
    public void SeedIsDeterministicAndContainsFourSections(int count)
    {
        var rows = new DemoEstimateStore(count).Snapshot();
        Assert.Equal(count + 4, rows.Count);
        Assert.Equal(4, rows.Count(r => r.IsSystem));
        Assert.Equal(count, rows.Count(r => !r.IsSystem));
        Assert.Equal(Enumerable.Range(0, rows.Count), rows.Select(r => r.SortKey));
        Assert.Equal(rows, new DemoEstimateStore(count).Snapshot());
        Assert.Contains(rows, r => !r.IsSystem && r.HasNoPrice);
    }

    [Fact]
    public void SeedMatchesPythonFixtureContract()
    {
        var rows = new DemoEstimateStore().Snapshot();
        Assert.Equal("Система П1", rows[0].Name);
        Assert.Equal("Воздуховод оцинкованный 1", rows[1].Name);
        Assert.Equal(0m, rows[1].Amount);
        Assert.Null(rows[1].PurchasePrice);
        Assert.Equal("Отвод 90° 2", rows[2].Name);
        Assert.Equal(1.25m, rows[2].Qty);
        Assert.Equal(142.50m, rows[2].Price);
        Assert.Equal(178.13m, rows[2].Amount);
        Assert.Equal(85.50m, rows[2].PurchasePrice!.Value);
    }

    [Fact]
    public void MutationsStayInMemoryAndResetRestoresSeed()
    {
        var store = new DemoEstimateStore();
        var original = store.Snapshot();
        var revision = store.Revision;
        int id = store.AddPosition(1, "Тест", "шт", 2m, 12.345m);
        Assert.Equal(101, id);
        Assert.Equal(24.69m, store.Snapshot().Single(r => !r.IsSystem && r.Id == id).Amount);
        store.UpdatePosition(id, "Тест 2", "шт", 3, 10);
        Assert.Equal(30m, store.Snapshot().Single(r => !r.IsSystem && r.Id == id).Amount);
        store.DeletePosition(1);
        Assert.Equal(revision + 3, store.Revision);
        Assert.Equal(100, original.Count(r => !r.IsSystem));
        Assert.Equal(original, new DemoEstimateStore().Snapshot());
        store.Reset();
        Assert.Equal(original, store.Snapshot());
    }

    [Fact]
    public void InvalidMutationsDoNotAdvanceRevision()
    {
        var store = new DemoEstimateStore();
        var revision = store.Revision;
        Assert.Throws<ArgumentException>(() => store.AddPosition(99, "Тест", "шт", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.UpdatePosition(1, "Тест", "шт", -1, 1));
        Assert.Throws<ArgumentException>(() => store.DeletePosition(0));
        Assert.Equal(revision, store.Revision);
    }

    [Fact]
    public void DemoArgumentRejectsSqlEvenAlongsideOtherOptions()
    {
        Assert.False(DemoMode.IsRequested(["--smoke-test"]));
        Assert.Throws<InvalidOperationException>(() => DemoMode.RejectSqlConnection(["--smoke-test", "--demo"]));
        DemoMode.RejectSqlConnection([]);
    }
}
