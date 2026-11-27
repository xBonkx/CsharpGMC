using System.Data;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SqlOpenValidationBatchTests
{
    [Theory]
    [InlineData("31", "READ_ONLY", 1, true)]
    [InlineData("31", "READ_WRITE", 1, false)]
    [InlineData("31", "READ_ONLY", 0, false)]
    [InlineData("28", "READ_ONLY", 1, false)]
    [InlineData(null, "READ_ONLY", 1, false)]
    public void PreviewResultStillRequiresCorrectVersionReadOnlyAndColumns(
        string? version, string state, int columns, bool allowed)
    {
        var table = new DataTable();
        table.Columns.Add("version", typeof(string));
        table.Columns.Add("updateability", typeof(string));
        table.Columns.Add("columns", typeof(int));
        table.Rows.Add((object?)version ?? DBNull.Value, state, columns);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        if (allowed)
            SqlServerDatabase.ValidatePreviewResult(reader, SqlSchemaCompatibility.PreviewDatabase, "localhost");
        else
            Assert.Throws<InvalidOperationException>(() =>
                SqlServerDatabase.ValidatePreviewResult(reader, SqlSchemaCompatibility.PreviewDatabase, "localhost"));
    }

    [Fact]
    public void BothOpenPathsKeepSessionAndIdentityChecks()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerDatabase.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerDatabase.cs"));
        Assert.Equal(2, source.Split("cmd.CommandText = PreviewValidationSql;").Length - 1);
        Assert.Equal(2, source.Split("ValidatePreviewResult(reader, conn.Database, builder.DataSource);").Length - 1);
        Assert.Equal(2, source.Split("identity.CommandText = CutoverIdentitySql;").Length - 1);
        Assert.Equal(2, source.Split("SqlSchemaCompatibility.RejectProduction(conn.Database, builder.DataSource);").Length - 1);
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", source);
        Assert.Contains("agr10_schema_manifest", source);
        Assert.Contains("DATABASEPROPERTYEX(DB_NAME(), 'Updateability')", source);
    }
}
