using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

/// <summary>Последние цены и история каталога из Python builder.py.</summary>
public sealed class CatalogReadTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");

    public CatalogReadTests()
    {
        _conn.Open();
        Database.EnsureSchema(_conn);
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public void Entries_UsesLatestPositiveCustomerAndWorkPricesIndependently()
    {
        var obj = Documents.CreateObject(_conn, "Школа");
        var doc = Documents.CreateContract(_conn, obj, "kp", "1", "", "Тест");
        var sys = Documents.AddSystem(_conn, doc, "П1");
        var item = Catalog.ItemId(_conn, "Клапан", "шт")!.Value;
        var p1 = Documents.AddPosition(_conn, sys, "Клапан", "шт", 1, 180m);
        RowWriter.Update(_conn, "positions", p1, new Dictionary<string, object?>
        { ["catalog_item_id"] = item, ["purchase_price"] = 120m, ["work_price"] = 25m });
        var p2 = Documents.AddPosition(_conn, sys, "Клапан", "шт", 1, 90m);
        RowWriter.Update(_conn, "positions", p2, new Dictionary<string, object?>
        { ["catalog_item_id"] = item, ["purchase_price"] = 60m, ["work_price"] = 10m });
        var p3 = Documents.AddPosition(_conn, sys, "Клапан", "шт", 1, 0m);
        RowWriter.Update(_conn, "positions", p3, new Dictionary<string, object?>
        { ["catalog_item_id"] = item, ["purchase_price"] = 5m, ["work_price"] = 0m });
        var empty = Catalog.ItemId(_conn, "Без цен", "м")!.Value;

        var entries = Catalog.Entries(_conn);
        Assert.Equal(2, entries.Count);
        var valve = Assert.Single(entries.Where(e => e.Id == item));
        Assert.Equal(90m, valve.Price); // последняя положительная, не максимальная
        Assert.Equal(60m, valve.PurchasePrice); // из ТОЙ ЖЕ строки, что цена
        Assert.Equal(10m, valve.WorkPrice);
        Assert.Equal(3, valve.Uses);
        var noPrices = Assert.Single(entries.Where(e => e.Id == empty));
        Assert.Null(noPrices.Price);
        Assert.Null(noPrices.PurchasePrice);
        Assert.Null(noPrices.WorkPrice);
        Assert.Equal(0, noPrices.Uses);
    }

    [Fact]
    public void PriceHistory_IncludesZeroPricesInNewestFirstOrder()
    {
        var obj = Documents.CreateObject(_conn, "Магазин");
        var doc = Documents.CreateContract(_conn, obj, "main", "7", "01.11.2026", "ОВ");
        var sys = Documents.AddSystem(_conn, doc, "ПВ1");
        var item = Catalog.ItemId(_conn, "Труба", "м")!.Value;
        var p1 = Documents.AddPosition(_conn, sys, "Труба", "м", 2, 10m);
        var p2 = Documents.AddPosition(_conn, sys, "Труба", "м", 3, 0m);
        RowWriter.Update(_conn, "positions", p1, new Dictionary<string, object?>
        { ["catalog_item_id"] = item, ["purchase_price"] = 7m });
        RowWriter.Update(_conn, "positions", p2, new Dictionary<string, object?>
        { ["catalog_item_id"] = item });

        var rows = Catalog.PriceHistory(_conn, item);
        Assert.Equal(2, rows.Count);
        Assert.Equal(0m, rows[0].Price);
        Assert.Equal(3m, rows[0].Qty);
        Assert.Equal(10m, rows[1].Price);
        Assert.Equal(7m, rows[1].PurchasePrice);
        Assert.Equal("Магазин", rows[0].Object);
        Assert.Equal("Договор № 7 от 01.11.2026 — ОВ", rows[0].Contract);
        Assert.Equal("01.11.2026", rows[0].Date);
        Assert.Equal("ПВ1", rows[0].System);
        Assert.Equal(doc, rows[0].ContractId);
    }
}
