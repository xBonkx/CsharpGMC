using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

public sealed class ObjectPropertiesTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    public ObjectPropertiesTests() { _conn.Open(); Database.EnsureSchema(_conn); }
    public void Dispose() => _conn.Dispose();

    [Fact]
    public void Create_TrimsFormFieldsButPreservesNoteAndLogsOwner()
    {
        var id = Documents.CreateObject(_conn, "  Объект  ", " Заказчик ", " Адрес ", " D:\\Local ", "  Примечание\nстрока  ", "legacy");
        Assert.Equal(new ObjectProperties("Объект", "Заказчик", "Адрес", "D:\\Local", "  Примечание\nстрока  ", "legacy"), Documents.ReadObject(_conn, id));
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT action,details,object_id,contract_id FROM change_log";
        using var r = cmd.ExecuteReader(); Assert.True(r.Read());
        Assert.Equal("create", r.GetString(0)); Assert.Equal("Объект", r.GetString(1));
        Assert.Equal(id, r.GetInt64(2)); Assert.True(r.IsDBNull(3)); Assert.False(r.Read());
    }

    [Fact]
    public void Update_LogsOnlyChangedFieldsAndDoesNotChangeExistingDocumentTag()
    {
        var id = Documents.CreateObject(_conn, "Old", form: "old");
        var doc = Documents.CreateContract(_conn, id);
        Database.Exec(_conn, "UPDATE contracts SET locked=1");
        var value = Documents.ReadObject(_conn, id) with { Name = " New ", Form = "new", Note = "\ntext\n" };
        Assert.True(Documents.UpdateObject(_conn, id, value));
        Assert.False(Documents.UpdateObject(_conn, id, value));
        Assert.Equal(3, RowWriter.Count(_conn, "change_log"));
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT details FROM change_log WHERE action='update'";
        Assert.Equal("name: «Old» → «New»; note: «» → «\ntext\n»; form: «old» → «new»", cmd.ExecuteScalar());
        cmd.CommandText = $"SELECT form FROM contracts WHERE id={doc}"; Assert.Equal("old", cmd.ExecuteScalar());
        Assert.Equal("new", Documents.ReadObject(_conn, id).Form);
    }

    [Fact]
    public void InvalidNameAndMissingObject_DoNotWriteAnything()
    {
        Assert.Throws<ArgumentException>(() => Documents.CreateObject(_conn, " \r\n "));
        var id = Documents.CreateObject(_conn, "Kept");
        Assert.Throws<ArgumentException>(() => Documents.UpdateObject(_conn, id, new(" ")));
        Assert.Throws<ArgumentException>(() => Documents.UpdateObject(_conn, 999, new("Unknown")));
        Assert.Equal("Kept", Documents.ReadObject(_conn, id).Name);
        Assert.Equal(1, RowWriter.Count(_conn, "objects")); Assert.Equal(1, RowWriter.Count(_conn, "change_log"));
    }

    [Fact]
    public void HistoryFailure_RollsBackCreateAndUpdateAtomically()
    {
        var id = Documents.CreateObject(_conn, "Before");
        Database.Exec(_conn, "CREATE TRIGGER reject_history BEFORE INSERT ON change_log BEGIN SELECT RAISE(ABORT,'test history failure'); END");
        Assert.Throws<SqliteException>(() => Documents.CreateObject(_conn, "Not saved"));
        Assert.Throws<SqliteException>(() => Documents.UpdateObject(_conn, id, new("Not saved")));
        Assert.Equal("Before", Documents.ReadObject(_conn, id).Name);
        Assert.Equal(1, RowWriter.Count(_conn, "objects")); Assert.Equal(1, RowWriter.Count(_conn, "change_log"));
    }

    [Fact]
    public void Tags_KeepUnknownCurrentAndSortCaptionsLikePython()
    {
        RowWriter.Insert(_conn, "tags", new Dictionary<string, object?> { ["key"] = "b", ["name"] = "Бета" });
        RowWriter.Insert(_conn, "tags", new Dictionary<string, object?> { ["key"] = "a", ["name"] = "Альфа" });
        Assert.Equal(new[] { "", "a", "b", "old" }, Documents.ObjectTags(_conn, "old").Select(t => t.Key));
        Assert.Equal("— обычный", Documents.ObjectTags(_conn)[0].Caption);
        Assert.Equal(3, Documents.ObjectTags(_conn, "a").Count);
    }
}
