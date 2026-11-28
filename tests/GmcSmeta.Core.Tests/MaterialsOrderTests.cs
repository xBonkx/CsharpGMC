using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class MaterialsOrderTests
{
    private static readonly (string Name, string Hex)[] Colors =
        { ("Жёлтый", "#FFF1A8"), ("Зелёный", "#D4F0C8") };

    private static CostMaterialsPosition Row(long id, string name, decimal? cost,
        decimal price = 100m, decimal qty = 1m, string color = "", decimal? fact = null) =>
        new(id, 1, null, name, "шт", qty, price, cost, fact, "", color, "", false);

    private static long[] Ids(IEnumerable<CostMaterialsPosition> rows) =>
        rows.Select(p => p.Id).ToArray();

    [Fact]
    public void EditedPriceDoesNotMoveRowUntilExplicitReset()
    {
        var order = new MaterialsOrder();
        var rows = new[] { Row(1, "A", 10), Row(2, "B", 20) };
        Assert.Equal(new long[] { 1, 2 }, Ids(order.Order(7, "kp", "cost_up", rows, Colors)));
        rows[0] = rows[0] with { Plan = 30 };
        var frozen = order.Order(7, "kp", "cost_up", rows, Colors);
        Assert.Equal(new long[] { 1, 2 }, Ids(frozen));
        Assert.Equal(30m, frozen[0].Plan);
        order.Reset();
        Assert.Equal(new long[] { 2, 1 }, Ids(order.Order(7, "kp", "cost_up", rows, Colors)));
    }

    [Fact]
    public void ChangingDocumentModeOrSortResetsFrozenKeys()
    {
        var order = new MaterialsOrder();
        var rows = new[] { Row(1, "A", 10), Row(2, "B", 20) };
        order.Order(7, "kp", "cost_up", rows, Colors);
        rows[0] = rows[0] with { Plan = 30 };
        Assert.Equal(new long[] { 2, 1 }, Ids(order.Order(8, "kp", "cost_up", rows, Colors)));
        rows[0] = rows[0] with { Plan = 1 };
        Assert.Equal(new long[] { 1, 2 }, Ids(order.Order(8, "group", "cost_up", rows, Colors)));
        Assert.Equal(new long[] { 2, 1 }, Ids(order.Order(8, "group", "cost_down", rows, Colors)));
    }

    [Theory]
    [InlineData("cost_up")]
    [InlineData("cost_down")]
    [InlineData("costsum")]
    [InlineData("markup")]
    public void NullCostIsLastAndActualCostWins(string sort)
    {
        var order = new MaterialsOrder();
        var rows = new[] { Row(1, "A", null), Row(2, "B", 1, fact: 30),
            Row(3, "C", 10, qty: 2) };
        var expected = sort == "cost_up" ? new long[] { 3, 2, 1 } : new long[] { 2, 3, 1 };
        Assert.Equal(expected, Ids(order.Order(7, "kp", sort, rows, Colors)));
    }

    [Fact]
    public void ColorRankUsesPaletteOrderAndUnknownColorLast()
    {
        var rows = new[] { Row(1, "A", 5, color: "#d4f0c8"),
            Row(2, "B", 5, color: "#111111"), Row(3, "C", 5, color: "#FFF1A8") };
        Assert.Equal(new long[] { 3, 1, 2 }, Ids(new MaterialsOrder().Order(7, "kp", "color", rows, Colors)));
    }

    [Fact]
    public void EqualKeysKeepOriginalOrderAndKpReturnsCurrentOrder()
    {
        var order = new MaterialsOrder();
        var rows = new[] { Row(2, "A", 5), Row(1, "a", 5) };
        Assert.Equal(new long[] { 2, 1 }, Ids(order.Order(7, "kp", "name", rows, Colors)));
        Array.Reverse(rows);
        Assert.Equal(new long[] { 2, 1 }, Ids(order.Order(7, "kp", "name", rows, Colors)));
        Assert.Equal(new long[] { 1, 2 }, Ids(order.Order(7, "kp", "kp", rows, Colors)));
    }

    [Fact]
    public void NewAndRemovedPositionsUseCurrentRowsWithoutLosingFrozenOrder()
    {
        var order = new MaterialsOrder();
        var rows = new[] { Row(1, "A", 10), Row(2, "B", 20) };
        order.Order(7, "kp", "cost_up", rows, Colors);
        var changed = new[] { rows[1] with { Plan = 1 }, Row(3, "C", 15) };
        Assert.Equal(new long[] { 3, 2 }, Ids(order.Order(7, "kp", "cost_up", changed, Colors)));
    }
}
