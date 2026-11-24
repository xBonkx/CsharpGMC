using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

public sealed class DocumentsEditorTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly long _object;
    private readonly long _contract;
    private readonly long _system;

    public DocumentsEditorTests()
    {
        _conn.Open();
        Database.Exec(_conn, "PRAGMA foreign_keys=ON");
        Database.EnsureSchema(_conn);
        _object = Documents.CreateObject(_conn, "Объект");
        _contract = Documents.CreateContract(_conn, _object, "kp", "1", "01.01.2026", "Док");
        _system = Documents.AddSystem(_conn, _contract, "Система");
    }

    [Fact]
    public void MovePosition_UpAndDown()
    {
        var a = Documents.AddPosition(_conn, _system, "A", qty: 1, price: 10);
        var b = Documents.AddPosition(_conn, _system, "B", qty: 1, price: 10);
        var c = Documents.AddPosition(_conn, _system, "C", qty: 1, price: 10);

        Assert.True(Documents.MovePosition(_conn, b, -1));   // b before a
        var order = Positions();
        Assert.Equal(new[] { "B", "A", "C" }, order);

        Assert.True(Documents.MovePosition(_conn, b, +1));   // back
        Assert.Equal(new[] { "A", "B", "C" }, Positions());
    }

    [Fact]
    public void MovePosition_BoundaryReturnsFalse()
    {
        var a = Documents.AddPosition(_conn, _system, "A", qty: 1, price: 10);
        Assert.False(Documents.MovePosition(_conn, a, -1));
    }

    [Fact]
    public void MakeSubposition_MakesAndUnmakes()
    {
        var parent = Documents.AddPosition(_conn, _system, "Parent", qty: 1, price: 10);
        var child = Documents.AddPosition(_conn, _system, "Child", qty: 1, price: 10);

        Assert.True(Documents.MakeSubposition(_conn, child, true));
        // child becomes subposition of the row above
        var parentId = Convert.ToInt64(Text($"SELECT parent_id FROM positions WHERE id={child}"));
        Assert.Equal(parent, parentId);

        Assert.True(Documents.MakeSubposition(_conn, child, false));
        Assert.True(string.IsNullOrEmpty(Text($"SELECT parent_id FROM positions WHERE id={child}")));
    }

    [Fact]
    public void MovePositionsToSystem_MovesTopLevelRows()
    {
        var system2 = Documents.AddSystem(_conn, _contract, "Система 2");
        var p1 = Documents.AddPosition(_conn, _system, "P1", qty: 1, price: 10);
        Documents.AddPosition(_conn, _system, "P2", qty: 1, price: 10);

        var moved = Documents.MovePositionsToSystem(_conn, new[] { p1 }, system2);
        Assert.Equal(1, moved);
        Assert.Equal(system2, Convert.ToInt64(Text($"SELECT system_id FROM positions WHERE id={p1}")));
    }

    [Fact]
    public void RenameSystem_WritesHistoryOnlyWhenChanged()
    {
        var before = CountHistory();
        Assert.True(Documents.RenameSystem(_conn, _system, "Новое имя"));
        Assert.Equal("Новое имя", Text($"SELECT name FROM systems WHERE id={_system}"));
        Assert.Equal(before + 1, CountHistory());

        // same name — nothing
        var before2 = CountHistory();
        Assert.False(Documents.RenameSystem(_conn, _system, "Новое имя"));
        Assert.Equal(before2, CountHistory());

        // empty — nothing
        Assert.False(Documents.RenameSystem(_conn, _system, "   "));
    }

    [Fact]
    public void SetSystemSmr_WritesWhenChanged()
    {
        Assert.True(Documents.SetSystemSmr(_conn, _system, 100m));
        Assert.Equal(100d, double.Parse(Text($"SELECT smr_amount FROM systems WHERE id={_system}"),
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(Documents.SetSystemSmr(_conn, _system, 100m));
        Assert.True(Documents.SetSystemSmr(_conn, _system, null));
        Assert.Equal("", Text($"SELECT smr_amount FROM systems WHERE id={_system}"));
    }

    [Fact]
    public void MoveSystem_UpAndDown()
    {
        var s2 = Documents.AddSystem(_conn, _contract, "С2");
        // initial order: _system, s2
        Assert.True(Documents.MoveSystem(_conn, s2, -1));
        var names = Systems();
        Assert.Equal(new[] { "С2", "Система" }, names);
    }

    [Fact]
    public void DeleteSystem_RemovesSystemAndPositions()
    {
        var p = Documents.AddPosition(_conn, _system, "P", qty: 1, price: 10);
        var deleted = Documents.DeleteSystem(_conn, _system);
        Assert.True(deleted > 0);
        Assert.Equal(0, Convert.ToInt32(Text($"SELECT COUNT(*) FROM systems WHERE id={_system}")));
        Assert.Equal(0, Convert.ToInt32(Text($"SELECT COUNT(*) FROM positions WHERE id={p}")));
    }

    [Fact]
    public void PositionEditor_UpdatePosition_ChangesAndHistory()
    {
        var p = Documents.AddPosition(_conn, _system, "Pos", "шт", 2m, 100m);
        var before = RowWriter.Count(_conn, "change_log");
        var changed = PositionEditor.UpdatePosition(_conn, p,
            new PositionChange("qty", 3m), new PositionChange("price", 110m));
        Assert.True(changed);
        Assert.Equal(3m, Convert.ToDecimal(Text($"SELECT qty FROM positions WHERE id={p}")));
        Assert.Equal(110m, Convert.ToDecimal(Text($"SELECT price FROM positions WHERE id={p}")));
        Assert.Equal(before + 1, RowWriter.Count(_conn, "change_log"));
    }

    [Fact]
    public void PositionEditor_UpdatePosition_LockedThrows()
    {
        var p = Documents.AddPosition(_conn, _system, "Pos", "шт", 2m, 100m);
        RowWriter.Update(_conn, "contracts", _contract, new Dictionary<string, object?> { ["locked"] = true });
        Assert.Throws<InvalidOperationException>(() =>
            PositionEditor.UpdatePosition(_conn, p, new PositionChange("qty", 3m)));
    }

    [Fact]
    public void PositionEditor_SaveCell_NumericAndFormula()
    {
        var p = Documents.AddPosition(_conn, _system, "Pos", "шт", 2m, 100m);
        var r = PositionEditor.SaveCell(_conn, p, "qty", "4+5");
        Assert.True(r.Saved);
        Assert.True(r.Changed);
        Assert.Equal(9m, Convert.ToDecimal(Text($"SELECT qty FROM positions WHERE id={p}")));
        // formula stored
        Assert.Contains("=", Text($"SELECT formulas FROM positions WHERE id={p}"));
    }

    [Fact]
    public void PositionEditor_SaveCell_LockedPurchaseAllowed()
    {
        var p = Documents.AddPosition(_conn, _system, "Pos", "шт", 2m, 100m);
        RowWriter.Update(_conn, "contracts", _contract, new Dictionary<string, object?> { ["locked"] = true });
        var r = PositionEditor.SaveCell(_conn, p, "purchase_price", "5");
        Assert.True(r.Saved);
    }

    [Fact]
    public void PositionEditor_SaveCell_LockedOtherBlocked()
    {
        var p = Documents.AddPosition(_conn, _system, "Pos", "шт", 2m, 100m);
        RowWriter.Update(_conn, "contracts", _contract, new Dictionary<string, object?> { ["locked"] = true });
        var r = PositionEditor.SaveCell(_conn, p, "qty", "5");
        Assert.False(r.Saved);
        Assert.NotNull(r.Error);
    }

    public void Dispose() => _conn.Dispose();

    private string Text(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? "" : Convert.ToString(value)!;
    }

    private int CountHistory() =>
        Convert.ToInt32(Text("SELECT COUNT(*) FROM change_log"));

    private List<string> Positions()
    {
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM positions WHERE system_id={_system} ORDER BY \"order\", id";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    private List<string> Systems()
    {
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT name FROM systems WHERE contract_id={_contract} ORDER BY \"order\", id";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}