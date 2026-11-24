using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

/// <summary>
/// Protect new area/basis editing before opening a SQL connection; real
/// rollback / parent summary update requires a separate pilot DB test.
/// </summary>
public sealed class SqlServerSpecialColumnsValidationTests
{
    private const string Prod =
        "Server=localhost;Database=GmcSmetaProd;Integrated Security=True;TrustServerCertificate=True";

    [Theory]
    [InlineData("m² всего")]
    [InlineData("qty")]
    [InlineData("basis;DROP TABLE positions")]
    public void OnlyWhitelistedFieldsAreAccepted(string field)
    {
        var result = SqlServerDatabase.SaveSpecialCell(Prod, 10, field, "1", "rev");
        Assert.False(result.Saved);
        Assert.Equal("rev", result.Revision);
    }

    [Theory]
    [InlineData("-2")]
    [InlineData("abc")]
    [InlineData("1/0")]
    [InlineData("2^10")]
    public void InvalidManualAreaRejectedBeforeConnection(string text)
    {
        var result = SqlServerDatabase.SaveCell(Prod, 10, "area_manual", text, "rev");
        Assert.False(result.Saved);
        Assert.Contains("площад", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProductionDatabaseCannotUseNewManualEditor()
    {
        var result = SqlServerDatabase.SaveCell(Prod, 10, "area_manual", "0,5", "rev");
        Assert.False(result.Saved);
        Assert.Contains("GmcSmetaPilot_", result.Error!);
    }

    [Fact]
    public void ProductionDatabaseCannotUseNewBasisEditor()
    {
        var result = SqlServerDatabase.SaveCell(Prod, 10, "basis", "ФЕР-15-01", "rev");
        Assert.False(result.Saved);
        Assert.Contains("GmcSmetaPilot_", result.Error!);
    }

    [Fact]
    public void IdMustBePositive()
    {
        var result = SqlServerDatabase.SaveSpecialCell(Prod, -1, "basis", "x", "rev");
        Assert.False(result.Saved);
    }
}
