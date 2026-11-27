using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementRequestPlannerTests
{
    private static SqlServerProcurement.BuyRow Row(
        long id, string name, decimal qty, decimal? cost, string supplier = "Поставщик",
        string status = "none", bool work = false, bool extra = false, string system = "ОВ") =>
        new(id, system, name, "шт", qty, 100m, status,
            SqlServerProcurement.StatusLabel(status), "#FFFFFF", "#000000",
            supplier, "", "", "", cost, null, "", "", false, 0m, 0m,
            work, extra, Array.Empty<string>());

    [Fact]
    public void ReportChildrenUseAlreadyReadParentIdsIncludingWorkAndExtra()
    {
        var data = new[]
        {
            Row(1, "Родитель", 1, null),
            Row(2, "Комплект", 1, null) with { ParentPositionId = 1 },
            Row(3, "Работа", 1, null, work: true) with { ParentPositionId = 1 },
            Row(4, "Вне КП", 1, null, extra: true) with { ParentPositionId = 1 }
        };
        var children = ProcurementRequestPlanner.ChildPositionIds(data);
        Assert.True(children.SetEquals(new long[] { 2, 3, 4 }));
        Assert.Single(ProcurementRequestPlanner.CreateRows(data, children, "Поставщик"));
    }

    [Fact]
    public void ShopOrder_SelectedShopRowsOverrideFullQueue()
    {
        var data = new[]
        {
            Row(1, "Клапан", 2, null, status: "issued") with { Source = "shop" },
            Row(2, "Короб", 4, null, status: "shop_queued") with { Source = "shop" },
            Row(3, "Вставка", 1, null, status: "shop_work") with { Source = "shop", ParentPositionId = 2 },
            Row(4, "Покупка", 3, null, status: "shop_queued"),
            Row(5, "Монтаж", 2, null, work: true) with { Source = "shop" }
        };
        Assert.Equal(new long[] { 1 }, ProcurementRequestPlanner.SelectShopRows(data,
            new HashSet<long> { 1, 4, 5 }).Select(p => p.PositionId));
        Assert.Equal(new long[] { 2, 3 }, ProcurementRequestPlanner.SelectShopRows(data,
            new HashSet<long> { 4 }).Select(p => p.PositionId));
        Assert.Equal(new long[] { 2, 3 }, ProcurementRequestPlanner.SelectShopRows(data,
            new HashSet<long>()).Select(p => p.PositionId));
    }

    [Fact]
    public void ShopPrintPreview_EditsDoNotMutateSourceAndInvalidRowsAreRejected()
    {
        var original = Row(1, "Короб", 3, null) with { Source = "shop", Replacement = "Замена", Note = "Срочно" };
        var line = Assert.Single(ProcurementRequestPlanner.ShopPrintRows(new[] { original }));
        Assert.Equal("Замена; Срочно", line.Note);
        var edited = line with { Name = "Короб 200×200", Quantity = 1.123456m };
        Assert.Null(ProcurementRequestPlanner.ShopPrintError(new[] { edited }));
        Assert.Equal(3m, original.Qty);
        Assert.NotNull(ProcurementRequestPlanner.ShopPrintError(new[] { edited with { Quantity = -1 } }));
        Assert.NotNull(ProcurementRequestPlanner.ShopPrintError(new[] { edited with { Quantity = 1.1234567m } }));
        Assert.NotNull(ProcurementRequestPlanner.ShopPrintError(new[] { edited, edited }));
        Assert.Throws<ArgumentException>(() => ProcurementRequestPlanner.ShopPrintRows(
            new[] { original with { Source = "buy" } }));
    }

    [Fact]
    public void SupplierRequest_UsesOnlyAssignedNewTopLevelMaterialPositions()
    {
        var data = new[]
        {
            Row(1, "Датчик", 5m, 7m),
            Row(2, "Датчик", 3m, 9m, "Другой"),
            Row(3, "Датчик", 4m, 9m, status: "ordered"),
            Row(4, "Монтаж датчика", 2m, 10m, work: true),
            Row(5, "Датчик", 1m, 10m, extra: true),
            Row(6, "Дочерняя строка", 2m, 1m)
        };
        var selected = ProcurementRequestPlanner.CreateRows(data, new HashSet<long> { 6 }, "Поставщик");
        var only = Assert.Single(selected);
        Assert.Equal(5m, only.Quantity);
        Assert.Equal(new long[] { 1 }, only.PositionIds);
    }

    [Fact]
    public void MergedRequest_SumsQuantitiesAndKeepsOriginalIdsAndSystems()
    {
        var data = new[]
        {
            Row(10, "Клапан", 2m, 100m, system: "ОВ-1"),
            Row(11, " клапан ", 3m, 120m, system: "ОВ-2")
        };
        var lines = ProcurementRequestPlanner.CreateRows(data, new HashSet<long>(), "Поставщик");
        var line = Assert.Single(lines);
        Assert.Equal(5m, line.Quantity);
        Assert.Equal(112m, line.UnitPrice);
        Assert.Equal(560m, line.Amount);
        Assert.Equal(new long[] { 10, 11 }, line.PositionIds);
        Assert.Equal(new[] { "ОВ-1", "ОВ-2" }, line.Systems);
    }

    [Fact]
    public void UnmergedRequest_KeepsSameNamedItemsSeparated()
    {
        var data = new[] { Row(10, "Клапан", 2m, null), Row(11, "Клапан", 3m, 100m) };
        Assert.Equal(2, ProcurementRequestPlanner.CreateRows(
            data, new HashSet<long>(), "Поставщик", merge: false).Count);
    }

    [Fact]
    public void NoPriceRequest_DoesNotInventSupplierPrice()
    {
        var line = Assert.Single(ProcurementRequestPlanner.CreateRows(
            new[] { Row(10, "Клапан", 2m, null) }, new HashSet<long>(), "Поставщик"));
        Assert.Null(line.UnitPrice);
        Assert.Null(line.Amount);
    }

    [Fact]
    public void SupplierCounts_DoNotCountExtraWorkChildrenOrOtherStages()
    {
        var rows = new[]
        {
            Row(1, "А", 1, null), Row(2, "Б", 1, null),
            Row(3, "В", 1, null, "Цех"), Row(4, "Г", 1, null, extra: true),
            Row(5, "Д", 1, null, status: "ordered"), Row(6, "Е", 1, null)
        };
        var counts = ProcurementRequestPlanner.SupplierCounts(rows, new HashSet<long> { 6 });
        Assert.Equal("Поставщик", counts[0].Supplier);
        Assert.Equal(2, counts[0].Count);
        Assert.Equal("Цех", counts[1].Supplier);
    }
}
