using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

// Все записи — только в собственной базе в памяти, без доступа к файлам пользователя.
public sealed class DocumentsTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly long _object;
    private readonly long _contract;
    private readonly long _system;

    public DocumentsTests()
    {
        _conn.Open();
        Database.Exec(_conn, "PRAGMA foreign_keys=ON");
        Database.EnsureSchema(_conn);
        _object = Documents.CreateObject(_conn, "  Тестовый объект  ");
        _contract = Documents.CreateContract(_conn, _object);
        _system = Documents.AddSystem(_conn, _contract, "Вентиляция");
    }

    [Fact]
    public void CreateContract_InheritsObjectFormAndPythonDefaults()
    {
        RowWriter.Update(_conn, "objects", _object, new Dictionary<string, object?> { ["form"] = "r4" });
        var id = Documents.CreateContract(_conn, _object);
        Assert.Equal("r4", Text($"SELECT form FROM contracts WHERE id={id}"));
        Assert.Equal("volumes", Text($"SELECT kp_phase FROM contracts WHERE id={id}"));
        Assert.Equal("2", Text($"SELECT \"order\" FROM contracts WHERE id={id}"));
        Assert.Equal(22m, decimal.Parse(Text($"SELECT vat_rate FROM contracts WHERE id={id}"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Тестовый объект", Text($"SELECT name FROM objects WHERE id={_object}"));
    }

    [Fact]
    public void Subsections_StayWithRootAndUseOnlyOneLevel()
    {
        var other = Documents.AddSystem(_conn, _contract, "Другая система");
        var sub = Documents.AddSystem(_conn, _contract, "Подраздел", _system);
        var second = Documents.AddSystem(_conn, _contract, "Второй подраздел", sub);
        Assert.Equal(new[] { _system, sub, second, other }, Ids("systems"));
        Assert.Equal(_system.ToString(), Text($"SELECT parent_id FROM systems WHERE id={second}"));
        Assert.Equal(new long[] { 0, 1, 2, 3 }, Orders("systems"));
    }

    [Fact]
    public void InsertAfterComponent_GoesAfterWholeGroupLikePython()
    {
        var parent = Documents.AddPosition(_conn, _system, "Установка", qty: 2, price: 100);
        var first = Documents.AddPosition(_conn, _system, "Комплектующая 1", price: 999, parentPositionId: parent);
        var second = Documents.AddPosition(_conn, _system, "Комплектующая 2", parentPositionId: parent);
        var last = Documents.AddPosition(_conn, _system, "Последняя");
        var inserted = Documents.AddPosition(_conn, _system, "Вставленная", afterId: first);
        Assert.Equal(new[] { parent, first, second, inserted, last }, Ids("positions"));
        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, Orders("positions"));
        Assert.Equal(0m, decimal.Parse(Text($"SELECT price FROM positions WHERE id={first}"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(200m, Totals.ReadOne(_conn, (int)_contract)!.Materials);
    }

    [Fact]
    public void NewBlankRow_CanBeInsertedAndEditedInTheRightSection()
    {
        var main = Documents.AddPosition(_conn, _system, "Вентилятор", price: 100);
        var sub = Documents.AddPosition(_conn, _system, "Вставка", parentPositionId: main);
        var after = Documents.AddPosition(_conn, _system, "Последняя");

        var inserted = Documents.AddPosition(_conn, _system, "", afterId: sub);
        var component = Documents.AddPosition(_conn, _system, "", parentPositionId: main);
        Assert.Equal(new[] { main, sub, component, inserted, after }, Ids("positions"));
        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, Orders("positions"));
        Assert.Equal("", Text($"SELECT name FROM positions WHERE id={inserted}"));
        Assert.Equal("", Text($"SELECT name FROM positions WHERE id={component}"));
        Assert.Equal("", Text($"SELECT IFNULL(catalog_item_id,'') FROM positions WHERE id={inserted}"));

        var document = EstimateReader.Load(_conn, (int)_contract);
        var systemRow = document.Rows.Single(r => r.IsSystem && r.Name == "Вентиляция");
        Assert.Equal((int)_system, systemRow.SystemId);
        var insertedRow = document.Rows.Single(r => r.Id == inserted && !r.IsSystem);
        var componentRow = document.Rows.Single(r => r.Id == component && !r.IsSystem);
        Assert.Equal((int)_system, insertedRow.SystemId);
        Assert.Null(insertedRow.ParentPositionId);
        Assert.Equal((int)_system, componentRow.SystemId);
        Assert.Equal((int)main, componentRow.ParentPositionId);
    }

    [Fact]
    public void ForeignParentsAndAnchors_AreRejectedWithoutWrites()
    {
        var otherContract = Documents.CreateContract(_conn, _object);
        var otherSystem = Documents.AddSystem(_conn, otherContract, "Чужая система");
        var foreignPosition = Documents.AddPosition(_conn, otherSystem, "Чужая позиция");
        var before = RowWriter.Count(_conn, "change_log");
        Assert.Throws<ArgumentException>(() => Documents.AddSystem(_conn, _contract, "Ошибка", otherSystem));
        Assert.Throws<ArgumentException>(() => Documents.AddPosition(_conn, _system, "Ошибка", parentPositionId: foreignPosition));
        Assert.Throws<ArgumentException>(() => Documents.AddPosition(_conn, _system, "Ошибка", afterId: foreignPosition));
        Assert.Equal(2, RowWriter.Count(_conn, "systems"));
        Assert.Equal(1, RowWriter.Count(_conn, "positions"));
        Assert.Equal(before, RowWriter.Count(_conn, "change_log"));
    }

    [Fact]
    public void NestedComponents_AreRejected()
    {
        var parent = Documents.AddPosition(_conn, _system, "Основная");
        var child = Documents.AddPosition(_conn, _system, "Комплектующая", parentPositionId: parent);
        Assert.Throws<ArgumentException>(() => Documents.AddPosition(_conn, _system, "Ошибка", parentPositionId: child));
        Assert.Equal(2, RowWriter.Count(_conn, "positions"));
    }

    [Fact]
    public void SignedDocument_RejectsAllEstimateWritesAndKeepsHistory()
    {
        var id = Documents.AddPosition(_conn, _system, "Исходная", qty: 2, price: 100);
        RowWriter.Update(_conn, "contracts", _contract, new Dictionary<string, object?> { ["locked"] = true });
        var before = RowWriter.Count(_conn, "change_log");
        Assert.Throws<InvalidOperationException>(() => Documents.AddSystem(_conn, _contract, "Запрет"));
        Assert.Throws<InvalidOperationException>(() => Documents.AddPosition(_conn, _system, "Запрет"));
        Assert.Throws<InvalidOperationException>(() => Documents.UpdatePosition(_conn, id, qty: 99));
        Assert.Throws<InvalidOperationException>(() => Documents.DeletePosition(_conn, id));
        Assert.Equal(before, RowWriter.Count(_conn, "change_log"));
        Assert.Equal(1, RowWriter.Count(_conn, "positions"));
        Assert.Equal(200m, Totals.ReadOne(_conn, (int)_contract)!.Materials);
    }

    [Fact]
    public void HistoryFailure_RollsBackDataAndOrder()
    {
        var parent = Documents.AddSystem(_conn, _contract, "Последняя");
        var before = Ids("systems");
        Database.Exec(_conn, "CREATE TRIGGER reject_log BEFORE INSERT ON change_log BEGIN SELECT RAISE(ABORT, 'test log failure'); END");
        Assert.Throws<SqliteException>(() => Documents.AddSystem(_conn, _contract, "Не сохранить", _system));
        Assert.Equal(before, Ids("systems"));
        Assert.Equal(new long[] { 0, 1 }, Orders("systems"));
        Assert.Throws<SqliteException>(() => Documents.CreateObject(_conn, "Не сохранить"));
        Assert.Throws<SqliteException>(() => Documents.CreateContract(_conn, _object));
        Assert.Equal(1, RowWriter.Count(_conn, "objects"));
        Assert.Equal(1, RowWriter.Count(_conn, "contracts"));
    }

    [Fact]
    public void Delete_RemovesComponentsRenumbersAndKeepsOwnerInHistory()
    {
        var parent = Documents.AddPosition(_conn, _system, "Основная");
        Documents.AddPosition(_conn, _system, "Комплектующая", parentPositionId: parent);
        var last = Documents.AddPosition(_conn, _system, "Оставшаяся");
        Assert.Equal(1, Documents.DeletePosition(_conn, parent));
        Assert.Equal(new[] { last }, Ids("positions"));
        Assert.Equal(new long[] { 0 }, Orders("positions"));
        Assert.Equal(_contract.ToString(), Text("SELECT contract_id FROM change_log WHERE action='delete'"));
        Assert.Equal(_object.ToString(), Text("SELECT object_id FROM change_log WHERE action='delete'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delete_RefusesRowsUsedInAnyActIncludingComponents(bool component)
    {
        var parent = Documents.AddPosition(_conn, _system, "Основная");
        var child = Documents.AddPosition(_conn, _system, "Комплектующая", parentPositionId: parent);
        var act = RowWriter.Insert(_conn, "acts", new Dictionary<string, object?> { ["contract_id"] = _contract });
        RowWriter.Insert(_conn, "act_lines", new Dictionary<string, object?>
        { ["act_id"] = act, ["position_id"] = component ? child : parent });
        var before = RowWriter.Count(_conn, "change_log");
        Assert.Throws<InvalidOperationException>(() => Documents.DeletePosition(_conn, parent));
        Assert.Equal(2, RowWriter.Count(_conn, "positions"));
        Assert.Equal(1, RowWriter.Count(_conn, "act_lines"));
        Assert.Equal(before, RowWriter.Count(_conn, "change_log"));
    }

    [Fact]
    public void FailedUpdateOrDeleteHistory_RollsBackChanges()
    {
        var id = Documents.AddPosition(_conn, _system, "Исходная", qty: 2, price: 10);
        Database.Exec(_conn, "CREATE TRIGGER reject_log BEFORE INSERT ON change_log BEGIN SELECT RAISE(ABORT, 'test log failure'); END");
        Assert.Throws<SqliteException>(() => Documents.UpdatePosition(_conn, id, qty: 99));
        Assert.Throws<SqliteException>(() => Documents.DeletePosition(_conn, id));
        Assert.Equal(20m, Totals.ReadOne(_conn, (int)_contract)!.Materials);
    }

    private string Text(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private long[] Ids(string table) => ReadNumbers($"SELECT id FROM {table} ORDER BY \"order\", id");
    private long[] Orders(string table) => ReadNumbers($"SELECT \"order\" FROM {table} ORDER BY \"order\", id");
    private long[] ReadNumbers(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var values = new List<long>();
        while (reader.Read()) values.Add(reader.GetInt64(0));
        return values.ToArray();
    }

    public void Dispose() => _conn.Dispose();
}
