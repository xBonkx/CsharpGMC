using System.Data;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SqlIntegerTests
{
    [Theory]
    [InlineData(typeof(byte))]
    [InlineData(typeof(short))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    public void ReadsAllSqlIntegerWidths(Type type)
    {
        using var table = new DataTable();
        table.Columns.Add("value", type);
        table.Rows.Add(Convert.ChangeType(17, type));
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        Assert.Equal(17, SqlInteger.ReadInt32(reader, 0));
        Assert.Equal(17L, SqlInteger.ReadInt64(reader, 0));
    }

    [Theory]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData((long)int.MinValue - 1)]
    public void BigIntIdsArePreservedAndNarrowingRejectsOverflow(long value)
    {
        using var table = new DataTable();
        table.Columns.Add("value", typeof(long));
        table.Rows.Add(value);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        Assert.Equal(value, SqlInteger.ReadInt64(reader, 0));
        Assert.Throws<OverflowException>(() => SqlInteger.ReadInt32(reader, 0));
    }

    [Fact]
    public void BoxedSnapshotIdsCanBeIntOrBigInt()
    {
        Assert.Equal(42L, SqlInteger.RequireInt64(42));
        Assert.Equal(42L, SqlInteger.RequireInt64(42L));
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt64(null));
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt64(DBNull.Value));
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt64(42.5m));
    }

    [Theory]
    [InlineData(true)]
    [InlineData("17")]
    [InlineData(17.0)]
    public void RequiredIntegerRejectsCoercionRatherThanRoundingOrParsing(object value)
    {
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt64(value));
    }

    [Fact]
    public void RequiredIntegerRejectsDecimalEvenWhenWhole()
    {
        Assert.Throws<InvalidCastException>(() => SqlInteger.RequireInt64(17m));
    }

    [Fact]
    public void MixedWidthReaderPreservesNullableParentAndLargeReference()
    {
        using var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("parent", typeof(long));
        table.Columns.Add("order", typeof(short));
        table.Columns.Add("object", typeof(long));
        table.Rows.Add(42, DBNull.Value, (short)9, (long)int.MaxValue + 10);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        Assert.Equal(42, SqlInteger.ReadInt32(reader, 0));
        Assert.Null(reader.IsDBNull(1) ? (int?)null : SqlInteger.ReadInt32(reader, 1));
        Assert.Throws<InvalidCastException>(() => SqlInteger.ReadInt32(reader, 1));
        Assert.Equal(9, SqlInteger.ReadInt32(reader, 2));
        Assert.Equal((long)int.MaxValue + 10, SqlInteger.ReadInt64(reader, 3));
    }

    [Theory]
    [InlineData("GmcSmeta.Core/Domain/Estimate.cs", "SqlInteger.ReadInt32(r, 0)")]
    [InlineData("GmcSmeta.Core/Domain/EstimateReader.Cards.cs", "SqlInteger.ReadInt32(reader, 8)")]
    [InlineData("GmcSmeta.Core/Domain/Finance.cs", "SqlInteger.ReadInt32(r, i)")]
    [InlineData("GmcSmeta.Core/Domain/Finance.Acts.cs", "SqlInteger.ReadInt64(r, 14)")]
    [InlineData("GmcSmeta.Core/Domain/Totals.cs", "SqlInteger.ReadInt32(r, 1)")]
    [InlineData("GmcSmeta.Core/Domain/Totals.SqlSums.cs", "SqlInteger.ReadInt32(reader, 0)")]
    [InlineData("GmcSmeta.Core/Domain/Workspace.cs", "SqlInteger.ReadInt32(reader, 11)")]
    [InlineData("GmcSmeta.App/LetterEditorWindow.xaml.cs", "SqlInteger.ReadInt64(r, 0)")]
    public void SharedReadersUseStrictIntegerPolicy(string relative, string expected)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", relative)))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", relative));
        Assert.Contains(expected, source);
        Assert.DoesNotContain("Convert.ToInt32(r.GetValue(", source);
        Assert.DoesNotContain("Convert.ToInt32(reader.GetValue(", source);
    }


    [Theory]
    [InlineData("GmcSmeta.Core/Db/SqlServerActs.Extras.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerActs.ActList.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerActs.Grid.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerAgreements.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerProcurement.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerStages.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerTrash.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerShipments.cs")]
    public void SqlServerProjectionUsesStrictIntegerAndFlagReads(string relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", relative)))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", relative));
        Assert.Contains("SqlInteger.ReadInt", source);
        Assert.DoesNotContain("Convert.ToInt64(r.GetValue(", source);
        Assert.DoesNotContain("Convert.ToInt64(reader.GetValue(", source);
        Assert.DoesNotContain("Convert.ToInt32(r.GetValue(", source);
        Assert.DoesNotContain("Convert.ToBoolean(r.GetValue(", source);
        Assert.DoesNotContain("Convert.ToBoolean(reader.GetValue(", source);
    }

    [Fact]
    public void ShipmentBigIntCountCannotWrapToNegative()
    {
        using var table = new DataTable();
        table.Columns.Add("count", typeof(long));
        table.Rows.Add((long)int.MaxValue + 1);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        Assert.Throws<OverflowException>(() => SqlInteger.ReadInt32(reader, 0));
    }
    [Theory]
    [InlineData("GmcSmeta.Core/Db/SqlServerPtoBackup.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerPto.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerLettersBackup.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerLetters.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerPassports.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerMetalWarehouse.Reads.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerMetalWarehouse.Writes.cs")]
    [InlineData("GmcSmeta.Core/Db/ProcurementRequestPlanner.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerExcelExistingUpdate.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerSmrMode.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerActs.Schema31.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerCostOperations.Batch.cs")]
    public void RemainingSqlServerReadersUseWidthIndependentPolicy(string relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", relative)))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", relative));
        Assert.True(source.Contains("SqlInteger.ReadInt") || source.Contains("SqlFlag.Read"));
        Assert.DoesNotMatch(@"\b(?:r|reader)\.Get(?:Int32|Int64|Boolean)\(", source);
    }

    [Theory]
    [InlineData("GmcSmeta.Core/Db/SqlServerSystemCopy.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerSectionActions.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerRateTransfer.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerDatabase.DragDrop.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerStages.Safety.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerWorkspace.ContractTransfer.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerStages.Links.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerCostTotals.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerCostSummary.cs")]
    [InlineData("GmcSmeta.Core/Db/SqlServerHistory.cs")]
    public void TransactionAndNavigationReadersUseStrictSqlTypes(string relative)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", relative)))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", relative));
        Assert.True(source.Contains("SqlInteger.ReadInt") || source.Contains("SqlFlag.Read"));
        Assert.DoesNotMatch(@"Convert\.To(?:Int32|Int64|Boolean)\(r\.GetValue\(", source);
    }
}
