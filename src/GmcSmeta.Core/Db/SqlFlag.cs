using System.Data;

namespace GmcSmeta.Core.Db;

/// <summary>Reads BIT and legacy integer flags without requiring one provider CLR type.
/// Required flags reject missing or unrelated values. Callers explicitly handle nullable flags.</summary>
public static class SqlFlag
{
    public static bool Require(object? value) => value switch
    {
        bool flag => flag,
        byte n => n != 0,
        short n => n != 0,
        int n => n != 0,
        long n => n != 0,
        _ => throw new InvalidCastException(
            $"Ожидался логический или целочисленный флаг SQL, получено: {value?.GetType().Name ?? "null"}.")
    };

    public static bool Read(IDataRecord reader, int ordinal) => Require(reader.GetValue(ordinal));
}
