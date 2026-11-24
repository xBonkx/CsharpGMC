using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

/// <summary>Unsafe structural edits must fail before connecting to SQL Server.</summary>
public sealed class SqlServerAreaTransformValidationTests
{
    private const string Production =
        "Server=localhost;Database=GmcSmetaProduction;Integrated Security=True;TrustServerCertificate=True";

    [Fact]
    public void CollapseCannotWriteProductionDatabase()
    {
        var result = SqlServerDatabase.CollapseArea(Production, 10, false, "rev");
        Assert.False(result.Saved);
        Assert.Equal("rev", result.Revision);
        Assert.Contains("GmcSmetaPilot_", result.Error!);
    }

    [Fact]
    public void WholeContractCollapseCannotWriteProductionDatabase()
    {
        var result = SqlServerDatabase.CollapseArea(Production, 10, true, "rev");
        Assert.False(result.Saved);
        Assert.Contains("GmcSmetaPilot_", result.Error!);
    }

    [Fact]
    public void ExpandCannotWriteProductionDatabase()
    {
        var result = SqlServerDatabase.ExpandArea(Production, 10, null, "rev");
        Assert.False(result.Saved);
        Assert.Contains("GmcSmetaPilot_", result.Error!);
    }

    [Fact]
    public void InvalidIdsDoNotAttemptSqlConnection()
    {
        var collapse = SqlServerDatabase.CollapseArea(Production, 0, false, "rev");
        var expand = SqlServerDatabase.ExpandArea(Production, 10, -1, "rev");
        Assert.False(collapse.Saved);
        Assert.False(expand.Saved);
    }

    [Fact]
    public void InvalidConnectionStringCannotOpenConnection()
    {
        var result = SqlServerDatabase.ExpandArea("Invalid=This", 10, null, "rev");
        Assert.False(result.Saved);
    }
}
