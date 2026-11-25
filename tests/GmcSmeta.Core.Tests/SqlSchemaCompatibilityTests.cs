using GmcSmeta.Core.Db;
using Xunit;

namespace GmcSmeta.Core.Tests;

public class SqlSchemaCompatibilityTests
{
    [Theory]
    [InlineData("GmcSmetaPilot_Cutover_20261120_160840", "DIMA\\SQLEXPRESS")]
    [InlineData("GmcSmetaPilot_Cutover_20261120_160840", "tcp:192.168.1.46,1433")]
    [InlineData("gmcsmetapilot_cutover_OTHER", "localhost")]
    [InlineData("GmcSmetaPilot_Cutover_20261120_160840", "localhost.example.com")]
    public void ProductionNameIsRejectedBeforeConnecting(string name, string server)
    {
        Assert.False(SqlSchemaCompatibility.IsWritablePilot(name, server));
        Assert.Throws<InvalidOperationException>(() => SqlSchemaCompatibility.Validate("28", name, null, false, server));
        var cs = $"Server={server};Database={name};Integrated Security=true";
        Assert.Throws<InvalidOperationException>(() => SqlServerWorkspace.RequirePilotDatabase(cs));
        Assert.Throws<InvalidOperationException>(() => SqlServerDatabase.Open(cs));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("tcp:localhost,1433")]
    [InlineData("tcp:127.0.0.1,1433")]
    public void OwnerConfirmedLocalCutoverAllowsSchema28Only(string server)
    {
        var name = SqlSchemaCompatibility.LocalTestCutover;
        Assert.True(SqlSchemaCompatibility.IsWritablePilot(name, server));
        Assert.False(SqlSchemaCompatibility.IsWritablePilot(name));
        SqlServerWorkspace.RequirePilotDatabase($"Server={server};Database={name};Integrated Security=true");
        SqlSchemaCompatibility.Validate("28", name, null, false, server);
        Assert.Throws<InvalidOperationException>(() =>
            SqlSchemaCompatibility.Validate("31", name, "READ_WRITE", true, server));
    }

    [Fact]
    public void PreviewRequiresVersion31ReadOnlyAndColumns()
    {
        var name = SqlSchemaCompatibility.PreviewDatabase;
        SqlSchemaCompatibility.Validate("31", name, "READ_ONLY", true);
        Assert.False(SqlSchemaCompatibility.IsWritablePilot(name));
        Assert.Throws<InvalidOperationException>(() => SqlServerWorkspace.RequirePilotDatabase(
            $"Server=localhost;Database={name};Integrated Security=true"));
        Assert.Throws<InvalidOperationException>(() => SqlSchemaCompatibility.Validate("31", name, "READ_WRITE", true));
        Assert.Throws<InvalidOperationException>(() => SqlSchemaCompatibility.Validate("31", name, "READ_ONLY", false));
        Assert.Throws<InvalidOperationException>(() => SqlSchemaCompatibility.Validate("28", name, "READ_ONLY", true));
    }

    [Fact]
    public void ExistingPilotRemainsSupportedButVersion31CannotWrite()
    {
        const string name = "GmcSmetaPilot_UnitTest";
        Assert.True(SqlSchemaCompatibility.IsWritablePilot(name));
        SqlSchemaCompatibility.Validate("28", name, null, false);
        Assert.Throws<InvalidOperationException>(() => SqlSchemaCompatibility.Validate("31", name, "READ_ONLY", true));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("tcp:localhost,1433")]
    [InlineData("tcp:127.0.0.1,1433")]
    [InlineData(@".\SQLEXPRESS")]
    public void OrdinarySchema31TestDatabaseIsWritableLocallyOnly(string server)
    {
        var name = SqlSchemaCompatibility.OrdinarySchema31TestDatabase;
        Assert.True(SqlSchemaCompatibility.IsOrdinarySchema31TestDatabase(name, server));
        Assert.True(SqlSchemaCompatibility.IsWritablePilot(name, server));
        Assert.True(SqlSchemaCompatibility.Schema31WritesAllowed(
            $"Server={server};Database={name};Integrated Security=true"));
        SqlSchemaCompatibility.Validate("31", name, "READ_WRITE", true, server);
        Assert.Throws<InvalidOperationException>(() =>
            SqlSchemaCompatibility.Validate("31", name, "READ_ONLY", true, server));
        Assert.Throws<InvalidOperationException>(() =>
            SqlSchemaCompatibility.Validate("31", name, "READ_WRITE", false, server));
        Assert.Throws<InvalidOperationException>(() =>
            SqlSchemaCompatibility.Validate("28", name, "READ_WRITE", true, server));
    }

    [Theory]
    [InlineData("tcp:192.168.1.46,1433")]
    [InlineData("localhost.example.com")]
    public void OrdinarySchema31TestDatabaseIsRejectedOnRemoteServers(string server)
    {
        var name = SqlSchemaCompatibility.OrdinarySchema31TestDatabase;
        Assert.False(SqlSchemaCompatibility.IsOrdinarySchema31TestDatabase(name, server));
        Assert.False(SqlSchemaCompatibility.Schema31WritesAllowed(name, server));
        Assert.Throws<InvalidOperationException>(() =>
            SqlSchemaCompatibility.Validate("31", name, "READ_WRITE", true, server));
    }

    [Fact]
    public void OtherPilotDoesNotGainOrdinarySchema31Writes()
    {
        const string name = "GmcSmetaPilot_UnitTest";
        Assert.False(SqlSchemaCompatibility.Schema31WritesAllowed(name, "localhost"));
        Assert.False(SqlSchemaCompatibility.Schema31WritesAllowed(
            $"Server=localhost;Database={name};Integrated Security=true"));
        Assert.False(SqlSchemaCompatibility.Schema31WritesAllowed(""));
    }
    [Theory]
    [InlineData("master", "localhost")]
    [InlineData("GmcSmetaPilot_Schema31WriteTest", "localhost")]
    [InlineData("GmcSmetaPilot_Cutover_20261120_160840", "tcp:192.168.1.46,1433")]
    public async Task AsyncOpenRejectsForbiddenCatalogBeforeNetwork(string name, string server)
    {
        var cs = $"Server={server};Database={name};Integrated Security=true";
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerDatabase.OpenAsync(cs, CancellationToken.None));
    }

    [Fact]
    public async Task AsyncOpenObservesPreCancelledTokenBeforeNetwork()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerDatabase.OpenAsync("Server=localhost;Database=GmcSmetaPilot_UnitTest;Integrated Security=true", cts.Token));
    }

}
