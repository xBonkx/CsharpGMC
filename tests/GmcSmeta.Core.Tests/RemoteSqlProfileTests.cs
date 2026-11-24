using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Tests;

public sealed class RemoteSqlProfileTests
{
    [Fact]
    public void Profile_UsesExplicitTcpPortAndEncryptedConnection()
    {
        var profile = NewProfile();
        var cs = new SqlConnectionStringBuilder(profile.ConnectionString("temporary-secret"));
        Assert.Equal("tcp:192.168.1.10,15433", cs.DataSource);
        Assert.Equal("GmcSmetaPilot_Laptop", cs.InitialCatalog);
        Assert.False(cs.IntegratedSecurity);
        Assert.Equal("gmc_laptop", cs.UserID);
        Assert.Equal("temporary-secret", cs.Password);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, cs.Encrypt);
        Assert.False(cs.TrustServerCertificate);
        Assert.False(cs.PersistSecurityInfo);
    }

    [Fact]
    public void WindowsAuth_DoesNotEmbedPasswordOrUser()
    {
        var profile = NewProfile() with { WindowsAuthentication = true, UserName = "ignored" };
        var cs = new SqlConnectionStringBuilder(profile.ConnectionString("not-used"));
        Assert.True(cs.IntegratedSecurity);
        Assert.True(string.IsNullOrEmpty(cs.Password));
        Assert.True(string.IsNullOrEmpty(cs.UserID));
    }

    [Theory]
    [InlineData("GmcSmeta")]
    [InlineData("master")]
    [InlineData("GmcSmetaPilot_")]
    [InlineData("Production")]
    public void Profile_RejectsNonPilotDatabase(string database) =>
        Assert.Throws<ArgumentException>(() =>
            (NewProfile() with { Database = database }).ConnectionString("secret"));

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Profile_RejectsBadPorts(int port) =>
        Assert.Throws<ArgumentException>(() =>
            (NewProfile() with { Port = port }).ConnectionString("secret"));

    [Fact]
    public void Profile_RejectsConnectionStringInjectionInServer() =>
        Assert.Throws<ArgumentException>(() =>
            (NewProfile() with { Server = "localhost;TrustServerCertificate=true" }).Validate());

    [Fact]
    public void Profile_RequiresSqlUserForSqlAuthentication() =>
        Assert.Throws<ArgumentException>(() =>
            (NewProfile() with { UserName = "" }).Validate());

    [Fact]
    public void Check_DoesNotReportSuccessWhenOnlyTcpWorks()
    {
        var check = new RemoteSqlCheck(true, false, false, "authentication failed", 4, 10);
        Assert.False(check.Success);
    }

    [Fact]
    public void Dpapi_IsWindowsUserScopedAndNotPlainText()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protectedValue = RemoteSqlProfileStore.ProtectPassword("temporary-secret");
        Assert.DoesNotContain("temporary-secret", protectedValue);
        Assert.Equal("temporary-secret", RemoteSqlProfileStore.UnprotectPassword(protectedValue));
    }

    private static RemoteSqlProfile NewProfile() => new()
    {
        Name = "Дом",
        Server = "192.168.1.10",
        Port = 15433,
        Database = "GmcSmetaPilot_Laptop",
        UserName = "gmc_laptop"
    };
}
