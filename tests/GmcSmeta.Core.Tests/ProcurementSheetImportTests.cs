using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementSheetImportTests
{
    private static SqlServerProcurement.SheetCandidate Candidate(long id = 11, string system = "П1",
        string supplier = "", decimal? price = null, bool linked = false) =>
        new(id, system, "Зонт кухонный", "шт", 2m, linked, supplier.Length == 0 ? null : 3L,
            supplier, "", "", "", price, null, "", "");

    private static SqlServerProcurement.SheetRow Row(long? id = null, string system = "П1",
        decimal? cost = null, string supplier = "Завод") =>
        new(id, system, "Зонт кухонный", "шт", 2m, supplier, "", "", "",
            cost, null, "", "");

    [Fact]
    public void SystemNameUnitQty_MatchesOnlyOnePosition()
    {
        var result = SqlServerProcurement.PlanSheet(new[] { Row(system: "П2") },
            new[] { Candidate(11, "П1"), Candidate(12, "П2") }, false);
        Assert.Equal("READY", result[0].State);
        Assert.Equal(12L, result[0].PositionId);
    }

    [Fact]
    public void AmbiguousRows_RequireExplicitPositionId()
    {
        var candidates = new[] { Candidate(11), Candidate(12) };
        Assert.Equal("BLOCKED", SqlServerProcurement.PlanSheet(new[] { Row() },
            candidates, false)[0].State);
        var explicitMatch = SqlServerProcurement.PlanSheet(new[] { Row(id: 12) },
            candidates, false)[0];
        Assert.Equal("READY", explicitMatch.State);
        Assert.Equal(12L, explicitMatch.PositionId);
    }

    [Fact]
    public void ExplicitId_MustAlsoMatchImmutableDescription()
    {
        var outcome = SqlServerProcurement.PlanSheet(new[] { Row(id: 11, system: "П2") },
            new[] { Candidate() }, false)[0];
        Assert.Equal("BLOCKED", outcome.State);
    }

    [Fact]
    public void DuplicateInputIds_BlockEveryDuplicateAtomically()
    {
        var outcome = SqlServerProcurement.PlanSheet(
            new[] { Row(id: 11), Row(id: 11) }, new[] { Candidate() }, false);
        Assert.All(outcome, x => Assert.Equal("BLOCKED", x.State));
    }

    [Fact]
    public void ManuallyEnteredPrice_IsNeverOverwritten()
    {
        var result = SqlServerProcurement.PlanSheet(new[] { Row(cost: 125.51m) },
            new[] { Candidate(price: 125.50m) }, false)[0];
        Assert.Equal("BLOCKED", result.State);
        Assert.Contains("цена", result.Reason.ToLowerInvariant());
    }

    [Fact]
    public void ExistingSupplier_IsNeverSilentlyReplaced()
    {
        var result = SqlServerProcurement.PlanSheet(new[] { Row(supplier: "Другой") },
            new[] { Candidate(supplier: "Цех") }, false)[0];
        Assert.Equal("BLOCKED", result.State);
    }

    [Fact]
    public void IssuedOrActLinkedPosition_FailsClosed()
    {
        var result = SqlServerProcurement.PlanSheet(new[] { Row() },
            new[] { Candidate(linked: true) }, false)[0];
        Assert.Equal("BLOCKED", result.State);
    }

    [Fact]
    public void SignedContract_FailsClosed()
    {
        Assert.Equal("BLOCKED", SqlServerProcurement.PlanSheet(new[] { Row() },
            new[] { Candidate() }, true)[0].State);
    }

    [Fact]
    public void UnknownStatusAndSubKopeckCost_AreRejected()
    {
        var row = Row() with { Stage = "unknown" };
        Assert.Equal("BLOCKED", SqlServerProcurement.PlanSheet(
            new[] { row }, new[] { Candidate() }, false)[0].State);
        Assert.Equal("BLOCKED", SqlServerProcurement.PlanSheet(
            new[] { Row(cost: 1.234m) }, new[] { Candidate() }, false)[0].State);
    }

    [Fact]
    public void ExistingText_PreservesExactSpellingAndWhitespace()
    {
        Assert.Equal("  Счёт № 7  ", SqlServerProcurement.FillSheetText("  Счёт № 7  ", "счет № 7"));
        Assert.Equal("Новый", SqlServerProcurement.FillSheetText("  ", " Новый "));
        var item = Candidate() with { OrderRef = "Счёт № 7" };
        var row = Row() with { OrderRef = "счет № 7", Note = "Дополнение" };
        Assert.Equal("READY", SqlServerProcurement.PlanSheet(new[] { row }, new[] { item }, false)[0].State);
    }

    [Fact]
    public void WorkPosition_IsNotProcurementMaterial()
    {
        var item = Candidate() with { Name = "Монтаж воздуховодов" };
        var row = Row() with { Name = item.Name };
        Assert.Equal("BLOCKED", SqlServerProcurement.PlanSheet(new[] { row }, new[] { item }, false)[0].State);
    }

    [Fact]
    public void FullyEqualValues_AreUnchanged()
    {
        var row = Row(supplier: "Цех", cost: 100.11m);
        var item = Candidate(supplier: "Цех", price: 100.11m);
        Assert.Equal("UNCHANGED", SqlServerProcurement.PlanSheet(
            new[] { row }, new[] { item }, false)[0].State);
    }

    [Fact]
    public void UnmatchedSourceRow_DoesNotCreateEstimatePosition()
    {
        var preview = SqlServerProcurement.PlanSheet(new[] { Row(system: "П9") },
            new[] { Candidate() }, false)[0];
        Assert.Equal("BLOCKED", preview.State);
        Assert.Null(preview.PositionId);
    }
}

public sealed class ProcurementBulkExtrasTests
{
    private static string? Block(bool found = true, bool locked = false, bool extra = true,
        bool children = false, bool acts = false, bool moves = false, bool shipment = false,
        bool request = false, bool price = false) =>
        SqlServerProcurement.ExtraDeletionBlocker(found, locked, extra, children, acts,
            moves, shipment, request, price);

    [Fact]
    public void CleanRootExtra_CanBeDeleted() => Assert.Null(Block());

    [Fact]
    public void ForeignAndEstimatePositions_AreProtected()
    {
        Assert.NotNull(Block(found: false));
        Assert.NotNull(Block(extra: false));
    }

    [Fact]
    public void LockedAndChildRows_AreProtected()
    {
        Assert.NotNull(Block(locked: true));
        Assert.NotNull(Block(children: true));
    }

    [Fact]
    public void ActsMovementsAndShipments_AreProtected()
    {
        Assert.NotNull(Block(acts: true));
        Assert.NotNull(Block(moves: true));
        Assert.NotNull(Block(shipment: true));
    }

    [Fact]
    public void OrdersAndManualPrices_AreProtected()
    {
        Assert.NotNull(Block(request: true));
        Assert.NotNull(Block(price: true));
    }

    [Fact]
    public void InvalidBatch_FailsBeforeConnectingToProduction()
    {
        var result = SqlServerProcurement.DeleteExtras(
            "Server=invalid;Database=Production", 7, Array.Empty<long>(), "rev");
        Assert.False(result.Saved);
        Assert.Contains("1–2500", result.Error!);
    }
}
