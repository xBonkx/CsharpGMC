using GmcSmeta.Core.Db;
using GmcSmeta.RemoteSqlBench;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Tests;

public sealed class SqlDiagnosticQueriesTests
{
    [Theory]
    [InlineData("GmcSmetaPilot_Cutover_20261120_160840")]
    [InlineData("gmcSmetaPilot_cutover_other")]
    [InlineData("master")]
    [InlineData("Production")]
    public void DiagnosticsNeverAllowCutoverOrOtherCatalog(string name) =>
        Assert.Throws<InvalidOperationException>(() => SqlDiagnostics.RequireIsolatedCatalog(name));

    [Fact]
    public void IsolatedPilotCatalogIsAllowedWithoutConnecting() =>
        SqlDiagnostics.RequireIsolatedCatalog("GmcSmetaPilot_Bench_UnitTest");

    [Theory]
    [InlineData(10, false)]
    [InlineData(50, true)]
    [InlineData(200, true)]
    [InlineData(512, false)]
    public void DirectorReadAndOracleUseBoundScopes(int count, bool late)
    {
        using var conn = new SqlConnection();
        var ids = Enumerable.Range(1, count).Select(i => (long)i).ToArray();
        using var actual = SqlServerDirector.CreateContractMetricsReadCommand(conn, ids, late);
        using var oracle = SqlDiagnosticQueries.DirectorReferenceCommand(conn, ids, late);
        Assert.Equal(ids, actual.Parameters.Cast<SqlParameter>().Select(p => (long)p.Value));
        Assert.Equal(ids, oracle.Parameters.Cast<SqlParameter>().Select(p => (long)p.Value));
        Assert.Contains("movement_totals", actual.CommandText);
        Assert.Contains("SELECT SUM(m.qty)", oracle.CommandText);
        Assert.Equal(late, actual.CommandText.Contains("N'back_supplier'"));
        Assert.Equal(late, oracle.CommandText.Contains("N'back_supplier'"));
        Assert.Contains("p.parent_id IS NULL", actual.CommandText);
    }

    [Fact]
    public void InvalidDirectorScopeNeverConstructsCommand()
    {
        using var conn = new SqlConnection();
        foreach (var ids in new long[][] { [], [0], [1,1], Enumerable.Range(1,513).Select(i => (long)i).ToArray() })
            Assert.Throws<ArgumentException>(() => SqlServerDirector.CreateContractMetricsReadCommand(conn, ids, true));
    }

    [Fact]
    public void AllMappedColumnsAndFlagsParticipateInParity()
    {
        var row = new SqlServerProcurement.BuyRow(1, "S", "N", "шт", 2m, 3m, "none", "label",
            "bg", "fg", null, "", "", "", null, null, "", "", false, 0m, 0m, false, false, ["flag"]);
        Assert.True(SqlDiagnosticQueries.SameBuyRow(row, row with { Flags = ["flag"] }));
        Assert.False(SqlDiagnosticQueries.SameBuyRow(row, row with { Flags = ["other"] }));
        Assert.False(SqlDiagnosticQueries.SameBuyRow(row, row with { Supplier = "S" }));
        Assert.False(SqlDiagnosticQueries.SameBuyRow(row, row with { PositionOrder = 7 }));
        Assert.False(SqlDiagnosticQueries.SameBuyRow(row, row with { CostPrice = 0m }));
    }

    [Fact]
    public void OptionalStatisticsAndUsageInventoryRemainReadOnlyAndScoped()
    {
        foreach (var table in new[] { "systems","positions","contracts","movements","procurement","acts","act_lines","shipment_lines","spec_revisions" })
        {
            Assert.Contains("N'" + table + "'", SqlDiagnostics.IndexUsageSql);
            Assert.Contains("N'" + table + "'", SqlDiagnostics.StatisticsDetailsSql);
        }
        Assert.Contains("user_updates", SqlDiagnostics.IndexUsageSql);
        Assert.Contains("database_id=DB_ID()", SqlDiagnostics.IndexUsageSql);
        Assert.Contains("modification_counter", SqlDiagnostics.StatisticsDetailsSql);
        Assert.DoesNotContain("UPDATE STATISTICS", SqlDiagnostics.StatisticsDetailsSql);
    }
}
