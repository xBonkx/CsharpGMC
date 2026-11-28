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

    [Theory]
    [InlineData("--DEMO")]
    [InlineData("--Demo")]
    [InlineData("--demo-launch")]
    public void MalformedDemoSwitchIsDetectedAndRejected(string flag)
    {
        Assert.True(DemoMode.IsRequested([flag, Db]));
        Assert.Throws<InvalidOperationException>(() => DemoMode.DatabaseFromArgs([flag, Db]));
        Assert.Throws<InvalidOperationException>(() => DemoMode.ValidateConnection(
            new SqlConnectionStringBuilder(DemoMode.CreateConnectionString(Db)), [flag, Db]));
    }

    [Fact]
    public void DemoProfileRequiresAbsoluteLocalRoot()
    {
        var local = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "demo-local"));
        Assert.Equal(Path.Combine(local, "GMC-Smeta-Demo", "CSharp", Db), DemoMode.ResolveProfileRoot(local, Db));
        Assert.Throws<InvalidOperationException>(() => DemoMode.ResolveProfileRoot("", Db));
        Assert.Throws<InvalidOperationException>(() => DemoMode.ResolveProfileRoot("relative", Db));
        Assert.Throws<InvalidOperationException>(() => DemoMode.ResolveProfileRoot(local, "master"));
        Assert.NotEqual(DemoMode.ResolveProfileRoot(local, Db),
            DemoMode.ResolveProfileRoot(local, DemoMode.Prefix + "abcdef123456"));
    }

    [Fact]
    public void DemoCannotUseFailoverPartnerOrAttachedDatabase()
    {
        var builder = new SqlConnectionStringBuilder(DemoMode.CreateConnectionString(Db));
        builder.FailoverPartner = "another-server";
        Assert.Throws<InvalidOperationException>(() => DemoMode.ValidateConnection(builder, ["--demo", Db]));
        builder.FailoverPartner = "";
        builder.AttachDBFilename = "foreign.mdf";
        Assert.Throws<InvalidOperationException>(() => DemoMode.ValidateConnection(builder, ["--demo", Db]));
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

    [Fact]
    public void InstalledDemoExecutableWithoutArgumentsCannotBecomeOfficeApp()
    {
        var local = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "local"));
        var root = DemoMode.ResolveProfileRoot(local, Db);
        var executable = Path.Combine(root, "launcher", "apps", "1.2.3-sc", "GMC-Smeta-Next.exe");
        Assert.Throws<InvalidOperationException>(() => DemoMode.RequireExplicitLaunch([], local, executable));
        Assert.Throws<InvalidOperationException>(() => DemoMode.RequireExplicitLaunch(["--smoke-test"], local, executable));
        DemoMode.RequireExplicitLaunch(["--demo", Db], local, executable);
        DemoMode.RequireExplicitLaunch([], local, Path.Combine(local, "GMC-Smeta-Office", "apps", "1.2.3-sc", "GMC-Smeta-Next.exe"));
        DemoMode.RequireExplicitLaunch([], local, Path.Combine(local, "GMC-Smeta-Demo", "CSharp-other", "GMC-Smeta-Next.exe"));
    }

    [Fact]
    public void DemoWorkspacePathCheckRejectsRelativeAndSiblingPaths()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "demo-workspace"));
        Assert.True(DemoMode.IsWithin(root, root));
        Assert.True(DemoMode.IsWithin(root, Path.Combine(root, "sub", "example.xlsx")));
        Assert.False(DemoMode.IsWithin(root, root + "-office"));
        Assert.False(DemoMode.IsWithin(root, Path.Combine(root, "..", "office")));
        Assert.False(DemoMode.IsWithin(root, "relative"));
        Assert.False(DemoMode.IsWithin(root, null));
    }

    [Fact]
    public void StartupConfiguresDemoBeforeApplicationOrTraceInitialization()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        const string relative = "src/GmcSmeta.App/StartupEntryPoint.cs";
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relative)))
            root = root.Parent;
        Assert.NotNull(root);
        var entry = File.ReadAllText(Path.Combine(root!.FullName, relative));
        var guard = entry.IndexOf("DemoMode.RequireExplicitLaunch", StringComparison.Ordinal);
        var configure = entry.IndexOf("DemoMode.ConfigureProcess(args)", StringComparison.Ordinal);
        var trace = entry.IndexOf("StartupTrace.Mark", StringComparison.Ordinal);
        var application = entry.IndexOf("app = new App()", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < configure && configure < trace && trace < application);
        Assert.Contains("MethodImplOptions.NoInlining", entry);
        var demo = File.ReadAllText(Path.Combine(root.FullName, "src/GmcSmeta.Core/Demo/DemoMode.cs"));
        Assert.Contains("Environment.SetEnvironmentVariable(\"GMC_SMETA_STARTUP_TRACE_DIR\", null)", demo);
        var config = File.ReadAllText(Path.Combine(root.FullName, "src/GmcSmeta.Core/AppConfig.cs"));
        Assert.Contains("GmcSmeta.Core.Demo.DemoMode.IsWithin(docs, demoValue.GetString())", config);
    }

}
