using System.Data;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

/// <summary>Reader mapping uses the exact SQL Server SELECT column order.</summary>
public sealed class ProcurementReadAndSupplierTests
{
    private static SqlServerProcurement.BuyRow Sample(string? supplier, bool direct, string note,
        string source = "buy", long? parentSystemId = null, DateTime? lastIssueDate = null)
    {
        var table = new DataTable();
        var types = new[] {
            typeof(long), typeof(string), typeof(string), typeof(string),
            typeof(decimal), typeof(decimal), typeof(bool), typeof(decimal),
            typeof(decimal), typeof(string), typeof(string), typeof(string),
            typeof(decimal), typeof(DateTime), typeof(string), typeof(string),
            typeof(bool), typeof(string), typeof(string), typeof(long), typeof(long), typeof(DateTime)
        };
        for (var i = 0; i < types.Length; i++)
            table.Columns.Add("c" + i, types[i]);

        table.Rows.Add(
            42L, "Вентиляция", "Воздуховод", "шт",
            10m, 750m, false, 7m, 2m,
            "ordered", "Заявка №5", "Счет №10", 450m,
            new DateTime(2026, 11, 28), "Замена", note, direct,
            supplier is null ? DBNull.Value : supplier,
            source, 91L, parentSystemId is null ? DBNull.Value : parentSystemId.Value,
            lastIssueDate is null ? DBNull.Value : lastIssueDate.Value);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        return SqlServerProcurement.MapBuyRow(reader, new DateTime(2026, 11, 21));
    }

    [Fact]
    public void BuyRows_MapsExtendedProcurementFieldsWithoutLosingOriginalName()
    {
        var values = new object[] { 42L, "В1", "Имя в смете", "шт", 10m, 750m, false, 7m, 2m,
            "ordered", "З5", "С7", 450m, new DateTime(2026, 11, 28), "замена", "примечание", true,
            "Поставщик", "buy", 91L, DBNull.Value, new DateTime(2026, 11, 20), "Имя для закупки",
            new DateTime(2026, 11, 21), "Ответственный", 43L, "Родительская позиция" };
        var table = new DataTable();
        for (var i = 0; i < values.Length; i++)
            table.Columns.Add("c" + i, values[i] is DBNull ? typeof(long) : values[i].GetType());
        table.Rows.Add(values);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var row = SqlServerProcurement.MapBuyRow(reader, new DateTime(2026, 11, 24));
        Assert.Equal("Имя в смете", row.Name);
        Assert.Equal("Имя для закупки", row.PurchaseName);
        Assert.Equal(new DateTime(2026, 11, 21), row.PaidDate);
        Assert.Equal("Ответственный", row.Responsible);
        Assert.Equal(43L, row.ParentPositionId);
    }

