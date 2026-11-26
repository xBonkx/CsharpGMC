using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateGroupTests
{
    private static EstimateRow Sy(int sort, int id, string name) =>
        new(RowKind.System, 0, sort, 0, false, "", name, "", 0, 0, 0, null, false, "")
        { SystemId = id };

    private static EstimateRow Pos(int sort, int id, int system, string name,
        string unit, decimal qty, decimal price, decimal? purchase = null,
        int? parent = null, bool extra = false) =>
        new(RowKind.Position, parent is null ? 1 : 2, sort, id, parent is not null,
            "", name, unit, qty, price, qty * price, purchase, extra, "")
        { SystemId = system, ParentPositionId = parent };

    private static EstimateRow[] Rows() =>
    [
        Sy(0, 1, "П1"),
        Pos(1, 101, 1, "Вентилятор Ø200", "шт.", 2, 300, 200),
        Pos(2, 111, 1, "Комплектующая", "шт", 1, 0, parent: 101),
        Pos(3, 103, 1, "Клапан", "шт", 1, 100),
        Sy(4, 2, "В1"),
        Pos(5, 201, 2, " вентилятор  Ø200 ", "ШТ", 3, 0, 200),
        Pos(6, 202, 2, "Воздуховод", "м.", 10, 5),
        Pos(7, 203, 2, "За пределами", "шт", 1, 99, extra: true)
    ];

    [Fact]
    public void SameGroups_MergeNormalizedNameAndUnit_AggregateWithoutMutating()
    {
        var source = Rows();
        var result = EstimateGroups.Build(source, EstimateGroups.Same);
        var groups = Assert.Single(result).Groups;
        var fan = Assert.Single(groups.Where(g => g.Name.Contains("Вентилятор")));
        Assert.Equal(new[] { 101, 201 }, fan.PositionIds);
        Assert.Equal(5m, fan.Quantity);
        Assert.Equal(600m, fan.Amount);
        Assert.Null(fan.CommonPrice);
        Assert.Equal(200m, fan.CommonPurchase);
        Assert.True(fan.MissingPrice);
        Assert.Equal(111, Assert.Single(fan.Components).Id);
        Assert.DoesNotContain(groups.SelectMany(g => g.Members), x => x.OutOfKp);
        Assert.Equal(8, source.Length);
        Assert.Equal(300m, source[1].Price);
    }

    [Fact]
    public void TypeGroups_UseFirstWordAndAmounts()
    {
        var types = EstimateGroups.Build(Rows(), EstimateGroups.Type);
        Assert.Contains(types, x => x.TypeKey == "вентилятор" && x.Amount == 600m);
        Assert.Contains(types, x => x.TypeKey == "клапан");
        Assert.Contains(types, x => x.TypeKey == "воздуховод");
        Assert.Equal(3, types.Count);
    }

    [Fact]
    public void MissingPriceOnly_ShowsWholeMixedGroupButNotOtherGroups()
    {
        var sections = EstimateGroups.Build(Rows(), EstimateGroups.Same, missingOnly: true);
        var fan = Assert.Single(Assert.Single(sections).Groups);
        Assert.Equal(2, fan.Members.Count);
        Assert.Contains(fan.Members, x => x.Price > 0);
        Assert.Contains(fan.Members, x => x.Price == 0);
    }

    [Fact]
    public void SearchMatchingOneMember_StillShowsEntireGroup()
    {
        var sections = EstimateGroups.Build(Rows(), EstimateGroups.Same, query: "вентилятор");
        var group = Assert.Single(Assert.Single(sections).Groups);
        Assert.Equal(2, group.Members.Count);
    }

    [Fact]
    public void NaturalSortAndFrozenGroups_PreserveOrder()
    {
        var rows = new[]
        {
            Sy(0, 1, "П1"),
            Pos(1, 11, 1, "В10", "шт", 1, 10),
            Pos(2, 12, 1, "В2", "шт", 1, 100),
        };
        var first = Assert.Single(EstimateGroups.Build(rows, EstimateGroups.Same,
            sort: EstimateViewProjection.ByName)).Groups;
        Assert.Equal(new[] { 12, 11 }, first.Select(x => x.Members[0].Id));
        var frozen = first.Select((x, rank) => (x.Key, rank))
            .ToDictionary(x => x.Key, x => x.rank);
        var edited = rows.Select(x => x.Id == 11
            ? x with { Price = 1000, Amount = 1000 } : x).ToArray();
        // Without frozen ranks the expensive row would jump in front.
        var sortedFresh = Assert.Single(EstimateGroups.Build(edited, EstimateGroups.Same,
            sort: EstimateViewProjection.ByAmount)).Groups;
        Assert.Equal(new[] { 11, 12 }, sortedFresh.Select(x => x.Members[0].Id));
        var stable = Assert.Single(EstimateGroups.Build(edited, EstimateGroups.Same,
            sort: EstimateViewProjection.ByAmount, frozen: frozen)).Groups;
        Assert.Equal(new[] { 12, 11 }, stable.Select(x => x.Members[0].Id));
        Assert.Equal(10, rows[1].Amount);
    }

    [Fact]
    public void MixedNullablePricesDoNotBecomeCommonAgain()
    {
        var rows = new[]
        {
            Pos(1, 11, 1, "Труба", "шт", 1, 10, 5) with { WorkPrice = 3 },
            Pos(2, 12, 1, "Труба", "шт", 2, 20, null) with { WorkPrice = null },
            Pos(3, 13, 1, "Труба", "шт", 3, 10, 5) with { WorkPrice = 3 }
        };
        var group = Assert.Single(Assert.Single(EstimateGroups.Build(rows, EstimateGroups.Same)).Groups);
        Assert.Null(group.CommonPrice);
        Assert.Null(group.CommonPurchase);
        Assert.Null(group.CommonWork);
        Assert.Equal(6m, group.Quantity);
        Assert.Equal(80m, group.Amount);
    }

    [Fact]
    public void TypeQuantityAndPriceSortRetainTieOrder()
    {
        var rows = new[]
        {
            Pos(1, 11, 1, "Клапан 1", "шт", 3, 20),
            Pos(2, 12, 1, "Труба 1", "шт", 4, 10),
            Pos(3, 13, 1, "Клапан 2", "шт", 1, 20),
            Pos(4, 14, 1, "Фильтр", "шт", 2, 50)
        };
        Assert.Equal(new[] { "клапан", "труба", "фильтр" },
            EstimateGroups.Build(rows, EstimateGroups.Type, EstimateViewProjection.ByQuantity).Select(s => s.TypeKey));
        Assert.Equal(new[] { "фильтр", "клапан", "труба" },
            EstimateGroups.Build(rows, EstimateGroups.Type, EstimateViewProjection.ByPrice).Select(s => s.TypeKey));
    }

    [Fact]
    public void MatchingGroupIds_MustCoverWholeCurrentGroup()
    {
        var rows = new (long, string, string, bool, bool)[]
        {
            (101, "Вентилятор", "шт.", true, false),
            (201, " вентилятор ", "ШТ", true, false),
            (301, "Вентилятор", "шт", false, false),
            (401, "Вентилятор", "шт", true, true),
            (501, "Клапан", "шт", true, false)
        };
        var key = EstimateGroups.Key("ВЕНТИЛЯТОР", "шт");
        Assert.True(EstimateGroups.SameMembers(rows, key, new long[] { 201, 101 }));
        Assert.False(EstimateGroups.SameMembers(rows, key, new long[] { 101 }));
        Assert.False(EstimateGroups.SameMembers(rows, key, new long[] { 201, 201 }));
        Assert.False(EstimateGroups.SameMembers(rows, key, new long[] { 201, 501 }));
    }

    [Theory]
    [InlineData("Ёмкость  100", "емкость 100", "шт.", "ШТ")]
    [InlineData("Воздуховод", "ВОЗДУХОВОД", "м²", "м2")]
    [InlineData("Решётка", "Решетка", "м³.", "м3")]
    public void Key_FollowsPythonItemKey(string a, string b, string unitA, string unitB) =>
        Assert.Equal(EstimateGroups.Key(a, unitA), EstimateGroups.Key(b, unitB));

    [Fact]
    public void M2AndYoVariants_MergeIntoOneGroup()
    {
        var rows = new[]
        {
            Sy(0, 1, "П1"),
            Pos(1, 11, 1, "Решётка", "м²", 1, 100),
            Pos(2, 12, 1, "решетка", "м2", 2, 200)
        };
        var group = Assert.Single(Assert.Single(
            EstimateGroups.Build(rows, EstimateGroups.Same)).Groups);
        Assert.Equal(new[] { 11, 12 }, group.PositionIds);
        Assert.Equal(3m, group.Quantity);
    }

    [Theory]
    [InlineData("", "прочее")]
    [InlineData(" 123 вентиль", "прочее")]
    [InlineData("Ёмкость 80", "емкость")]
    [InlineData("Air duct", "air")]
    public void TypeOf_MatchesPythonFirstWord(string input, string expected) =>
        Assert.Equal(expected, EstimateGroups.TypeOf(input));
}
