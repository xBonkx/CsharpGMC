namespace GmcSmeta.Core.Tests;

public sealed class SqlScalarWritePolicyTests
{
    [Theory]
    [InlineData("SqlServerSmrExpenses.cs")]
    [InlineData("SqlServerExcelImport.cs")]
    [InlineData("SqlServerPositions.BulkDelete.cs")]
    [InlineData("SqlServerMetalWarehouse.Writes.cs")]
    [InlineData("SqlServerGroupEdit.cs")]
    [InlineData("SqlServerWorkspace.DeletePreview.cs")]
    [InlineData("SqlServerWorkspaceReferenceWrites.cs")]
    [InlineData("SqlServerProcurement.Extras.cs")]
    public void RequiredScalarDoesNotSilentlyCoerce(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GmcSmeta.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db", name));
        Assert.DoesNotMatch(@"Convert\.ToInt(?:32|64)\(\w+\.ExecuteScalar\(\)\s*[,)]", source);
        Assert.Contains("SqlInteger.RequireInt64(", source);
    }

    [Fact]
    public void DeletePreviewUsesCheckedCountNarrowing()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GmcSmeta.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerWorkspace.DeletePreview.cs"));
        Assert.Contains("acts = checked((int)SqlInteger.RequireInt64(cmd.ExecuteScalar()))", source);
        Assert.Contains("movements = checked((int)SqlInteger.RequireInt64(cmd.ExecuteScalar()))", source);
    }
}
