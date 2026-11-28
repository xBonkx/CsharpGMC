using System.Data;

namespace GmcSmeta.Core.Db;

/// <summary>Read SQL integer types without assuming the boxed CLR width.
/// Required values never silently become zero; narrowing is checked.</summary>
public static class SqlInteger
{
    public static long RequireInt64(object? value) => value switch
    {
        byte n => n,
        short n => n,
        int n => n,
        long n => n,
        _ => throw new InvalidCastException(
            $"Ожидалось целое число SQL Server, получено: {value?.GetType().Name ?? "null"}.")
    };

    public static long ReadInt64(IDataRecord reader, int ordinal) =>
        RequireInt64(reader.GetValue(ordinal));

    public static int ReadInt32(IDataRecord reader, int ordinal) =>
        checked((int)ReadInt64(reader, ordinal));

    public static int RequireInt32(object? value) => checked((int)RequireInt64(value));
}
