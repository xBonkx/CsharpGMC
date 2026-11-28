using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

/// <summary>
/// Офлайн-проверки восстановления из корзины SQL Server (trash.py): приведение
/// значений снимка к типам колонок, перепривязка формул объёмов, счётчик дней,
/// пилот-гвард. Живые SQL-операции выполняются только в базе GmcSmetaPilot_*.
/// </summary>
public sealed class SqlServerTrashRestoreTests
{
    private static JsonElement Json(string text) =>
        JsonSerializer.Deserialize<JsonElement>(text);

    [Theory]
    [InlineData("true", "bit", true)]
    [InlineData("false", "bit", false)]
    [InlineData("1", "bit", true)]
    [InlineData("0", "bit", false)]
    [InlineData("123", "bigint", 123L)]
    [InlineData("7", "int", 7)]
    [InlineData("2.5", "float", 2.5)]
    public void JsonToSql_ConvertsNumbersAndBooleansByColumnType(string json, string dataType, object expected)
    {
        var value = SqlServerTrash.JsonToSql(Json(json), dataType);
        Assert.Equal(expected, value);
    }

    [Fact]
    public void JsonToSql_ParsesSnapshotDatesLikePythonIsoformat()
    {
        // Python _store пишет date.isoformat()/datetime.isoformat(), C#-снимок — ISO-8601.
        Assert.Equal(new DateTime(2026, 11, 20),
            SqlServerTrash.JsonToSql(Json("\"2026-11-20\""), "date"));
        Assert.Equal(new DateTime(2026, 11, 20, 16, 34, 52),
            SqlServerTrash.JsonToSql(Json("\"2026-11-20T16:34:52\""), "datetime2"));
        Assert.Equal(new DateTime(2026, 11, 20, 16, 34, 52, 420),
            SqlServerTrash.JsonToSql(Json("\"2026-11-20T16:34:52.420\""), "datetime"));
        Assert.Equal(new DateTime(2026, 11, 20, 16, 34, 52).AddTicks(1234567),
            SqlServerTrash.JsonToSql(Json("\"2026-11-20T16:34:52.1234567\""), "datetime2"));
    }

    [Fact]
    public void JsonToSql_NullKeepsDBNullAndTextKeepsText()
    {
        Assert.Equal(DBNull.Value, SqlServerTrash.JsonToSql(Json("null"), "nvarchar"));
        Assert.Equal(DBNull.Value, SqlServerTrash.JsonToSql(Json("null"), "bigint"));
        Assert.Equal("Воздуховод 500×400", SqlServerTrash.JsonToSql(Json("\"Воздуховод 500×400\""), "nvarchar"));
        Assert.Equal(Guid.Parse("5b1d3f4a-6b2c-4e8f-9a01-c2d3e4f50607"),
            SqlServerTrash.JsonToSql(Json("\"5b1d3f4a-6b2c-4e8f-9a01-c2d3e4f50607\""), "uniqueidentifier"));
    }

    [Fact]
    public void RemapIds_RebindsFormulaReferencesToNewPositionIds()
    {
        // trash.restore: STORED.sub — известные id заменяются, неизвестные остаются.
        var map = new Dictionary<long, long> { [5] = 100, [6] = 200 };
        Assert.Equal("@100*1,5", ActQuantityFormulas.RemapIds("@5*1,5", map));
        Assert.Equal("=@100+@200", ActQuantityFormulas.RemapIds("=@5+@6", map));
        Assert.Equal("@100+@999", ActQuantityFormulas.RemapIds("@5+@999", map));
        Assert.Equal("К2*2", ActQuantityFormulas.RemapIds("К2*2", map));
        Assert.Equal("", ActQuantityFormulas.RemapIds("", map));
    }

    [Fact]
    public void TrashEntry_DaysLeftCountsFromDeletion()
    {
        var fresh = new SqlServerTrash.TrashEntry(1, "object", "Объект", "Объект", 3,
            DateTime.Now.AddDays(-1), "user");
        Assert.Equal(29, fresh.DaysLeft);
        var expired = new SqlServerTrash.TrashEntry(2, "object", "Объект", "Объект", 0,
            DateTime.Now.AddDays(-40), "user");
        Assert.Equal(0, expired.DaysLeft);
        var unknown = new SqlServerTrash.TrashEntry(3, "object", "Объект", "Объект", 0, null, "user");
        Assert.Equal(SqlServerTrash.Days, unknown.DaysLeft);
    }

