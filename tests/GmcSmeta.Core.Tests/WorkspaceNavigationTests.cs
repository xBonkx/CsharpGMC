using System.Text.Json;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class WorkspaceNavigationTests
{
    [Fact]
    public void SearchWordsPreserveCyrillicAndOrder()
    {
        Assert.Equal(["клапан", "500", "250"], SqlServerWorkspaceNavigation.Words("  клапан 500 250 "));
        Assert.Empty(SqlServerWorkspaceNavigation.Words("  "));
    }

    [Theory]
    [InlineData("клапан", "%клапан%")]
    [InlineData("100%_[~", "%100~%~_~[~~%")]
    public void SearchEscapesSqlWildcards(string source, string expected) =>
        Assert.Equal(expected, SqlServerWorkspaceNavigation.LikePattern(source));

    [Fact]
    public void SearchLimitsIndependentWordsBeforeSendingSql()
    {
        var words = SqlServerWorkspaceNavigation.Words("1 2 3 4 5 6 7 8");
        Assert.Equal(6, words.Length);
        Assert.Equal(80, SqlServerWorkspaceNavigation.Words(new string('a', 250))[0].Length);
    }

    [Theory]
    [InlineData("ДНС / Р4", "днс_р4")]
    [InlineData("  ", "tag")]
    [InlineData("Customer 123", "customer_123")]
    public void TagKeyMatchesPythonNaming(string name, string expected) =>
        Assert.Equal(expected, SqlServerWorkspaceReferenceWrites.TagKey(name));

    [Fact]
    public void TagRuleValidationAcceptsOnlyKnownFlags()
    {
        var rules = SqlServerWorkspaceReferenceWrites.NormalizeRules(
            "{\"vat_top\":true,\"basis\":true}");
        using var json = JsonDocument.Parse(rules);
        Assert.True(json.RootElement.GetProperty("vat_top").GetBoolean());
        Assert.True(json.RootElement.GetProperty("basis").GetBoolean());
        Assert.False(json.RootElement.GetProperty("ks_dns").GetBoolean());
        Assert.Equal(6, json.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void InvalidTagRulesDoNotReachSql()
    {
        Assert.Throws<ArgumentException>(() =>
            SqlServerWorkspaceReferenceWrites.NormalizeRules("{bad"));
        Assert.Throws<ArgumentException>(() =>
            SqlServerWorkspaceReferenceWrites.NormalizeRules("{\"erase_audit\":true}"));
    }

    [Theory]
    [InlineData(12, "Д-77", "28.11.2026", "Монтаж", "Договор № Д-77 от 28.11.2026 — Монтаж")]
    [InlineData(14, "", "", "", "Договор без номера (ID 14)")]
    public void LegalSearchCaptionPreservesIndependentNumber(long id, string number, string date, string subject, string expected) =>
        Assert.Equal(expected, SqlServerWorkspaceNavigation.AgreementSearchTitle(id, number, date, subject));

    [Fact]
    public void LegalHitCarriesAgreementIdentityNotAnAnnex()
    {
        var hit = new SqlServerWorkspaceNavigation.SearchHit("agreement", 17, 0, 0, "Договор № Д-77", "Объект")
            { AgreementId = 12345678901L };
        Assert.Equal("Юридический договор", hit.KindTitle);
        Assert.Equal(17, hit.ObjectId);
        Assert.Equal(0, hit.ContractId);
        Assert.Equal(12345678901L, hit.AgreementId);
    }

    [Fact]
    public async Task GlobalSearchRejectsForbiddenDatabaseBeforeOpening()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqlServerWorkspaceNavigation.SearchAsync(
            "Server=localhost;Database=master;Integrated Security=true", "Договор"));
    }

    [Fact]
    public void LegalSearchUsesExactIdAndActiveSchemaGuard()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GmcSmeta.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string Read(string path) => File.ReadAllText(Path.Combine(root!.FullName, path));
        var sql = Read("src/GmcSmeta.Core/Db/SqlServerWorkspaceNavigation.cs");
        Assert.Contains("if (legalEnabled)", sql);
        Assert.Contains("a.archived=0 AND o.archived=0", sql);
        Assert.Contains("AgreementId = id", sql);
        Assert.Contains("SqlServerDatabase.OpenAsync(connectionString, cancellationToken)", sql);
        var route = Read("src/GmcSmeta.App/MainWindow.Navigation.cs");
        Assert.Contains("OpenAgreementCard(target, legalId);", route);
        var page = Read("src/GmcSmeta.App/AgreementCardView.xaml.cs");
        Assert.Contains("await LoadCard(_initialLegalId);", page);
        var read = Read("src/GmcSmeta.Core/Db/SqlServerWorkspace.AgreementCard.cs");
        Assert.Contains("if (legalId is > 0 && selected is null)", read);
        Assert.True(read.IndexOf("var legalCompanies =", StringComparison.Ordinal) <
            read.IndexOf("revision == SqlServerDatabase.Revision(conn)", StringComparison.Ordinal));
    }

    [Fact]
    public void SearchPresentationDoesNotChangeNavigationIds()
    {
        var hit = new SqlServerWorkspaceNavigation.SearchHit(
            "position", 17, 25, 123456, "Клапан", "Объект › КП › Система");
        Assert.Equal("Позиция", hit.KindTitle);
        Assert.Equal(17, hit.ObjectId);
        Assert.Equal(25, hit.ContractId);
        Assert.Contains("Клапан", hit.Display);
    }
}
