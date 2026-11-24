using System.Data.Common;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

public sealed class GenericReaderAndSqlPilotTests
{
    [Fact]
    public void SharedReaders_WorkWithDbConnectionInterface()
    {
        using var sqlite = new SqliteConnection("Data Source=:memory:");
        sqlite.Open();
        Database.EnsureSchema(sqlite);
        var objectId = Documents.CreateObject(sqlite, "Проверка SQL-адаптера");
        var contractId = Documents.CreateContract(sqlite, objectId, "kp", "15", "", "Расценки");
        var systemId = Documents.AddSystem(sqlite, contractId, "П1");
        Documents.AddPosition(sqlite, systemId, "Воздуховод", "м2", 3m, 100m);

        DbConnection generic = sqlite;
        Assert.Contains(Workspace.Objects(generic), x => x.Id == objectId);
        Assert.Contains(Workspace.Documents(generic), x => x.Id == contractId);
        var estimate = EstimateReader.Load(generic, checked((int)contractId));
        Assert.Equal("Проверка SQL-адаптера", estimate.ObjectName);
        Assert.Equal(1, estimate.Positions);
        Assert.Equal(300m, estimate.Totals.Materials);
    }

    [Theory]
    [InlineData("invalid_field", "hello")]
    public void SqlServerPilot_RejectsUnsupportedFieldsBeforeNetwork(string field, string value)
    {
        var result = SqlServerDatabase.SaveCell("this is not a connection string", 1, field, value, "rev");
        Assert.False(result.Saved);
        Assert.Contains("не перенесена", result.Error);
    }

    [Fact]
    public void SqlServerPilot_BasisIsNowSupportedButStillPilotOnly()
    {
        var result = SqlServerDatabase.SaveCell("this is not a connection string", 1, "basis", "M-11", "rev");
        Assert.False(result.Saved);
        Assert.Contains("Некорректная строка подключения", result.Error);
    }

    [Fact]
    public void SqlServerPilot_WorkPrice_IsSupportedField()
    {
        // work_price перенесён: некорректное число отбраковывается до подключения,
        // как у остальных числовых колонок.
        var result = SqlServerDatabase.SaveCell("this is not a connection string", 1, "work_price", "abc", "rev");
        Assert.False(result.Saved);
        Assert.Contains("Недопустимое число", result.Error);
    }

    [Fact]
    public void SqlServerPilot_InsertCopiedRows_ValidatesBeforeNetwork()
    {
        var result = SqlServerDatabase.InsertCopiedRows("this is not a connection string", 1, 1,
            null, Array.Empty<CopiedRow>(), "rev");
        Assert.False(result.Saved);
        Assert.Contains("от 1 до 5000", result.Error);

        var bad = new[] { new CopiedRow("", "шт", 1m, 0m, null, "", "", null) };
        var invalid = SqlServerDatabase.InsertCopiedRows("this is not a connection string", 1, 1, null, bad, "rev");
        Assert.False(invalid.Saved);
        Assert.Contains("не прошли проверку", invalid.Error);
    }

    [Fact]
    public void SqlServerPilot_RejectsOversizedUnitBeforeNetwork()
    {
        var result = SqlServerDatabase.SaveCell("not-a-connection", 1, "unit", new string('a', 33), "rev");
        Assert.False(result.Saved);
        Assert.Contains("32", result.Error);
    }

    [Theory]
    [InlineData("qty", "-2")]
    [InlineData("price", "abc")]
    [InlineData("purchase_price", "2/0")]
    public void SqlServerPilot_RejectsInvalidNumbersBeforeNetwork(string field, string value)
    {
        var result = SqlServerDatabase.SaveCell("this is not a connection string", 1, field, value, "rev");
        Assert.False(result.Saved);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void SqlServerPilot_ReplaceRows_ValidatesBeforeNetwork()
    {
        var result = SqlServerDatabase.ReplaceRows("this is not a connection string", 1,
            Array.Empty<SqlServerDatabase.ReplaceUpdate>(), Array.Empty<long>(),
            Array.Empty<CopiedRow>(), "rev");
        Assert.False(result.Saved);
        Assert.Contains("Выделите строки", result.Error);

        var negative = new[] { new SqlServerDatabase.ReplaceUpdate(1, null, null, -5m, null, null) };
        var invalid = SqlServerDatabase.ReplaceRows("this is not a connection string", 1,
            negative, Array.Empty<long>(), Array.Empty<CopiedRow>(), "rev");
        Assert.False(invalid.Saved);
        Assert.Contains("не прошли проверку", invalid.Error);
    }
}
