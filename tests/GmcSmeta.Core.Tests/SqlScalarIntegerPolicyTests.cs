using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SqlScalarIntegerPolicyTests
{
    [Theory]
    [InlineData("SqlServerSections.cs")]
    [InlineData("SqlServerAgreements.cs")]
    [InlineData("SqlServerSystemCopy.cs")]
    [InlineData("SqlServerPtoBackup.cs")]
    [InlineData("SqlServerAttachments.cs")]
    [InlineData("SqlServerPto.cs")]
    [InlineData("SqlServerPositions.cs")]
    [InlineData("SqlServerPassports.cs")]
    public void ReviewedRequiredScalarResultsAreNotCoerced(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GmcSmeta.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db", name));
        Assert.DoesNotMatch(@"Convert\.ToInt(?:32|64)\(\w+\.ExecuteScalar\(\)", source);
        Assert.Matches(@"SqlInteger\.RequireInt64\(\w+\.ExecuteScalar\(\)\)", source);
    }

    [Fact]
    public void MissingScalarIsNotSuccessfulLockOrEmptyCount()
    {
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt64(null));
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt64(DBNull.Value));
        Assert.Equal(0L, SqlInteger.RequireInt64(0));
        Assert.Equal(-1L, SqlInteger.RequireInt64(-1));
        Assert.Equal(long.MaxValue, SqlInteger.RequireInt64(long.MaxValue));
        Assert.Throws<OverflowException>(() => { _ = checked((int)SqlInteger.RequireInt64(long.MaxValue)); });
    }

    [Fact]
    public void RequiredInt32ScalarRejectsMissingAndForeignValues()
    {
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt32(null));
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt32(DBNull.Value));
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt32(42.5m));
        Assert.Equal(-1, SqlInteger.RequireInt32(-1));
        Assert.Equal(int.MaxValue, SqlInteger.RequireInt32(int.MaxValue));
        Assert.Throws<OverflowException>(() => SqlInteger.RequireInt32((long)int.MaxValue + 1));
    }

    [Theory]
    [InlineData("SqlServerActs.cs")]
    [InlineData("SqlServerActs.Extras.cs")]
    [InlineData("SqlServerActs.Schema31ContractDiscount.cs")]
    [InlineData("SqlServerCostOperations.Batch.cs")]
    [InlineData("SqlServerCostSummary.cs")]
    [InlineData("SqlServerDatabase.cs")]
    [InlineData("SqlServerDatabase.AreaCollapse.cs")]
    [InlineData("SqlServerEditorOps.cs")]
    [InlineData("SqlServerExcelAppend.cs")]
    [InlineData("SqlServerExcelExistingUpdate.cs")]
    [InlineData("SqlServerExcelExistingUpdate.Undo.cs")]
    [InlineData("SqlServerLetters.cs")]
    [InlineData("SqlServerLettersBackup.cs")]
    [InlineData("SqlServerMetalLegacyImport.cs")]
    [InlineData("SqlServerMetalWarehouse.cs")]
    [InlineData("SqlServerMetalWarehouse.Reads.cs")]
    [InlineData("SqlServerMetalWarehouse.Writes.cs")]
    [InlineData("SqlServerPaste.cs")]
    [InlineData("SqlServerPeopleAccess.cs")]
    [InlineData("SqlServerPriceLike.cs")]
    [InlineData("SqlServerProcurement.cs")]
    [InlineData("SqlServerProcurement.BulkExtras.cs")]
    [InlineData("SqlServerProcurement.Movements.cs")]
    [InlineData("SqlServerProcurement.SheetImport.cs")]
    [InlineData("SqlServerShipmentPrintData.cs")]
    [InlineData("SqlServerShipments.cs")]
    [InlineData("SqlServerSpecUpdate.cs")]
    [InlineData("SqlServerStages.cs")]
    [InlineData("SqlServerTrash.cs")]
    [InlineData("SqlServerTrashRestore.cs")]
    [InlineData("SqlServerWorkspace.cs")]
    [InlineData("SqlServerWorkspace.ContractTransfer.cs")]
    [InlineData("RowWriter.cs")]
    [InlineData("TrashRestoreReconciliation.cs")]
    [InlineData("MigrationFinanceSnapshot.cs")]
    public void RemainingExecuteScalarSitesAreStrict(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "GmcSmeta.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db", name));
        Assert.DoesNotMatch(@"Convert\.To(?:Int32|Int64)\(\w+\.ExecuteScalar", source);
        Assert.DoesNotMatch(@"\((?:int|long)\)\w+\.ExecuteScalar", source);
        Assert.True(source.Contains("SqlInteger.RequireInt64(")
            || source.Contains("SqlInteger.RequireInt32(")
            || source.Contains("SqlFlag.Require("),
            $"{name} has no strict scalar helper");
    }

    [Fact]
    public void NullableScalarBranchesStayExplicit()
    {
        Assert.All(new (string Relative, string Needle)[]
        {
            ("src/GmcSmeta.Core/Db/SqlServerStages.cs", "SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? contractId)"),
            ("src/GmcSmeta.Core/Db/SqlServerStages.cs", "SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? 0L)"),
            ("src/GmcSmeta.Core/Db/SqlServerShipments.cs", "SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? 0L)"),
            ("src/GmcSmeta.Core/Db/SqlServerMetalWarehouse.Writes.cs", "SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? 0L)"),
            ("src/GmcSmeta.Core/Domain/Documents.cs", "SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? 1L)"),
            ("src/GmcSmeta.App/ProcurementPane.xaml.cs", "SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? 0L)"),
            ("src/GmcSmeta.App/RateTransferDialog.xaml.cs", "checked((int)SqlInteger.RequireInt64(read.ExecuteScalar() ?? 0L))"),
            ("src/GmcSmeta.Cli/SqlLive.cs", "SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? 0L)"),
        }, entry =>
        {
            DirectoryInfo? root = new(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "GmcSmeta.slnx")))
                root = root.Parent;
            Assert.NotNull(root);
            Assert.Contains(entry.Needle, File.ReadAllText(Path.Combine(root!.FullName, entry.Relative)));
        });
    }
}
