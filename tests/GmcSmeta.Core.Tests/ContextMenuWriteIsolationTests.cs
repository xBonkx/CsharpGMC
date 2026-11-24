using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class ContextMenuWriteIsolationTests
{
    private const string Production = "Server=invalid;Database=GmcSmeta;Integrated Security=true;Connect Timeout=1;TrustServerCertificate=true";

    [Theory]
    [InlineData("price")]
    [InlineData("purchase_price")]
    [InlineData("cost_price")]
    [InlineData("supplier")]
    [InlineData("cost_note")]
    public void ClearMaterialsCannotOpenProduction(string field)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SqlServerCostOperations.ClearMaterialCells(Production, 1, new long[] { 1, 2 }, field, "rev"));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("qty")]
    [InlineData("work_price")]
    public void ClearMaterialsRejectsUnlistedFieldsBeforeConnection(string field)
    {
        Assert.Throws<ArgumentException>(() =>
            SqlServerCostOperations.ClearMaterialCells(Production, 1, new long[] { 1 }, field, "rev"));
    }

    [Fact]
    public void ClearMaterialsRejectsInvalidIdsRatherThanPartialWrite()
    {
        Assert.Throws<ArgumentException>(() =>
            SqlServerCostOperations.ClearMaterialCells(Production, 1, new long[] { 1, 0 }, "price", "rev"));
    }

    [Fact]
    public void EmptyMaterialSelectionDoesNotOpenDatabase()
    {
        var result = SqlServerCostOperations.ClearMaterialCells(Production, 1, Array.Empty<long>(), "price", "rev");
        Assert.Equal(0, result.Changed);
        Assert.Equal(0, result.Skipped);
        Assert.Equal("rev", result.Revision);
    }
}
