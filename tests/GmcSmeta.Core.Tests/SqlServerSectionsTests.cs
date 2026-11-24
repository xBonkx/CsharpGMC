using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

/// <summary>
/// Pre-connection validation for SQL Server section writes (no database touched).
/// SQL Server transaction and concurrency tests require an isolated test instance on Windows.
/// </summary>
public sealed class SqlServerSectionsTests
{
    [Theory]
    [InlineData("", false, null, "пустым")]
    [InlineData("    ", true, 2L, "пустым")]
    [InlineData("П1", true, null, "выбери")]
    public void AddSection_RejectsInvalidInputWithoutConnecting(
        string name, bool subsection, long? anchor, string expected)
    {
        var result = SqlServerDatabase.AddSection("not-a-connection-string", 1,
            name, subsection, anchor, "rev");
        Assert.False(result.Saved);
        Assert.Contains(expected, result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("rev", result.Revision);
    }

    [Fact]
    public void AddSection_RejectsLongNameBeforeNetwork()
    {
        var result = SqlServerDatabase.AddSection("not-a-connection-string", 1,
            new string('П', 256), false, null, "rev");
        Assert.False(result.Saved);
        Assert.Contains("255", result.Error!);
    }

    [Theory]
    [InlineData(0L, 1L, "П1")]
    [InlineData(1L, 0L, "П1")]
    [InlineData(1L, 1L, "   ")]
    public void RenameSection_RejectsInvalidInputWithoutConnecting(long contractId, long systemId, string name)
    {
        var result = SqlServerDatabase.RenameSection("not-a-connection-string", contractId,
            systemId, name, "rev");
        Assert.False(result.Saved);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void RenameSection_RejectsLongNameBeforeNetwork()
    {
        var result = SqlServerDatabase.RenameSection("not-a-connection-string", 1,
            1, new string('П', 256), "rev");
        Assert.False(result.Saved);
        Assert.Contains("255", result.Error!);
    }
}
