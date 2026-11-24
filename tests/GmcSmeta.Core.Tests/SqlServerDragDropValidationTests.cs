using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SqlServerDragDropValidationTests
{
    private const string Production =
        "Server=localhost;Database=GmcSmetaProduction;Integrated Security=True;TrustServerCertificate=True";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChangingHierarchyCannotOpenProductionDatabase(bool makeSub)
    {
        var result = SqlServerDatabase.MakeSubposition(Production, 1, 42, makeSub, "rev");
        Assert.False(result.Saved);
        Assert.Equal("rev", result.Revision);
        Assert.Contains("GmcSmetaPilot_", result.Error!);
    }

    [Fact]
    public void DraggingRowsCannotOpenProductionDatabase()
    {
        var result = SqlServerDatabase.DragPositions(Production, 1,
            new long[] { 1, 2 }, 3, 4, "rev");
        Assert.False(result.Saved);
        Assert.Equal("rev", result.Revision);
        Assert.Contains("GmcSmetaPilot_", result.Error!);
    }

    [Fact]
    public void DraggingSystemCannotOpenProductionDatabase()
    {
        var result = SqlServerDatabase.DragSystem(Production, 1, 2, null, "rev");
        Assert.False(result.Saved);
        Assert.Contains("GmcSmetaPilot_", result.Error!);
    }

    [Fact]
    public void DraggingSubsectionCannotOpenProductionDatabase()
    {
        var result = SqlServerDatabase.DragSubsection(Production, 1, 3, 4, null, "rev");
        Assert.False(result.Saved);
        Assert.Contains("GmcSmetaPilot_", result.Error!);
    }

    [Fact]
    public void DuplicatedOrInvalidSelectionsAreRejectedBeforeConnection()
    {
        var duplicates = SqlServerDatabase.DragPositions(Production, 1,
            new long[] { 42, 42 }, 3, null, "rev");
        Assert.False(duplicates.Saved);
        var empty = SqlServerDatabase.DragPositions(Production, 1,
            Array.Empty<long>(), 3, null, "rev");
        Assert.False(empty.Saved);
        var negative = SqlServerDatabase.DragPositions(Production, 1,
            new long[] { -42 }, 3, null, "rev");
        Assert.False(negative.Saved);
    }

    [Fact]
    public void InvalidSubsectionParentRejectedBeforeConnection()
    {
        var result = SqlServerDatabase.DragSubsection(Production, 1, 4, 0, null, "rev");
        Assert.False(result.Saved);
        Assert.Equal("rev", result.Revision);
    }

    [Fact]
    public void WrongSqlConnectionStringRejectedBeforeConnecting()
    {
        var result = SqlServerDatabase.DragSystem("Database=master;Server=localhost",
            1, 2, null, "rev");
        Assert.False(result.Saved);
        Assert.Contains("GmcSmetaPilot_", result.Error!);
    }
}
