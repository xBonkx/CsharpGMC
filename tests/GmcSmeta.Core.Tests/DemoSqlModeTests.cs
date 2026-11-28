using System.Data;
using GmcSmeta.Core.Demo;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Tests;

public sealed class DemoSqlModeTests
{
    private const string Db = DemoMode.Prefix + "123456abcdef";

    [Fact]
    public void SmokeDiagnosticsAreLimitedToDemoSmokeArguments()
    {
        Assert.False(DemoMode.IsSmokeRequested(["--smoke-test"]));
        Assert.False(DemoMode.IsSmokeRequested(["--demo", Db]));
        Assert.True(DemoMode.IsSmokeRequested(["--demo", Db, "--smoke-test"]));
    }

    [Fact]
    public void OnlyGeneratedDatabaseIsAllowed()
    {
        Assert.Equal(Db, DemoMode.DatabaseFromArgs(["--demo", Db]));
        Assert.Equal(Db, DemoMode.DatabaseFromArgs(["--demo", Db, "--smoke-test"]));
        Assert.Throws<InvalidOperationException>(() => DemoMode.DatabaseFromArgs(["--demo"]));
        Assert.Throws<InvalidOperationException>(() => DemoMode.DatabaseFromArgs(["--demo", "master"]));
        Assert.Throws<InvalidOperationException>(() => DemoMode.DatabaseFromArgs(["--demo", "GmcSmetaPilot_Cutover_20261125"]));
        Assert.Throws<InvalidOperationException>(() => DemoMode.DatabaseFromArgs(["--demo", Db, "--open", "1"]));
    }

    [Fact]
    public void ConnectionCannotRedirectToAnotherDatabaseOrServer()
    {
        var builder = new SqlConnectionStringBuilder(DemoMode.CreateConnectionString(Db));
        DemoMode.ValidateConnection(builder, ["--demo", Db]);
        Assert.True(SqlSchemaCompatibility.IsAgreementTestDatabase(Db, builder.DataSource));
        Assert.Throws<InvalidOperationException>(() => DemoMode.ValidateConnection(builder, []));
        builder.DataSource = "tcp:192.168.1.46,1433";
        Assert.Throws<InvalidOperationException>(() => DemoMode.ValidateConnection(builder, ["--demo", Db]));
        builder.DataSource = DemoMode.LocalServer;
        builder.InitialCatalog = "master";
        Assert.Throws<InvalidOperationException>(() => DemoMode.ValidateConnection(builder, ["--demo", Db]));
    }

    [Theory]
    [InlineData(0, "master")]
    [InlineData(1, "OTHER")]
    [InlineData(2, "SQLEXPRESS")]
    [InlineData(3, "TCP")]
    [InlineData(4, "")]
    [InlineData(5, "28")]
    public void ServerIdentityAndReadinessMustMatch(int index, string bad)
    {
        var table = new DataTable();
        for (var i = 0; i < 6; i++) table.Columns.Add("c" + i, typeof(string));
        object[] good = [Db, "LOCAL", "MSSQLSERVER", "Shared memory", Db, "32"];
        table.Rows.Add(good);
        var wrong = good.ToArray();
        wrong[index] = bad;
        table.Rows.Add(wrong);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        DemoMode.ValidateIdentity(reader, Db, "LOCAL");
        Assert.True(reader.Read());
        Assert.Throws<InvalidOperationException>(() => DemoMode.ValidateIdentity(reader, Db, "LOCAL"));
    }

    [Fact]
    public void UnnamedDefaultInstanceIsAccepted()
    {
        var table = new DataTable();
        for (var i = 0; i < 6; i++) table.Columns.Add("c" + i, typeof(string));
        table.Rows.Add(Db, "LOCAL", DBNull.Value, "Shared memory", Db, "32");
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        DemoMode.ValidateIdentity(reader, Db, "LOCAL");
    }
}
