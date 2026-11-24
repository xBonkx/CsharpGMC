using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

/// <summary>
/// Rejected requests never open a SQL connection. This complements the
/// real SQL Server pilot transaction test to be run on the user's PC.
/// </summary>
public sealed class SqlServerGroupEditValidationTests
{
    [Fact]
    public void SingleRowCannotBeSavedAsEqualGroup()
    {
        var result = SqlServerDatabase.SaveGroup("not a connection string",
            20, "вентилятор|шт", new long[] { 2 }, "price", "300", "rev");
        Assert.False(result.Saved);
        Assert.Equal(0, result.Changed);
    }

    [Fact]
    public void DuplicateIdsCannotBeEditedTwice()
    {
        var result = SqlServerDatabase.SaveGroup("not a connection string",
            20, "вентилятор|шт", new long[] { 2, 2 }, "price", "300", "rev");
        Assert.False(result.Saved);
    }

    [Theory]
    [InlineData("qty")]
    [InlineData("note")]
    [InlineData("contract_id")]
    [InlineData("price; DROP TABLE positions")]
    public void OnlyWhitelistedColumnsAllowed(string field)
    {
        var result = SqlServerDatabase.SaveGroup("not a connection string",
            20, "вентилятор|шт", new long[] { 2, 3 }, field, "300", "rev");
        Assert.False(result.Saved);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("1/0")]
    public void InvalidNumericInputFailsBeforeConnection(string input)
    {
        var result = SqlServerDatabase.SaveGroup("not a connection string",
            20, "вентилятор|шт", new long[] { 2, 3 }, "price", input, "rev");
        Assert.False(result.Saved);
    }

    [Fact]
    public void EmptyNameRejectedWithoutContactingSqlServer()
    {
        var result = SqlServerDatabase.SaveGroup("not a connection string",
            20, "вентилятор|шт", new long[] { 2, 3 }, "name", "", "rev");
        Assert.False(result.Saved);
    }
}
