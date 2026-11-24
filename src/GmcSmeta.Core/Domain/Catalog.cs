using System.Text.RegularExpressions;
using GmcSmeta.Core.Db;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Каталог позиций — перенос services.catalog_key / _catalog_item.
/// Ключ не зависит от регистра, лишних пробелов и точки в конце единицы.
/// </summary>
public static partial class Catalog
{
    /// <summary>
    /// Единица из каталога по имени (как self.catalog в Python): null, если такого имени нет.
    /// Нужна при вводе названия: цена не подставляется, единица — да.
    /// </summary>
    public static string? Unit(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT unit FROM catalog_items WHERE lower(trim(name)) = lower(trim($name)) " +
            "ORDER BY name DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$name", name);
        var found = cmd.ExecuteScalar();
        return found is null or DBNull ? null : (string)found;
    }

    /// <summary>Найти или создать элемент каталога; для пустого имени — null.</summary>
    public static long? ItemId(SqliteConnection conn, string? name, string? unit)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var key = Key(name, unit);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM catalog_items WHERE key = $key LIMIT 1";
            cmd.Parameters.AddWithValue("$key", key);
            var found = cmd.ExecuteScalar();
            if (found is not null and not DBNull) return Convert.ToInt64(found);
        }

        return RowWriter.Insert(conn, "catalog_items", new Dictionary<string, object?>
        {
            ["key"] = key,
            ["name"] = name.Trim(),
            ["unit"] = (unit ?? "").Trim(),
        });
    }
}
