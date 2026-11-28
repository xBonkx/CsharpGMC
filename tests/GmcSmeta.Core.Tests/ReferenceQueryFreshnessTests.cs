using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class ReferenceQueryFreshnessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CatalogPageSelectsOnlyNeededProjections(bool includeUsage)
    {
        var query = SqlServerWorkspaceNavigation.CatalogPageSql(["клапан", "%"], includeUsage);
        Assert.Contains("WITH catalog_page AS (SELECT c.id,c.name,c.unit FROM dbo.catalog_items c WHERE ", query);
        var pageEnd = query.IndexOf("FETCH NEXT @take ROWS ONLY)", StringComparison.Ordinal);
        var priceLookup = query.IndexOf("FROM catalog_page c OUTER APPLY", StringComparison.Ordinal);
        Assert.True(pageEnd >= 0 && priceLookup > pageEnd);
        Assert.Contains("p.price,p.purchase_price", query);
        Assert.Contains("p.catalog_item_id=c.id AND p.price>0 ORDER BY p.id DESC", query);
        Assert.EndsWith("ORDER BY c.name,c.id", query);
        Assert.Contains("LIKE LOWER(@w0) ESCAPE N'~'", query);
        Assert.Contains("LIKE LOWER(@w1) ESCAPE N'~'", query);
        Assert.DoesNotContain("клапан", query);
        Assert.Equal(includeUsage, query.Contains("COUNT_BIG(*)", StringComparison.Ordinal));
        Assert.Equal(!includeUsage, query.Contains("CAST(0 AS bigint)", StringComparison.Ordinal));
    }

    [Fact]
    public void CompatibilityReadersDoNotBypassRevisionFence()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        const string relative = "src/GmcSmeta.Core/Db/SqlServerWorkspaceNavigation.cs";
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relative)))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, relative));
        Assert.Contains("CatalogSnapshot(connectionString, query, skip, take).Rows", source);
        Assert.Contains("ReadFreshReference(connectionString, ReadCompanies)", source);
        Assert.Contains("ReadFreshReference(connectionString, ReadTags)", source);
        var start = source.IndexOf("private static IReadOnlyList<T> ReadFreshReference<T>", StringComparison.Ordinal);
        var end = source.IndexOf("public static IReadOnlyList<CompanyEntry> ReadCompanies", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var helper = source[start..end];
        var first = helper.IndexOf("var before = SqlServerDatabase.Revision(conn)", StringComparison.Ordinal);
        var read = helper.IndexOf("var rows = read(conn)", StringComparison.Ordinal);
        var after = helper.IndexOf("before != SqlServerDatabase.Revision(conn)", StringComparison.Ordinal);
        Assert.True(first >= 0 && first < read && read < after);
        Assert.Contains("string.IsNullOrWhiteSpace(before)", helper);
        Assert.Contains("throw new DBConcurrencyException", helper);
    }
}
