using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

/// <summary>Переименование, архив, год архива и удаление объекта с копией в корзину.</summary>
public sealed class ObjectActionsTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    public ObjectActionsTests() { _conn.Open(); Database.EnsureSchema(_conn); }
    public void Dispose() => _conn.Dispose();

    private long SeedObject(string name = "Школа №5") =>
        Documents.CreateObject(_conn, name, "Заказчик", "Адрес");

    /// <summary>Объект с документом, разделом, позицией, закупкой, движениями, актами и выдачей.</summary>
    private (long Object, long Contract, long Position, long Act, long Shipment) SeedFullObject(string name = "Школа №5")
    {
        var obj = SeedObject(name);
        var contract = Documents.CreateContract(_conn, obj, "main", "12", "01.02.2026", "Вентиляция");
        var system = Documents.AddSystem(_conn, contract, "Система");
        var position = Documents.AddPosition(_conn, system, "Решётка", "шт", 10m, 500m);

        RowWriter.Insert(_conn, "procurement", new Dictionary<string, object?> { ["position_id"] = position });
        RowWriter.Insert(_conn, "movements", new Dictionary<string, object?>
        { ["position_id"] = position, ["kind"] = "in", ["qty"] = 4m });
        RowWriter.Insert(_conn, "movements", new Dictionary<string, object?>
        { ["position_id"] = position, ["kind"] = "in", ["qty"] = 6m });

        var act = RowWriter.Insert(_conn, "acts", new Dictionary<string, object?>
        { ["contract_id"] = contract, ["number"] = "1", ["signed"] = true });
        RowWriter.Insert(_conn, "act_lines", new Dictionary<string, object?>
        { ["act_id"] = act, ["position_id"] = position, ["qty"] = 2m, ["price"] = 500m });
        RowWriter.Insert(_conn, "acts", new Dictionary<string, object?>
        { ["contract_id"] = contract, ["number"] = "2", ["signed"] = false });

        RowWriter.Insert(_conn, "smr_costs", new Dictionary<string, object?> { ["contract_id"] = contract });
        RowWriter.Insert(_conn, "attachments", new Dictionary<string, object?>
        { ["contract_id"] = contract, ["name"] = "scan.pdf", ["path"] = @"C:\scan.pdf" });
        RowWriter.Insert(_conn, "spec_revisions", new Dictionary<string, object?> { ["contract_id"] = contract });

        var shipment = RowWriter.Insert(_conn, "shipments", new Dictionary<string, object?>
        { ["object_id"] = obj, ["number"] = "В-1" });
        RowWriter.Insert(_conn, "shipment_lines", new Dictionary<string, object?>
        { ["shipment_id"] = shipment, ["position_id"] = position, ["qty"] = 1m });
        RowWriter.Insert(_conn, "access_lists", new Dictionary<string, object?>
        { ["object_id"] = obj, ["kind"] = "workers", ["file"] = "list.xlsx", ["data"] = "{}" });
        return (obj, contract, position, act, shipment);
    }

    // ---------------------------------------------------------------- переименование

    [Fact]
    public void Rename_TrimsAndWritesHistoryOnce()
    {
        var id = SeedObject("Старое имя");
        Assert.True(Documents.RenameObject(_conn, id, "  Новое имя  "));
        Assert.Equal("Новое имя", Documents.ReadObject(_conn, id).Name);
        Assert.False(Documents.RenameObject(_conn, id, "Новое имя"));      // то же имя — без записи
        Assert.False(Documents.RenameObject(_conn, id, "   "));            // пустое — без записи
        Assert.Equal(2, RowWriter.Count(_conn, "change_log"));             // create + update

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT details FROM change_log WHERE action='update'";
        Assert.Equal("name: «Старое имя» → «Новое имя»", cmd.ExecuteScalar());
    }

    [Fact]
    public void Rename_UnknownObject_Throws()
    {
        Assert.Throws<ArgumentException>(() => Documents.RenameObject(_conn, 999, "Нет такого"));
    }

    // ---------------------------------------------------------------- архив

    [Fact]
    public void Archive_SetsTodayAndUnarchiveClearsDate()
    {
        var id = SeedObject();
        Documents.SetObjectArchived(_conn, id, true);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT archived, archived_on FROM objects WHERE id={id}";
            using var r = cmd.ExecuteReader(); Assert.True(r.Read());
            Assert.Equal(1L, r.GetInt64(0));
            Assert.Equal(DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"), r.IsDBNull(1) ? "" : r.GetString(1));
        }
        Documents.SetObjectArchived(_conn, id, false);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT archived, archived_on FROM objects WHERE id={id}";
            using var r = cmd.ExecuteReader(); Assert.True(r.Read());
            Assert.Equal(0L, r.GetInt64(0));
            Assert.True(r.IsDBNull(1));
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT action FROM change_log WHERE entity='object' ORDER BY id";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read()); Assert.Equal("create", r.GetString(0));
            Assert.True(r.Read()); Assert.Equal("archive", r.GetString(0));
            Assert.True(r.Read()); Assert.Equal("unarchive", r.GetString(0));
        }
    }

    [Fact]
    public void ChangeArchiveYear_MovesYearAndFeb29FallsToFeb28()
    {
        var id = SeedObject();
        Documents.SetObjectArchived(_conn, id, true);
        Database.Exec(_conn, $"UPDATE objects SET archived_on='2024-02-29' WHERE id={id}");
        Documents.ChangeArchiveYear(_conn, id, 2023);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT archived_on FROM objects WHERE id={id}";
        Assert.Equal("2023-02-28", cmd.ExecuteScalar());
        cmd.CommandText = "SELECT details FROM change_log WHERE action='archive_year'";
        Assert.Equal("Школа №5: 2024 → 2023", cmd.ExecuteScalar());
    }

    [Fact]
    public void ChangeArchiveYear_RequiresArchive()
    {
        var id = SeedObject();
        Assert.Throws<InvalidOperationException>(() => Documents.ChangeArchiveYear(_conn, id, 2030));
    }

    // ---------------------------------------------------------------- удаление и корзина

    [Fact]
    public void Delete_StoresFullCopyInTrashAndCascades()
    {
        var (obj, contract, position, act, shipment) = SeedFullObject();
        Documents.DeleteObject(_conn, obj);

        foreach (var table in "objects,contracts,systems,positions,procurement,movements,acts,act_lines,shipments,shipment_lines,access_lists,smr_costs,attachments,spec_revisions".Split(','))
            Assert.Equal(0, RowWriter.Count(_conn, table));

        Assert.Equal(1, RowWriter.Count(_conn, "trash"));
        long rows; string title; string by; string data; string kind;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT kind, rows, title, deleted_by, data FROM trash";
            using var r = cmd.ExecuteReader(); Assert.True(r.Read());
            kind = r.GetString(0); rows = r.GetInt64(1); title = r.GetString(2); by = r.GetString(3); data = r.GetString(4);
        }
        Assert.Equal("object", kind);
        Assert.Equal(1, rows);                                   // одна позиция сметы
        Assert.Equal("Школа №5", title);
        Assert.Equal(ChangeLog.CurrentUser, by);

        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("objects").GetArrayLength());
        Assert.Equal(1, root.GetProperty("contracts").GetArrayLength());
        Assert.Equal(1, root.GetProperty("systems").GetArrayLength());
        Assert.Equal(1, root.GetProperty("positions").GetArrayLength());
        Assert.Equal(1, root.GetProperty("procurement").GetArrayLength());
        Assert.Equal(2, root.GetProperty("movements").GetArrayLength());
        Assert.Equal(2, root.GetProperty("acts").GetArrayLength());
        Assert.Equal(1, root.GetProperty("act_lines").GetArrayLength());
        Assert.Equal(1, root.GetProperty("shipments").GetArrayLength());
        Assert.Equal(1, root.GetProperty("shipment_lines").GetArrayLength());
        Assert.Equal(1, root.GetProperty("smr_costs").GetArrayLength());
        Assert.Equal(1, root.GetProperty("attachments").GetArrayLength());
        Assert.Equal(1, root.GetProperty("spec_revisions").GetArrayLength());
        Assert.Equal(1, root.GetProperty("access_lists").GetArrayLength());
        // старые id сохраняются в копии — как в Python restore
        Assert.Equal(obj, root.GetProperty("objects")[0].GetProperty("id").GetInt64());
        Assert.Equal(position, root.GetProperty("positions")[0].GetProperty("id").GetInt64());

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT action FROM change_log ORDER BY id DESC LIMIT 1";
            Assert.Equal("delete", cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void Delete_HistoryFailureRollsBackObjectAndTrash()
    {
        var (obj, _, _, _, _) = SeedFullObject();
        Database.Exec(_conn, "CREATE TRIGGER reject_history BEFORE INSERT ON change_log BEGIN SELECT RAISE(ABORT,'test'); END");
        Assert.Throws<SqliteException>(() => Documents.DeleteObject(_conn, obj));
        Assert.Equal(1, RowWriter.Count(_conn, "objects"));
        Assert.Equal(0, RowWriter.Count(_conn, "trash"));
    }

    [Fact]
    public void DescribeForDelete_ListsDocumentsActsShipmentsAndMovements()
    {
        var (obj, contract, position, _, _) = SeedFullObject();
        for (var i = 0; i < 4; i++)                                // всего 5 документов
            Documents.CreateContract(_conn, obj, "kp", $"КП{i + 2}", "", $"Черновик {i}");
        var info = Documents.DescribeObjectForDelete(_conn, obj);

        Assert.Equal("Школа №5", info.Name);
        Assert.Equal(4, info.Details.Count);
        Assert.StartsWith("документы: 5 (Договор № 12 от 01.02.2026 — Вентиляция, КП № КП2 — Черновик 0, КП № КП3 — Черновик 1, КП № КП4 — Черновик 2 …)",
            info.Details[0]);
        Assert.Equal("акты КС-2/КС-3: 2, из них подписанных: 1", info.Details[1]);
        Assert.Equal("ведомости выдачи: 1", info.Details[2]);
        Assert.Equal("история закупок и выдачи: 2 записей", info.Details[3]);
    }

    [Fact]
    public void DescribeForDelete_EmptyObject_SaysEmpty()
    {
        var id = SeedObject("Пустой");
        var info = Documents.DescribeObjectForDelete(_conn, id);
        Assert.Empty(info.Details);
    }
}
