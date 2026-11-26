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
}
