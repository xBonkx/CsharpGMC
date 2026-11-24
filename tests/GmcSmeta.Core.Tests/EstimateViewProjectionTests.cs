using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateViewProjectionTests
{
    private static EstimateRow Section(int sort, int id, string name, int level = 0) =>
        new(RowKind.System, level, sort, 0, false, "", name, "", 0, 0, 0, null, false, "")
        { SystemId = id };

    private static EstimateRow Pos(int sort, int id, int section, string name,
        decimal price, decimal qty = 1, int? parent = null, bool outside = false) =>
        new(RowKind.Position, parent is null ? 1 : 2, sort, id, parent is not null,
            id.ToString(), name, "шт", qty, price, price * qty, null, outside, "")
        { SystemId = section, ParentPositionId = parent };

    private static EstimateRow[] Example() =>
    [
        Section(0, 10, "П1"),
        Pos(1, 101, 10, "В10", 10),
        Pos(2, 102, 10, "Комплект", 0, parent: 101),
        Pos(3, 103, 10, "В2", 0),
        Section(4, 20, "В1"),
        Pos(5, 201, 20, "Решётка", 50),
        Pos(6, 202, 20, "Клапан", 0)
    ];

    [Fact]
    public void DefaultView_IsUnchangedAndDoesNotMutateSource()
    {
        var rows = Example();
        var projected = EstimateViewProjection.Apply(rows);
        Assert.Same(rows, projected);
        Assert.Equal(rows.Select(x => x.SortKey), projected.Select(x => x.SortKey));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, rows.Select(x => x.SortKey));
    }

    [Fact]
    public void SortByName_NaturalOrder_PreservesComponentImmediatelyAfterParent()
    {
        var view = EstimateViewProjection.Apply(Example(), sort: EstimateViewProjection.ByName);
        Assert.Equal(new[] { 0, 3, 1, 2, 4, 6, 5 }, view.Select(x => x.SortKey));
    }

    [Fact]
    public void SortByAmount_MovesMostExpensiveWithinSystemOnly()
    {
        var view = EstimateViewProjection.Apply(Example(), sort: EstimateViewProjection.ByAmount);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 }, view.Select(x => x.SortKey));
    }

    [Fact]
    public void ListView_ExcludesSectionHeaders_AndCanSortAcrossSections()
    {
        var view = EstimateViewProjection.Apply(Example(),
            sort: EstimateViewProjection.ByPrice, flat: true);
        Assert.Equal(new[] { 201, 101, 102, 103, 202 }, view.Select(x => x.Id));
        Assert.DoesNotContain(view, row => row.IsSystem);
    }

    [Fact]
    public void OnlyNoPrice_IncludesSectionsAndRelatedComponents_NotOtherPricedRows()
    {
        var rows = Example();
        var view = EstimateViewProjection.Apply(rows, noPriceOnly: true);
        Assert.Equal(new[] { 0, 3, 4, 6 }, view.Select(x => x.SortKey));
        Assert.Equal(7, rows.Length);  // no mutation
    }

    [Fact]
    public void NoPriceSearch_DoesNotShowMatchingPaidPosition()
    {
        var view = EstimateViewProjection.Apply(Example(), "Решётка", noPriceOnly: true);
        Assert.Empty(view);
        var unpaid = EstimateViewProjection.Apply(Example(), "Клапан", noPriceOnly: true);
        Assert.Equal(new[] { 4, 6 }, unpaid.Select(x => x.SortKey));
    }

    [Fact]
    public void ChildOfNoPricePosition_IsRetained()
    {
        var rows = new[]
        {
            Section(0, 10, "Система"), Pos(1, 100, 10, "Пустая", 0),
            Pos(2, 101, 10, "Датчик", 0, parent: 100),
            Pos(3, 102, 10, "Платная", 5)
        };
        var view = EstimateViewProjection.Apply(rows, noPriceOnly: true);
        Assert.Equal(new[] { 0, 1, 2 }, view.Select(x => x.SortKey));
    }

    [Fact]
    public void SubsystemKeepsItsOwnPositionsAndParentSection()
    {
        var rows = new[]
        {
            Section(0, 10, "Главная"), Pos(1, 100, 10, "Заполнена", 4),
            Section(2, 11, "Подраздел", 1),
            new EstimateRow(RowKind.Position, 2, 3, 101, false, "3",
                "Без цены", "шт", 2, 0, 0, null, false, "") { SystemId = 11 }
        };
        var view = EstimateViewProjection.Apply(rows, noPriceOnly: true);
        Assert.Equal(new[] { 0, 2, 3 }, view.Select(x => x.SortKey));
    }

    [Theory]
    [InlineData("В2", "В10")]
    [InlineData("Решётка 02", "Решётка 11")]
    [InlineData("a9", "a12")]
    public void NaturalComparison_OrdersNumbersAsNumbers(string left, string right) =>
        Assert.True(EstimateViewProjection.NaturalCompare(left, right) < 0);

    [Fact]
    public void FrozenRanks_PreserveOrderAfterPriceChanges()
    {
        var before = new[]
        {
            Section(0, 10, "П1"),
            Pos(1, 100, 10, "Старая", 100),
            Pos(2, 200, 10, "Новая", 20)
        };
        var first = EstimateViewProjection.Apply(before, sort: EstimateViewProjection.ByPrice);
        var frozen = first.Where(row => !row.IsSystem)
            .Select((row, rank) => (row.Id, rank))
            .ToDictionary(pair => pair.Id, pair => pair.rank);
        var edited = before.Select(row => row.Id == 200
            ? row with { Price = 500, Amount = 500 } : row).ToArray();
        var stable = EstimateViewProjection.Apply(edited,
            sort: EstimateViewProjection.ByPrice, frozenOrder: frozen);
        Assert.Equal(new[] { 0, 1, 2 }, stable.Select(row => row.SortKey));
        var fresh = EstimateViewProjection.Apply(edited,
            sort: EstimateViewProjection.ByPrice);
        Assert.Equal(new[] { 0, 2, 1 }, fresh.Select(row => row.SortKey));
        Assert.Equal(20m, before[2].Price); // read-only projection
    }

    [Fact]
    public void UnknownSortIsRejectedWithoutDataMutation() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EstimateViewProjection.Apply(Example(), sort: "sql-order-by"));

    [Fact]
    public void QuantitySortDescending_KeepsSourceOrderForTies()
    {
        var rows = new[] { Section(0, 10, "П1"), Pos(1, 1, 10, "А", 1, 1),
            Pos(2, 2, 10, "Б", 1, 10), Pos(3, 3, 10, "В", 1, 10) };
        var view = EstimateViewProjection.Apply(rows, sort: EstimateViewProjection.ByQuantity);
        Assert.Equal(new[] { 0, 2, 3, 1 }, view.Select(x => x.SortKey));
    }
}
