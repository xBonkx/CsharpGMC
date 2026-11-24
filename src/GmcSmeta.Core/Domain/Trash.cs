using System.Text.Json;
using System.Text.Json.Serialization;
using GmcSmeta.Core.Db;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// «Корзина» — защита от случайного удаления (легаси SQLite-линии, вытесняется
/// SqlServerTrash). Перед удалением объекта его строки целиком копируются в таблицу
/// trash: документы, разделы, позиции, закупки и движения, акты КС-2, выдача, файлы.
/// Восстановление со сменой id для SQL Server — SqlServerTrash.Restore.
/// </summary>
public static partial class Trash
{
    public const int Days = 30;

    /// <summary>Полная копия объекта перед удалением (trash.keep_object). Возвращает id записи корзины.</summary>
    public static long KeepObject(SqliteConnection conn, long objectId)
    {
        var name = ScalarText(conn, $"SELECT name FROM objects WHERE id = {objectId}") ?? "";

        var contractIds = IdList(conn, $"SELECT id FROM contracts WHERE object_id = {objectId} ORDER BY \"order\", id");
        var dump = DumpContracts(conn, contractIds);
        // Python: строки по позициям своей копии, затем дополняем строками по ведомостям объекта.
        var shipmentLines = (List<Dictionary<string, object?>>)dump["shipment_lines"]!;
        var have = shipmentLines.Select(r => (long)r["id"]!).ToHashSet();
        var shipmentIds = IdList(conn, $"SELECT id FROM shipments WHERE object_id = {objectId} ORDER BY id");
        var extra = DumpRows(conn, "shipment_lines", InList(shipmentIds), "shipment_id");
        shipmentLines.AddRange(extra.Where(r => !have.Contains((long)r["id"]!)));
        dump["objects"] = DumpRows(conn, "objects", objectId.ToString(), "id");
        dump["access_lists"] = DumpRows(conn, "access_lists", objectId.ToString(), "object_id");
        dump["shipments"] = DumpRows(conn, "shipments", objectId.ToString(), "object_id");

        return Store(conn, "object", name, name, dump);
    }

    /// <summary>Полная копия документа перед удалением (trash.keep_contract). Возвращает id записи корзины.</summary>
    public static long KeepContract(SqliteConnection conn, long contractId)
    {
        string title, objectName;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.kind, c.number, c.date, c.name, o.name FROM contracts c " +
                "JOIN objects o ON o.id = c.object_id WHERE c.id = $id";
            cmd.Parameters.AddWithValue("$id", contractId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Документ не найден.");
            string Text(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            title = Documents.ContractTitleText(Text(0), Text(1), Text(2), Text(3));
            objectName = Text(4);
        }

        var dump = DumpContracts(conn, new List<long> { contractId });
        return Store(conn, "contract", title, objectName, dump);
    }

    /// <summary>Снимок документа целиком — общий для объекта и одного документа (trash._dump_contracts).</summary>
    private static Dictionary<string, object?> DumpContracts(SqliteConnection conn, IReadOnlyList<long> contractIds)
    {
        var dump = new Dictionary<string, object?>();
        dump["contracts"] = DumpRows(conn, "contracts", InList(contractIds), "id");
        dump["systems"] = DumpRows(conn, "systems", InList(contractIds), "contract_id");
        var systemIds = IdList(conn, InSelect("SELECT id FROM systems WHERE contract_id IN ({0}) ORDER BY id", contractIds));
        dump["positions"] = DumpRows(conn, "positions", InList(systemIds), "system_id");
        var positionIds = IdList(conn, InSelect("SELECT id FROM positions WHERE system_id IN ({0}) ORDER BY id", systemIds));
        dump["procurement"] = DumpRows(conn, "procurement", InList(positionIds), "position_id");
        dump["movements"] = DumpRows(conn, "movements", InList(positionIds), "position_id");
        dump["acts"] = DumpRows(conn, "acts", InList(contractIds), "contract_id");
        var actIds = IdList(conn, InSelect("SELECT id FROM acts WHERE contract_id IN ({0}) ORDER BY id", contractIds));
        dump["act_lines"] = DumpRows(conn, "act_lines", InList(actIds), "act_id");
        dump["shipment_lines"] = DumpRows(conn, "shipment_lines", InList(positionIds), "position_id");
        dump["smr_costs"] = DumpRows(conn, "smr_costs", InList(contractIds), "contract_id");
        dump["attachments"] = DumpRows(conn, "attachments", InList(contractIds), "contract_id");
        dump["spec_revisions"] = DumpRows(conn, "spec_revisions", InList(contractIds), "contract_id");
        return dump;
    }

    /// <summary>Записать снимок в корзину (trash._store): число позиций и пользователь — как в Python.</summary>
    private static long Store(SqliteConnection conn, string kind, string title, string objectName,
        Dictionary<string, object?> dump) =>
        RowWriter.Insert(conn, "trash", new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["title"] = title,
            ["object_name"] = objectName,
            ["rows"] = (long)(dump["positions"] as List<Dictionary<string, object?>>)!.Count,
            ["deleted_by"] = ChangeLog.CurrentUser,
            ["data"] = JsonSerializer.Serialize(dump, JsonOptions),
        });

    /// <summary>Дата, когда запись корзины будет удалена (trash.days_left).</summary>
    public static DateTime ExpiresAt(DateTime deletedAt) => deletedAt.AddDays(Days);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Все колонки строк таблицы, подходящих по ключу, по возрастанию id (как _rows).</summary>
    private static List<Dictionary<string, object?>> DumpRows(SqliteConnection conn, string table,
        string conditionValue, string column)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (conditionValue.Length == 0) return rows;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{table}\" WHERE \"{column}\" IN ({conditionValue}) ORDER BY id";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < r.FieldCount; i++)
            {
                var value = r.IsDBNull(i) ? null : r.GetValue(i);
                // Python trash._store исключает бинарные значения (логотипы и т.п.).
                if (value is byte[]) continue;
                row[r.GetName(i)] = value;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Python пропускает запрос с пустым списком id; подставляем заведомо пустой вместо IN ().</summary>
    private static string InList(IReadOnlyList<long> ids) => ids.Count > 0
        ? string.Join(", ", ids.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        : "NULL";

    private static string InSelect(string template, IReadOnlyList<long> ids) => ids.Count > 0
        ? string.Format(System.Globalization.CultureInfo.InvariantCulture, template, InList(ids))
        : "SELECT NULL WHERE 0";

    private static List<long> IdList(SqliteConnection conn, string sql)
    {
        var ids = new List<long>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    private static string? ScalarText(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }
}