    private static JsonElement LegalSnapshot() => Json("""
        {"objects":[{"id":10,"archived":false}],
         "agreements":[{"id":20,"object_id":10,"archived":false,"number":"D-1"},
                       {"id":21,"object_id":10,"archived":false,"number":"D-2"}]}
        """);

    [Fact]
    public void LegalCardsWithoutAnnexesRequireCompatibleSchema()
    {
        var snapshot = LegalSnapshot();
        TrashRestoreSafety.ValidateSnapshot(snapshot, "object");
        TrashRestoreSafety.ValidateLegalSchema(snapshot, true);
        Assert.Throws<InvalidOperationException>(() => TrashRestoreSafety.ValidateLegalSchema(snapshot, false));
    }

    [Fact]
    public void PermanentDeletionOfIndependentLegalCardsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => TrashRestoreSafety.ValidateForget(LegalSnapshot(), "object"));
        TrashRestoreSafety.ValidateForget(Json("""{"objects":[{"id":10,"archived":false}]}"""), "object");
    }

    [Theory]
    [InlineData("""{"id":1,"object_id":10,"kind":"kp","locked":false,"agreement_id":20}""")]
    [InlineData("""{"id":1,"object_id":10,"kind":"kp","locked":false,"annex_number":"1"}""")]
    public void KpCannotCarryLegalAnnexIdentity(string contract)
    {
        var snapshot = Json("{\"contracts\":[" + contract + "]}");
        Assert.Throws<InvalidOperationException>(() => TrashRestoreSafety.ValidateSnapshot(snapshot, "contract"));
        Assert.Throws<InvalidOperationException>(() => TrashRestoreSafety.ValidateLegalSchema(snapshot, false));
    }

    [Fact]
    public void ArchivedLegalHistoryAndLegalFilesRemainProtected()
    {
        var archived = Json("""{"objects":[{"id":10,"archived":false}],"agreements":[{"id":20,"object_id":10,"archived":true}]}""");
        Assert.Throws<InvalidOperationException>(() => TrashRestoreSafety.ValidateSnapshot(archived, "object"));
        var files = Json("""{"objects":[{"id":10,"archived":false}],"agreement_files":[{"id":30,"agreement_id":20}]}""");
        Assert.Throws<InvalidOperationException>(() => TrashRestoreSafety.ValidateSnapshot(files, "object"));
    }

    [Fact]
    public void LegalColumnSetMustBeCompleteAndUnambiguous()
    {
        string[] columns = ["id", "object_id", "archived", "number"];
        TrashRestoreSafety.ValidateLegalColumns(LegalSnapshot(), columns);
        Assert.Throws<InvalidOperationException>(() =>
            TrashRestoreSafety.ValidateLegalColumns(LegalSnapshot(), columns.Append("subject")));
        Assert.Throws<InvalidOperationException>(() =>
            TrashRestoreSafety.ValidateLegalColumns(LegalSnapshot(), columns.Take(3)));
        var duplicate = Json("""{"agreements":[{"id":20,"ID":21,"object_id":10,"archived":false,"number":"D-1"}]}""");
        Assert.Throws<InvalidDataException>(() => TrashRestoreSafety.ValidateLegalColumns(duplicate, columns));
    }

    [Fact]
    public void EmptyLegalSectionsAndLegacyKpRemainCompatible()
    {
        var snapshot = Json("""{"contracts":[{"id":1,"object_id":10,"kind":"kp","locked":false,"agreement_id":null,"annex_number":""}],"agreements":[]}""");
        TrashRestoreSafety.ValidateSnapshot(snapshot, "contract");
        TrashRestoreSafety.ValidateLegalSchema(snapshot, false);
        TrashRestoreSafety.ValidateForget(snapshot, "contract");
    }

    [Fact]
    public void RestoreForgetAndPurge_RejectNonPilotCatalogsBeforeConnecting()
    {
        var production = "Server=localhost;Database=Production;Integrated Security=true";
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerTrash.Restore(production, 1, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerTrash.Forget(production, 1, "rev"));
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerTrash.Purge(production));
    }
}
