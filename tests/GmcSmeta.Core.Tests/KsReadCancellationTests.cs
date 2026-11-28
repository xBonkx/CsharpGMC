using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class KsReadCancellationTests
{
    [Fact]
    public void CancelledReportsAndExportDoNotOpenSql()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        Assert.ThrowsAny<OperationCanceledException>(() => SqlServerKsContractScope.Read("", 1, token));
        Assert.ThrowsAny<OperationCanceledException>(() => SqlServerKsContractScope.ReadOverview("", 1, token));
        Assert.ThrowsAny<OperationCanceledException>(() => SqlServerKsRequisites.ReadSnapshot("", 1, token));
        Assert.ThrowsAny<OperationCanceledException>(() => SqlServerActs.Ks3Figures("", 1, token));
        Assert.ThrowsAny<OperationCanceledException>(() => SqlServerKsPrintData.Read("", 1, "1", token));
        Assert.ThrowsAny<OperationCanceledException>(() => SqlServerKsPrintData.ReadSingle("", 1, 1, "1", token));
        Assert.ThrowsAny<OperationCanceledException>(() => SqlServerKsPrintData.ReadAll("", 1, "1", token));
    }

    [Theory]
    [InlineData("ActsEditorView.Loading.cs", "common.Revision == before")]
    [InlineData("ActsEditorView.Loading.cs", "Totals.ReadOneWithRules(conn, contractId, token)")]
    [InlineData("ActsEditorView.Loading.cs", "RememberReadContext(loaded)")]
    [InlineData("ActsEditorView.xaml.cs", "selectedActId ??= 0")]
    [InlineData("ContractActsWorkspace.xaml.cs", "cached.Revision == before")]
    [InlineData("ContractActsWorkspace.xaml.cs", "common?.Acts, rules, totals?.Form")]
    [InlineData("ActsEditorView.Export.cs", "await Task.Run(() => read(token), token)")]
    [InlineData("ActsEditorView.Loading.cs", "_actExportCancellation?.Cancel()")]
    [InlineData("AllContractActsWindow.xaml.cs", "ReadOverview(_connectionString, _contractId, token)")]
    [InlineData("Ks3ReportWindow.xaml.cs", "Ks3Figures(_connectionString, _actId, token)")]
    [InlineData("KsGroupReportWindow.xaml.cs", "_connectionString,_contractId,_groupKey, token")]
    [InlineData("KsRequisitesWindow.xaml.cs", "ReadSnapshot(_connectionString, _contractId, token)")]
    public void SharedContextAndCancellationAreConnectedToTheUi(string file, string expected)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        Assert.Contains(expected, File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file)));
    }

    [Fact]
    public void DocumentBatchGuardsRunBeforeSqlAndKeepParameterBudgetBounded()
    {
        using var conn = new Microsoft.Data.SqlClient.SqlConnection();
        Assert.Empty(SqlServerActs.ActsOfDocuments(conn, Array.Empty<long>(), null, default));
        Assert.Throws<ArgumentException>(() => SqlServerActs.ActsOfDocuments(conn, new long[] { 1, 1 }, null, default));
        Assert.Throws<ArgumentException>(() => SqlServerActs.ActsOfDocuments(conn, new long[] { -1 }, null, default));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqlServerActs.ActsOfDocuments(conn, new long[] { 1 }, null, cancellation.Token));
        Assert.InRange(SqlServerActs.ActDocumentBatchSize, 1, 500);
    }

    [Fact]
    public void ScopeUsesBoundedDocumentBatchAndPreservesRowContractIdentity()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerActs.ActList.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var db = Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db");
        var list = File.ReadAllText(Path.Combine(db, "SqlServerActs.ActList.cs"));
        var scope = File.ReadAllText(Path.Combine(db, "SqlServerKsContractScope.cs"));
        Assert.Contains("contractIds.Chunk(ActDocumentBatchSize)", list);
        Assert.Contains("a.ContractId, \"\", sum.Ks3Net", list);
        Assert.Contains("SqlInteger.ReadInt64(r, 21)", list);
        Assert.Contains("SqlServerActs.ActsOfDocuments(conn, scopeDocs.Select(d => d.Id).ToArray()", scope);
        Assert.Contains("SourceName = labels[act.ContractId]", scope);
    }
}
