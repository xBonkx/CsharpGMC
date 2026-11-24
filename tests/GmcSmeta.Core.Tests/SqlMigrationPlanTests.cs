using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SqlMigrationPlanTests
{
    [Theory]
    [InlineData(28,29,"migrate_discount_v29.sql")]
    [InlineData(28,30,"migrate_ks_sections_v30.sql")]
    [InlineData(29,30,"migrate_ks_sections_v30.sql")]
    [InlineData(28,31,"migrate_ks_discount_v31.sql")]
    [InlineData(29,31,"migrate_ks_discount_v31.sql")]
    [InlineData(30,31,"migrate_ks_discount_v31.sql")]
    [InlineData(31,31,null)]
    public void RoutesMatchPython(int from,int to,string? script) => Assert.Equal(script,SqlMigrationPlan.Script(from,to));

    [Theory]
    [InlineData(27,31)]
    [InlineData(31,28)]
    [InlineData(31,32)]
    public void UnknownOrDowngradeBlocked(int from,int to) =>
        Assert.Throws<InvalidOperationException>(() => SqlMigrationPlan.Script(from,to));

    [Theory]
    [InlineData("master")]
    [InlineData("GmcSmetaPilot_Cutover_20261120_160840")]
    [InlineData("GmcSmetaPilot_Test_Csharp;DROP")]
    [InlineData("GmcSmetaPilot_Schema31WriteTest")]
    public void ForbiddenSourceBlocked(string name) =>
        Assert.Throws<InvalidOperationException>(() => SqlMigrationPlan.RequireRehearsalSource(name));

    [Theory]
    [InlineData("GmcSmetaPilot_Test_Migration28")]
    [InlineData("GmcSmetaPilot_ReleaseCheck_123")]
    public void ExplicitPilotAccepted(string name) => SqlMigrationPlan.RequireRehearsalSource(name);
}
