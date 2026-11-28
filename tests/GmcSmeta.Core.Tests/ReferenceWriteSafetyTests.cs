using System.Data;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class ReferenceWriteSafetyTests
{
    private static string Source(string relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", relative)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", relative));
    }

    [Theory]
    [InlineData("CompanyWindow", "SaveCompany", "DeleteCompany", "ReadCompanies")]
    [InlineData("TagsWindow", "SaveTag", "DeleteTag", "ReadTags")]
    public void ReferenceWritesHoldExclusiveOperationAndDisplayedRevision(string window, string save, string delete, string read)
    {
        var all = Source("GmcSmeta.App/WorkspaceReferenceWindows.cs");
        var start = all.IndexOf("public sealed class " + window, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = all.IndexOf("public sealed class ", start + 1, StringComparison.Ordinal);
        var source = end < 0 ? all[start..] : all[start..end];
        Assert.Equal(3, source.Split("_operation.TryEnter(this)").Length - 1);
        Assert.Contains("if (_operation.IsBusy) { e.Cancel = true; return; }", source);
        Assert.Contains("var revision = _revision;", source);
        Assert.Contains(save + "(_connectionString,", source);
        Assert.Contains(delete + "(", source);
        Assert.Contains("await LoadCoreAsync(saved);", source);
        Assert.Contains("SqlServerWorkspaceNavigation." + read + "(conn)", source);
        Assert.Contains("string.IsNullOrWhiteSpace(before)", source);
        Assert.DoesNotContain("MessageBoxButton.YesNo, MessageBoxImage.Warning)", source);
        Assert.Contains("Запись не подтверждена.", source);
    }

    [Fact]
    public void LegalCompanyReferenceIsIncludedAndDeletionChecksItBeforeDelete()
    {
        var read = Source("GmcSmeta.Core/Db/SqlServerWorkspaceNavigation.cs");
        var write = Source("GmcSmeta.Core/Db/SqlServerWorkspaceReferenceWrites.cs");
        Assert.Contains("FROM dbo.agreements WHERE company_id=c.id", read);
        Assert.Contains("SqlServerAgreements.Enabled(conn, tx)", write);
        var check = write.IndexOf("FROM dbo.agreements WITH (UPDLOCK,HOLDLOCK) WHERE company_id=@id", StringComparison.Ordinal);
        var delete = write.IndexOf("DELETE dbo.companies WHERE id=@id", StringComparison.Ordinal);
        Assert.True(check >= 0 && delete > check);
    }

    [Fact]
    public void CompanyMapperKeepsLegalAndDocumentCountsSeparate()
    {
        using var table = new DataTable();
        Type[] types = [typeof(long), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(double), typeof(string), typeof(string), typeof(string), typeof(int), typeof(long), typeof(byte[]), typeof(long)];
        for (var i = 0; i < types.Length; i++) table.Columns.Add("c" + i, types[i]);
        table.Rows.Add(42L, "Организация", "", "", "", "", 22d, "", "", "", 3, 0L, DBNull.Value, 2L);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        var company = SqlServerWorkspaceNavigation.ReadCompany(reader);
        Assert.Equal(0L, company.Uses);
        Assert.Equal(2L, company.AgreementUses);
    }
}
