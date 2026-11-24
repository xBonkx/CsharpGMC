using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Лёгкая миграция как в Python (`_add_missing_columns`): добавить колонки, которые есть
/// в модели, но отсутствуют в файле базы. Только новые колонки — существующие данные не трогаются.
/// Тип и значение по умолчанию берутся из сгенерированного описания (SchemaData), поэтому
/// совпадают с Python-версией.
/// </summary>
public static class Migrations
{
    /// <summary>Добавить недостающие колонки во все существующие таблицы.</summary>
    public static List<string> AddMissingColumns(SqliteConnection conn)
    {
        var added = new List<string>();
        foreach (var table in SchemaData.Tables)
        {
            if (!Database.TableExists(conn, table.Name)) continue;

            var have = ColumnNames(conn, table.Name);
            foreach (var column in table.Columns)
            {
                if (have.Contains(column.Name)) continue;

                Database.Exec(conn,
                    $"ALTER TABLE \"{table.Name}\" ADD COLUMN \"{column.Name}\" " +
                    column.SqlType + DefaultSql(column));
                added.Add($"{table.Name}.{column.Name}");
            }
        }
        return added;
    }

    /// <summary>DEFAULT для скалярных значений; для «сейчас» и вызовов — без DEFAULT (как в Python).</summary>
    private static string DefaultSql(ColumnInfo column) => column.DefaultKind switch
    {
        DefaultKind.String => " DEFAULT '" + column.DefaultValue.Replace("'", "''") + "'",
        DefaultKind.Number => " DEFAULT " + column.DefaultValue,
        _ => "",
    };

    public static HashSet<string> ColumnNames(SqliteConnection conn, string table)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(1));
        return names;
    }
}
