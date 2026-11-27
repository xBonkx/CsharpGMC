using System.Data;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class SqlFlagTests
{
    [Theory]
    [InlineData(typeof(bool))]
    [InlineData(typeof(byte))]
    [InlineData(typeof(short))]
    [InlineData(typeof(int))]
    [InlineData(typeof(long))]
    public void ReadsBitAndAllIntegerWidths(Type type)
    {
        using var table = new DataTable();
        table.Columns.Add("flag", type);
        table.Rows.Add(Convert.ChangeType(0, type));
        table.Rows.Add(Convert.ChangeType(1, type));
        table.Rows.Add(DBNull.Value);
        using var reader = table.CreateDataReader();
        Assert.True(reader.Read());
        Assert.False(SqlFlag.Read(reader, 0));
        Assert.True(reader.Read());
        Assert.True(SqlFlag.Read(reader, 0));
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.Throws<InvalidCastException>(() => SqlFlag.Read(reader, 0));
        Assert.False(!reader.IsDBNull(0) && SqlFlag.Read(reader, 0));
    }

    [Fact]
    public void LegacyNonzeroValuesRemainTrue()
    {
        Assert.True(SqlFlag.Require(-1));
        Assert.True(SqlFlag.Require(2L));
        Assert.True(SqlFlag.Require(byte.MaxValue));
    }

    [Fact]
    public void RequiredFlagsRejectMissingAndUnrelatedValues()
    {
        foreach (var value in new object?[] { null, DBNull.Value, "true", "1", 1m, 1.0 })
            Assert.Throws<InvalidCastException>(() => SqlFlag.Require(value));
    }
}