    [Fact]
    public void BuyRowsQuery_TargetsBothMovementAggregationAndPositionProjection()
    {
        var sql = SqlServerProcurement.BuyRowsQuery(2);
        Assert.Contains("WHERE ms.contract_id=@id AND m.position_id IN (@pos0,@pos1)", sql);
        Assert.Contains("WHERE sy.contract_id=@id AND p.id IN (@pos0,@pos1)", sql);
        Assert.DoesNotContain("@pos2", sql);
        Assert.Equal(SqlServerProcurement.BuyRowsQuery(), SqlServerProcurement.BuyRowsQuery(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlServerProcurement.BuyRowsQuery(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SqlServerProcurement.BuyRowsQuery(SqlServerProcurement.MaxIncrementalBuyRows + 1));
    }

    [Fact]
    public void BuyRowsQuery_GroupsMovementTotalsOnceWithinContractScope()
    {
        var sql = SqlServerProcurement.BuyRowsQuery();

        // Only positions from this contract may contribute to quantities.
        Assert.Contains("WHERE sy.contract_id=@id", sql);
        Assert.Contains("WHERE ms.contract_id=@id GROUP BY m.position_id", sql);
        Assert.Contains("LEFT JOIN (", sql);

        // Four movement kinds are summed in one grouped pass, not five
        // correlated SELECTs executed for every procurement position.
        Assert.DoesNotContain("(SELECT SUM(", sql);
        Assert.DoesNotContain("(SELECT MAX(", sql);
        Assert.Contains("WHEN m.kind=N'in' THEN m.qty", sql);
        Assert.Contains("WHEN m.kind=N'back_supplier' THEN m.qty", sql);
        Assert.Contains("WHEN m.kind=N'out' THEN m.qty", sql);
        Assert.Contains("WHEN m.kind=N'back' THEN m.qty", sql);
        Assert.Contains("ISNULL(mv.qty_in,0) - ISNULL(mv.qty_back_supplier,0) AS received", sql);
        Assert.Contains("ISNULL(mv.qty_out,0) - ISNULL(mv.qty_back,0) AS issued", sql);
        Assert.Contains("MAX(CASE WHEN m.kind=N'out' THEN m.[date] END) AS last_issue_date", sql);
        Assert.Contains("ORDER BY sy.[order], sy.id, p.[order], p.id", sql);
    }

    [Fact]
    public void BuyRows_MapsSupplierNoteAndDirectFlagFromDistinctColumns()
    {
        var row = Sample("Цех", true, "Срочный заказ");
        Assert.Equal(42, row.PositionId);
        Assert.Equal("Цех", row.Supplier);
        Assert.Equal("Срочный заказ", row.Note);
        Assert.True(row.DirectToSite);
        Assert.Equal(450m, row.CostPrice);
        Assert.Equal(new DateTime(2026, 11, 28), row.DueDate);
        Assert.Equal("part_issued", row.Status); // Movement quantities override ordered stage.
        Assert.Equal(7m, row.Received);
        Assert.Equal(2m, row.Issued);
    }

    [Fact]
    public void BuyRows_HandlesNullSupplierWithoutConvertingBoolToString()
    {
        var row = Sample(null, false, "Обычная строка");
        Assert.Null(row.Supplier);
        Assert.False(row.DirectToSite);
        Assert.Equal("Обычная строка", row.Note);
    }

    [Fact]
    public void BuyRows_ProjectsWorkshopSourceAndSystemHierarchyEvenAfterReceipt()
    {
        var row = Sample("Цех", false, "", source: "shop", parentSystemId: 17L);
        Assert.Equal("shop", row.Source);
        Assert.Equal(91L, row.SystemId);
        Assert.Equal(17L, row.ParentSystemId);
    }

    [Fact]
    public void BuyRows_ProjectsLastIssueDateForTemporarySort()
    {
        var date = new DateTime(2026, 11, 20);
        var row = Sample("Цех", false, "", lastIssueDate: date);
        Assert.Equal(date, row.LastIssueDate);
        Assert.Null(Sample(null, false, "").LastIssueDate);
    }

    [Fact]
    public void BuyRows_FallsBackToBuyAndNoParentForOrdinaryPositions()
    {
        var row = Sample(null, false, "");
        Assert.Equal("buy", row.Source);
        Assert.Equal(91L, row.SystemId);
        Assert.Null(row.ParentSystemId);
    }

    [Fact]
    public void SupplierId_RemainsWhenOtherFieldEdited_AndCanBeExplicitlyCleared()
    {
        Assert.Equal(15L, SqlServerProcurement.SupplierIdForUpdate(15L, null, null));
        Assert.Null(SqlServerProcurement.SupplierIdForUpdate(15L, "", null));
        Assert.Equal(27L, SqlServerProcurement.SupplierIdForUpdate(15L, "Новый", 27L));
    }
    [Fact]
    public void ClearingCostAndDueDate_IsExplicitAndDoesNotAffectOtherEdits()
    {
        var due = new DateTime(2026, 11, 28);
        Assert.Equal(150m, SqlServerProcurement.CostPriceForUpdate(150m, null, false));
        Assert.Equal(180m, SqlServerProcurement.CostPriceForUpdate(150m, 180m, false));
        Assert.Null(SqlServerProcurement.CostPriceForUpdate(150m, null, true));
        Assert.Equal(due, SqlServerProcurement.DueDateForUpdate(due, null, false));
        Assert.Null(SqlServerProcurement.DueDateForUpdate(due, null, true));
    }

    [Theory]
    [InlineData("Оплачено", "paid")]
    [InlineData("Счёт", "invoice")]
    [InlineData("Счет", "invoice")]
    [InlineData("По факту / монтажники", "fact")]
    [InlineData("В изготовлении", "shop_work")]
    [InlineData("ordered", "ordered")]
    [InlineData("Не заказываем", "skip")]
    public void ImportPlan_ConvertsRussianStagesToStableSqlCodes(string text, string code) =>
        Assert.Equal(code, SqlServerProcurement.NormalizeStage(text));

    [Fact]
    public void ImportPlan_RejectsUnknownStageRatherThanWritingInvisibleStatus()
    {
        Assert.Throws<ArgumentException>(() => SqlServerProcurement.NormalizeStage("????"));
        Assert.Equal("", SqlServerProcurement.NormalizeStage(null));
    }

    private static SqlServerProcurement.MovementHistoryRow Movement(
        string kind, long? shipmentId = null, long positionId = 42)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(long));
        table.Columns.Add("position_id", typeof(long));
        table.Columns.Add("kind", typeof(string));
        table.Columns.Add("qty", typeof(decimal));
        table.Columns.Add("date", typeof(DateTime));
        table.Columns.Add("doc", typeof(string));
        table.Columns.Add("note", typeof(string));
        table.Columns.Add("user", typeof(string));
        table.Columns.Add("shipment_id", typeof(long));
        table.Rows.Add(17L, positionId, kind, 4.5m,
            new DateTime(2026, 11, 20), "Накладная №1", "Сверено", "Инженер",
            shipmentId is null ? DBNull.Value : shipmentId.Value);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        return SqlServerProcurement.MapMovement(reader);
    }

