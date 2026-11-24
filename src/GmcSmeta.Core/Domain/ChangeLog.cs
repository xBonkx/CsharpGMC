using GmcSmeta.Core.Db;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// «История изменений»: каждая правка документа пишет строку в change_log — как в Python.
/// Строка привязывается к документу и объекту, чтобы история открывалась одним запросом.
/// </summary>
public static partial class ChangeLog
{
    /// <summary>Имя пользователя для истории: учётная запись Windows.</summary>
    public static string CurrentUser
    {
        get
        {
            var name = Environment.UserName;
            return string.IsNullOrWhiteSpace(name) ? "?" : name;
        }
    }

    public static void Log(SqliteConnection conn, string entity, long? entityId, string action,
                           string details = "", string? user = null)
    {
        var (contractId, objectId) = Owner(conn, entity, entityId);
        RowWriter.Insert(conn, "change_log", new Dictionary<string, object?>
        {
            ["user"] = user ?? CurrentUser,
            ["entity"] = entity,
            ["entity_id"] = entityId,
            ["action"] = action,
            ["details"] = details,
            ["contract_id"] = contractId,
            ["object_id"] = objectId,
        });
    }

    /// <summary>К какому документу и объекту относится изменение. Никогда не бросает исключение.</summary>
    private static (long? ContractId, long? ObjectId) Owner(SqliteConnection conn, string entity, long? id)
    {
        if (id is null) return (null, null);

        long? contractId = entity switch
        {
            "contract" => id,
            "system" => Scalar(conn, "SELECT contract_id FROM systems WHERE id = $id", id.Value),
            "position" or "procurement" or "movement" => Scalar(conn,
                "SELECT sy.contract_id FROM positions p JOIN systems sy ON sy.id = p.system_id WHERE p.id = $id",
                id.Value),
            "act" => Scalar(conn, "SELECT contract_id FROM acts WHERE id = $id", id.Value),
            "smr_cost" => Scalar(conn, "SELECT contract_id FROM smr_costs WHERE id = $id", id.Value),
            _ => null,
        };

        if (entity == "object")
            return (null, id);

        if (entity == "shipment")
        {
            var shipmentObject = Scalar(conn, "SELECT object_id FROM shipments WHERE id = $id", id.Value);
            return (null, shipmentObject);
        }

        var objectId = contractId is null
            ? null
            : Scalar(conn, "SELECT object_id FROM contracts WHERE id = $id", contractId.Value);
        return (contractId, objectId);
    }

    private static long? Scalar(SqliteConnection conn, string sql, long id)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            var value = cmd.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToInt64(value);
        }
        catch (SqliteException)
        {
            return null;      // история не должна ломать сохранение
        }
    }
}
