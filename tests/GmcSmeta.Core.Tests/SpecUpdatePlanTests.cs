using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SpecUpdatePlanTests
{
    private static SpecExistingPosition P(long id, long system, string name, decimal qty,
        decimal price = 100, bool fromSpec = true, bool linked = false) =>
        new(id, system, null, name, "шт.", qty, price, 50, "ручная цена",
            fromSpec, name, linked, (int)id, false);

    private static SpecIncomingPosition N(string name, decimal qty, int order = 1) =>
        new(name, "шт", qty, "", order);

    [Fact]
    public void DuplicateSpecRowsAggregateButNeverModifyManualRows()
    {
        var prior = new[] { new SpecExistingSystem(1, " П1 ", 0, null) };
        var positions = new[] { P(10, 1, "Клапан", 2), P(11, 1, "Клапан", 1),
            P(12, 1, "Вручную", 5, fromSpec: false) };
        var incoming = new[]
        {
            new SpecIncomingSystem("п1", new[] { N("Клапан", 6), N(" Клапан ", 2), N("Новый", 3) })
        };
        var diff = SpecUpdatePlan.Compare(prior, positions, incoming);
        var qty = Assert.Single(diff.Changes.Where(x => x.Kind == "qty"));
        Assert.Equal(3, qty.OldQuantity);
        Assert.Equal(8, qty.NewQuantity);
        Assert.Equal(100, qty.Price);
        Assert.Equal(new long[] { 10, 11 }, qty.MatchingIds);
        Assert.Single(diff.Changes.Where(x => x.Kind == "add"));
        Assert.DoesNotContain(diff.Changes, x => x.Name == "Вручную");
    }

    [Fact]
    public void RenameAndMoveAreDistinctFromDeletingAndAdding()
    {
        var systems = new[]
        {
            new SpecExistingSystem(1, "Система старая", 0, null),
            new SpecExistingSystem(2, "Система Б", 1, null)
        };
        var positions = new[] { P(1, 1, "A", 1), P(2, 1, "Б", 2),
            P(3, 2, "В", 3) };
        var incoming = new[]
        {
            new SpecIncomingSystem("Система новая", new[] { N("A", 1), N("Б", 2) }),
            new SpecIncomingSystem("Система Б", new[] { N("В", 3) })
        };
        var diff = SpecUpdatePlan.Compare(systems, positions, incoming);
        Assert.Single(diff.Changes);
        var rename = diff.Changes[0];
        Assert.Equal("sys_rename", rename.Kind);
        Assert.Equal("Система старая", rename.OldSystem);
        Assert.Equal("Система новая", rename.System);
        Assert.Equal(3, diff.Unchanged.Values.Sum());
    }

    [Fact]
    public void MovingReferencedItemWarnsRatherThanDroppingItsIdentifier()
    {
        var systems = new[]
        {
            new SpecExistingSystem(1, "П1", 0, null),
            new SpecExistingSystem(2, "В1", 1, null)
        };
        var diff = SpecUpdatePlan.Compare(systems, new[] { P(8, 1, "Клапан", 2, linked: true) },
            new[] { new SpecIncomingSystem("П1", Array.Empty<SpecIncomingPosition>()),
                new SpecIncomingSystem("В1", new[] { N("Клапан", 4) }) });
        var leave = Assert.Single(diff.Changes.Where(x => x.Kind == "move_out"));
        var arrive = Assert.Single(diff.Changes.Where(x => x.Kind == "move_in"));
        Assert.Equal(8, leave.PositionId);
        Assert.Contains("удаление запрещено", leave.Warning);
        Assert.Equal(100, arrive.Price);
        Assert.Equal("В1", leave.OldSystem);
        Assert.Equal("П1", arrive.OldSystem);
    }

    [Fact]
    public void NameNormalizationMatchesPythonIncludingYoNbspAndSuperscripts()
    {
        Assert.Equal(SpecUpdatePlan.ItemKey("Воздуховод\u00a0Ёж", "м²."),
            SpecUpdatePlan.ItemKey("воздуховод ЕЖ", "м2"));
    }

    [Fact]
    public void TenThousandScheduleRowsGenerateOnlyChangedRecords()
    {
        const int count = 10_000;
        var systems = new[] { new SpecExistingSystem(1, "П1", 0, null) };
        var old = Enumerable.Range(1, count).Select(i => P(i, 1, "Позиция " + i, i)).ToArray();
        var latest = Enumerable.Range(1, count)
            .Select(i => N("Позиция " + i, i == 9_999 ? i + 2 : i, i)).ToArray();
        var diff = SpecUpdatePlan.Compare(systems, old,
            new[] { new SpecIncomingSystem("П1", latest) });
        var changed = Assert.Single(diff.Changes);
        Assert.Equal("qty", changed.Kind);
        Assert.Equal(9_999, changed.PositionId);
        Assert.Equal(count - 1, diff.Unchanged["П1"]);
    }

    [Fact]
    public void DuplicateSystemNamesAreBlockedNotSilentlyMerged()
    {
        Assert.Throws<InvalidOperationException>(() => SpecUpdatePlan.Compare(
            new[] { new SpecExistingSystem(1, "П1", 0, null),
                new SpecExistingSystem(2, "п1", 1, null) },
            Array.Empty<SpecExistingPosition>(),
            new[] { new SpecIncomingSystem("П1", Array.Empty<SpecIncomingPosition>()) }));
    }
}