    [Theory]
    [InlineData("in", "Пришло на склад")]
    [InlineData("out", "Выдано на объект")]
    [InlineData("back", "Возврат с объекта")]
    [InlineData("back_supplier", "Возврат поставщику")]
    public void MovementHistory_ProjectsAllPythonKindsAndMetadata(string kind, string label)
    {
        var move = Movement(kind);
        Assert.Equal(17L, move.Id);
        Assert.Equal(42L, move.PositionId);
        Assert.Equal(kind, move.Kind);
        Assert.Equal(label, move.KindLabel);
        Assert.Equal(4.5m, move.Qty);
        Assert.Equal(new DateTime(2026, 11, 20), move.Date);
        Assert.Equal("Накладная №1", move.Document);
        Assert.Equal("Сверено", move.Note);
        Assert.Equal("Инженер", move.User);
        Assert.Null(move.ShipmentId);
    }

    [Fact]
    public void MovementDeletion_AllowsIndependentMovementButNotShipmentIssue()
    {
        var independent = Movement("in");
        Assert.Null(SqlServerProcurement.MovementDeletionError(independent, 42L));
        var shipment = Movement("out", shipmentId: 73L);
        Assert.Contains("ведомости", SqlServerProcurement.MovementDeletionError(shipment, 42L));
        Assert.Equal(73L, shipment.ShipmentId);
    }

    [Fact]
    public void MovementDeletion_RejectsOtherPositionAndMissingMovement()
    {
        Assert.NotNull(SqlServerProcurement.MovementDeletionError(Movement("in"), 111L));
        Assert.NotNull(SqlServerProcurement.MovementDeletionError(null, 42L));
    }

    [Fact]
    public void MovementHistory_KeepsUnknownKindVisibleInsteadOfDiscardingRecord()
    {
        Assert.Equal("new_kind", SqlServerProcurement.MovementKindLabel("new_kind"));
    }


    [Fact]
    public void SetProc_RejectsEmptyOrInvalidTargetsBeforeOpeningConnection()
    {
        const string pilot = "Server=invalid;Database=GmcSmetaPilot_ProcValidation;Integrated Security=true;Connect Timeout=1";
        foreach (var ids in new[] { Array.Empty<long>(), new long[] { 0 }, new long[] { 1, -1 } })
        {
            var saved = SqlServerProcurement.SetProc(pilot, 1, ids, "Не создавать поставщика",
                null, null, null, null, null, null, null, null, "rev");
            Assert.False(saved.Saved);
            Assert.Equal("rev", saved.Revision);
        }
    }
}
