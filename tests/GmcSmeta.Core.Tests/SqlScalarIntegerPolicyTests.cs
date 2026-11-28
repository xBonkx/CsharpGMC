using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SqlScalarIntegerPolicyTests
{
    [Theory]
    [InlineData("SqlServerSections.cs")]
    [InlineData("SqlServerAgreements.cs")]
    [InlineData("SqlServerSystemCopy.cs")]
    [InlineData("SqlServerPtoBackup.cs")]
    [InlineData("SqlServerAttachments.cs")]
    [InlineData("SqlServerPto.cs")]
    [InlineData("SqlServerPositions.cs")]
    [InlineData("SqlServerPassports.cs")]
    public void ReviewedRequiredScalarResultsAreNotCoerced(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GmcSmeta.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db", name));
        Assert.DoesNotMatch(@"Convert\.ToInt(?:32|64)\(\w+\.ExecuteScalar\(\)", source);
        Assert.Matches(@"SqlInteger\.RequireInt64\(\w+\.ExecuteScalar\(\)\)", source);
    }

    [Fact]
    public void MissingScalarIsNotSuccessfulLockOrEmptyCount()
    {
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt64(null));
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt64(DBNull.Value));
        Assert.Equal(0L, SqlInteger.RequireInt64(0));
        Assert.Equal(-1L, SqlInteger.RequireInt64(-1));
        Assert.Equal(long.MaxValue, SqlInteger.RequireInt64(long.MaxValue));
        Assert.Throws<OverflowException>(() => { _ = checked((int)SqlInteger.RequireInt64(long.MaxValue)); });
    }
}
