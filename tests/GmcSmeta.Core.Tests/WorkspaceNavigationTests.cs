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
