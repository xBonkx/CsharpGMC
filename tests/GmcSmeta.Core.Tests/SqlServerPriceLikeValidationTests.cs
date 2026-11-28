using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SqlServerPriceLikeValidationTests
{
    private const string Production =
        "Server=localhost;Database=GmcSmetaProduction;Integrated Security=True;TrustServerCertificate=True";

    [Fact]
    public void BatchedPriceCopyNeverConnectsToProduction()
    {
        var result = SqlServerPriceLike.SetPriceLike(Production, 10, 100,
            "price", "rev");
        Assert.False(result.Saved);
        Assert.Equal("rev", result.Revision);
        Assert.Contains("GmcSmetaPilot_", result.Error!);
    }

    [Theory]
    [InlineData("qty")]
    [InlineData("price;DELETE FROM dbo.positions")]
    [InlineData("work_price")]
    public void RejectsUnwhitelistedFieldsBeforeConnection(string field)
    {
        var result = SqlServerPriceLike.SetPriceLike("bad", 10, 100, field, "rev");
        Assert.False(result.Saved);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingRevisionIsRejectedBeforeConnection(string revision)
    {
        var result = SqlServerPriceLike.SetPriceLike("bad", 10, 100, "price", revision);
        Assert.False(result.Saved);
        Assert.Equal(0, result.Changed);
        Assert.Equal(revision, result.Revision);
    }

    [Fact]
    public void InvalidIdentifiersAreRejectedBeforeConnection()
    {
        var result = SqlServerPriceLike.SetPriceLike("bad", 0, 100, "price", "rev");
        Assert.False(result.Saved);
        Assert.Equal(0, result.Changed);
    }
}
