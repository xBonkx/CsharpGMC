using System.Text.Json;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class ExcelExistingUndoSnapshotTests
{
    private static readonly string Hash = new('A', 64);
    private static readonly string PlanId = new('B', 64);

    private static ExcelExistingQuantityUndoRow Quantity(long id) =>
        new(id, 12, null, "Клапан", "шт", 2m, 3m, 500m, 400m, 100m,
            "Ручное примечание", "", 42,
            "{\"src\":\"Клапан\",\"excel_imported_qty\":\"2\"}");

    private static ExcelExistingUndoSnapshot Snapshot(long[]? added = null,
        ExcelExistingQuantityUndoRow[]? quantities = null) =>
        new(1, 123, Hash, PlanId, "before-rev", "after-rev",
            quantities ?? new[] { Quantity(1) }, added ?? Array.Empty<long>());

    [Fact]
    public void SnapshotRoundTripRetainsPriceFormulaSourceAndRevision()
    {
        var original = Snapshot();
        var parsed = ExcelExistingUndoSnapshot.Parse(JsonSerializer.Serialize(original), Hash, 123);
        Assert.True(parsed.CanUndoQuantities);
        Assert.Equal("before-rev", parsed.BeforeRevision);
        Assert.Equal("after-rev", parsed.AfterRevision);
        var change = Assert.Single(parsed.QuantityEdits);
        Assert.Equal(2m, change.BeforeQuantity);
        Assert.Equal(3m, change.AppliedQuantity);
        Assert.Equal(400m, change.PurchasePrice);
        Assert.Equal(100m, change.WorkPrice);
        Assert.Equal(42, change.SourceRow);
        Assert.Contains("excel_imported_qty", change.ExtraJson);
    }

    [Fact]
    public void AddedPositionsForbidImplicitDeleteAndPartialUndo()
    {
        var withAdd = Snapshot(added: new long[] { 99 });
        withAdd.Validate(Hash, 123);
        Assert.False(withAdd.CanUndoQuantities);
        Assert.False(Snapshot(quantities: Array.Empty<ExcelExistingQuantityUndoRow>()).CanUndoQuantities);
    }

    [Fact]
    public void DuplicateIdentityAndWrongContractOrVersionFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Snapshot(quantities: new[] { Quantity(1), Quantity(1) }).Validate(Hash, 123));
        Assert.Throws<InvalidOperationException>(() =>
            Snapshot(added: new long[] { 9, 9 }).Validate(Hash, 123));
        Assert.Throws<InvalidOperationException>(() => Snapshot().Validate(Hash, 999));
        Assert.Throws<InvalidOperationException>(() =>
            (Snapshot() with { Version = 2 }).Validate(Hash, 123));
        Assert.Throws<InvalidOperationException>(() =>
            ExcelExistingUndoSnapshot.Parse("{broken", Hash, 123));
    }

    [Theory]
    [InlineData(2_500)]
    [InlineData(10_000)]
    public void SyntheticLargeQuantitySnapshotRetainsEveryRow(int count)
    {
        var originals = Enumerable.Range(1, count).Select(id => Quantity(id)).ToArray();
        var snapshot = Snapshot(quantities: originals);
        var restored = ExcelExistingUndoSnapshot.Parse(JsonSerializer.Serialize(snapshot), Hash, 123);
        Assert.True(restored.CanUndoQuantities);
        Assert.Equal(count, restored.QuantityEdits.Count);
        Assert.Equal(1, restored.QuantityEdits[0].Id);
        Assert.Equal(count, restored.QuantityEdits[^1].Id);
    }

    [Theory]
    [InlineData("Server=localhost;Database=master;Integrated Security=true")]
    [InlineData("Server=localhost;Database=Production;Integrated Security=true")]
    public void UndoReadbackAndMutationRefuseNonPilotBeforeConnecting(string connectionString)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerExcelExistingUpdate.ReadUndoSnapshot(connectionString, 123, Hash));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerExcelExistingUpdate.WasUndone(connectionString, 123, Hash));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerExcelExistingUpdate.UndoQuantities(connectionString, 123, Hash, "after-rev"));
    }
}
