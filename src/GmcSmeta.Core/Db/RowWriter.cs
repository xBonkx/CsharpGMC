using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Запись строк в любую таблицу с теми же значениями по умолчанию, что в Python-модели.
/// Имена колонок проверяются по описанию (SchemaData): опечатка сразу даёт ошибку,
/// а не тихо создаёт мусор в базе.
///
/// Деньги и количества хранятся в базе как REAL (так же, как Python писал float),
/// поэтому decimal переводится в double только в момент записи.
/// </summary>
public static class RowWriter
{
    public static long Insert(SqliteConnection conn, string table, IReadOnlyDictionary<string, object?> values)
    {
        var info = SchemaData.Get(table);
        var names = new List<string>();
        var values2 = new List<object?>();

        foreach (var column in info.Columns)
        {
            names.Add(column.Name);
            if (values.TryGetValue(column.Name, out var given))
                values2.Add(Normalize(given));
            else
                values2.Add(DefaultValue(column));
        }

        using var cmd = conn.CreateCommand();
        var pars = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            var p = "$p" + i;
            pars.Add(p);
            cmd.Parameters.AddWithValue(p, values2[i] ?? DBNull.Value);
        }
        cmd.CommandText = $"INSERT INTO \"{table}\" ({Quote(names)}) VALUES ({string.Join(", ", pars)})";
        cmd.ExecuteNonQuery();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)idCmd.ExecuteScalar()!;
    }

    /// <summary>Изменить указанные колонки строки по id.</summary>
    public static void Update(SqliteConnection conn, string table, long id,
                              IReadOnlyDictionary<string, object?> values)
    {
        var info = SchemaData.Get(table);
        var known = info.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var cmd = conn.CreateCommand();
        var sets = new List<string>();
        var i = 0;
        foreach (var (name, value) in values)
        {
            if (!known.Contains(name))
                throw new ArgumentException($"в таблице {table} нет колонки {name}");
            if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase))
                continue;

            var p = "$p" + i++;
            sets.Add($"\"{name}\" = {p}");
            cmd.Parameters.AddWithValue(p, Normalize(value) ?? DBNull.Value);
        }
        if (sets.Count == 0) return;

        cmd.Parameters.AddWithValue("$id", id);
        cmd.CommandText = $"UPDATE \"{table}\" SET {string.Join(", ", sets)} WHERE id = $id";
        cmd.ExecuteNonQuery();
    }

    public static int Delete(SqliteConnection conn, string table, long id)
    {
        SchemaData.Get(table);   // проверка имени таблицы
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM \"{table}\" WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery();
    }

    public static long Count(SqliteConnection conn, string table)
    {
        SchemaData.Get(table);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Значение по умолчанию для колонки — как его подставил бы SQLAlchemy.</summary>
    private static object? DefaultValue(ColumnInfo column) => column.DefaultKind switch
    {
        DefaultKind.String => column.DefaultValue,
        DefaultKind.Number => ParseNumber(column.DefaultValue),
        DefaultKind.Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        _ => null,
    };

    private static object ParseNumber(string text) =>
        text.Contains('.', StringComparison.Ordinal)
            ? double.Parse(text, CultureInfo.InvariantCulture)
            : long.Parse(text, CultureInfo.InvariantCulture);

    /// <summary>decimal → double (колонки денег в базе REAL); bool → 0/1, как SQLite.</summary>
    private static object? Normalize(object? value) => value switch
    {
        null => null,
        bool b => b ? 1L : 0L,
        decimal d => (double)d,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => value,
    };

    private static string Quote(IEnumerable<string> names) =>
        string.Join(", ", names.Select(n => $"\"{n}\""));
}
