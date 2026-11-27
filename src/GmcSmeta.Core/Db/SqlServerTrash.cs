using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// «Корзина» SQL Server — перенос trash.py: перед удалением объекта или документа его
/// строки целиком (JSON-снимок) копируются в dbo.trash: документы, разделы, позиции
/// с комплектующими, закупки и движения, акты КС-2 и их строки, выдача, СМР-затраты,
/// файлы, спецификации; для объекта — сам объект, допуски и ведомости. Бинарные
/// значения исключаются, как в Python trash._store. Восстановление со сменой id —
/// в SqlServerTrashRestore (partial этого же класса).
/// </summary>
public static partial class SqlServerTrash
{
    public const int Days = 30;

    /// <summary>Полная копия объекта перед удалением (trash.keep_object). Возвращает id записи корзины.</summary>
    public static long KeepObject(SqlConnection conn, SqlTransaction tx, long objectId)
    {
        var name = ScalarText(conn, tx,
            "SELECT name FROM dbo.objects WHERE id=@id", objectId) ?? "";

        var contractIds = IdList(conn, tx,
            "SELECT id FROM dbo.contracts WHERE object_id=@id ORDER BY [order],id", objectId);
        var dump = DumpContracts(conn, tx, contractIds);
        // Python: строки по позициям своей копии, затем дополняем строками по ведомостям объекта.
        var shipmentLines = (List<Dictionary<string, object?>>)dump["shipment_lines"]!;
        var have = shipmentLines.Select(r => SqlInteger.RequireInt64(r["id"])).ToHashSet();
        var shipmentIds = IdList(conn, tx,
            "SELECT id FROM dbo.shipments WHERE object_id=@id ORDER BY id", objectId);
        var extra = DumpRows(conn, tx, "shipment_lines", shipmentIds, "shipment_id");
        shipmentLines.AddRange(extra.Where(r => !have.Contains(SqlInteger.RequireInt64(r["id"]))));
        if (SqlServerAgreements.Enabled(conn, tx))
        {
            var legalIds = IdList(conn, tx, "SELECT id FROM dbo.agreements WHERE object_id=@id ORDER BY id", objectId);
            var cards = DumpRows(conn, tx, "agreements", new List<long> { objectId }, "object_id");
            foreach (var card in cards) card.Remove("row_version");
            dump["agreements"] = cards;
            dump["agreement_files"] = DumpRows(conn, tx, "agreement_files", legalIds, "agreement_id");
        }
        dump["objects"] = DumpRows(conn, tx, "objects", new List<long> { objectId }, "id");
        dump["access_lists"] = DumpRows(conn, tx, "access_lists", new List<long> { objectId }, "object_id");
        dump["shipments"] = DumpRows(conn, tx, "shipments", new List<long> { objectId }, "object_id");
        // Связи за пределами копии (mssql-ветка trash.keep_object): metal_entries.object_id
        // обнуляется при удалении — restore возвращает ссылку по этим парам.
        dump["metal_links"] = DumpLinkRows(conn, tx, "metal_entries", "object_id", objectId);

        // Fail closed BEFORE the caller deletes rows in its transaction: only a
        // lossless, independently restorable open snapshot may enter the trash.
        TrashRestoreSafety.ValidateSnapshot(JsonSerializer.SerializeToElement(dump, JsonOptions), "object");
        return Store(conn, tx, "object", name, name, dump);
    }

    /// <summary>Полная копия документа перед удалением (trash.keep_contract). Возвращает id записи корзины.</summary>
    public static long KeepContract(SqlConnection conn, SqlTransaction tx, long contractId, bool allowStage = false)
    {
        string title, objectName;
        using (var cmd = new SqlCommand(
            "SELECT c.kind,c.number,c.[date],c.name,o.name FROM dbo.contracts c " +
            "JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Документ не найден.");
            string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            title = Documents.ContractTitleText(Text(0), Text(1), Text(2), Text(3));
            objectName = Text(4);
        }

        var dump = DumpContracts(conn, tx, new List<long> { contractId });
        // KeepContract is called before SqlServerWorkspace deletes acts/movements.
        // Do not allow irreversible loss that the current restore cannot recover.
        TrashRestoreSafety.ValidateSnapshot(JsonSerializer.SerializeToElement(dump, JsonOptions), "contract", allowStage);
        return Store(conn, tx, "contract", title, objectName, dump);
    }

    /// <summary>Дата, когда запись корзины будет удалена (trash.days_left).</summary>
    public static DateTime ExpiresAt(DateTime deletedAt) => deletedAt.AddDays(Days);

    // ---------------------------------------------------------------- внутреннее

    /// <summary>Снимок документов целиком — общий для объекта и одного документа (trash._dump_contracts).</summary>
    private static Dictionary<string, object?> DumpContracts(SqlConnection conn, SqlTransaction tx,
        IReadOnlyList<long> contractIds)
    {
        var dump = new Dictionary<string, object?>();
        dump["contracts"] = DumpRows(conn, tx, "contracts", contractIds, "id");
        dump["systems"] = DumpRows(conn, tx, "systems", contractIds, "contract_id");
        var systemIds = IdListIn(conn, tx,
            "SELECT id FROM dbo.systems WHERE contract_id IN ({0}) ORDER BY id", contractIds);
        dump["positions"] = DumpRows(conn, tx, "positions", systemIds, "system_id");
        var positionIds = IdListIn(conn, tx,
            "SELECT id FROM dbo.positions WHERE system_id IN ({0}) ORDER BY id", systemIds);
        dump["procurement"] = DumpRows(conn, tx, "procurement", positionIds, "position_id");
        dump["movements"] = DumpRows(conn, tx, "movements", positionIds, "position_id");
        dump["acts"] = DumpRows(conn, tx, "acts", contractIds, "contract_id");
        var actIds = IdListIn(conn, tx,
            "SELECT id FROM dbo.acts WHERE contract_id IN ({0}) ORDER BY id", contractIds);
        dump["act_lines"] = DumpRows(conn, tx, "act_lines", actIds, "act_id");
        dump["shipment_lines"] = DumpRows(conn, tx, "shipment_lines", positionIds, "position_id");
        dump["smr_costs"] = DumpRows(conn, tx, "smr_costs", contractIds, "contract_id");
        dump["attachments"] = DumpRows(conn, tx, "attachments", contractIds, "contract_id");
        dump["spec_revisions"] = DumpRows(conn, tx, "spec_revisions", contractIds, "contract_id");
        // mssql-ветка trash._dump_contracts: держатели ссылок за пределами копии. Удаление
        // обнуляет base_contract_id и price_history.contract_id — restore возвращает их по парам.
        dump["contract_links"] = DumpRows(conn, tx, "contracts", contractIds, "base_contract_id")
            .Where(r => !contractIds.Contains(SqlInteger.RequireInt64(r["id"])))
            .Select(r => new Dictionary<string, object?>
            {
                ["id"] = r["id"],
                ["base_contract_id"] = r["base_contract_id"],
            }).ToList();
        dump["price_links"] = DumpRows(conn, tx, "price_history", contractIds, "contract_id")
            .Select(r => new Dictionary<string, object?>
            {
                ["id"] = r["id"],
                ["contract_id"] = r["contract_id"],
            }).ToList();
        return dump;
    }

    /// <summary>Записать снимок в корзину (trash._store): число позиций и пользователь — как в Python.</summary>
    private static long Store(SqlConnection conn, SqlTransaction tx, string kind, string title,
        string objectName, Dictionary<string, object?> dump)
    {
        using var cmd = new SqlCommand(
            "INSERT dbo.trash (kind,title,object_name,[rows],deleted_at,deleted_by,data) " +
            "OUTPUT INSERTED.id VALUES (@kind,@title,@object,@rows,@at,@by,@data)", conn, tx);
        cmd.Parameters.Add("@kind", SqlDbType.NVarChar, 16).Value = kind;
        cmd.Parameters.Add("@title", SqlDbType.NVarChar, -1).Value = title;
        cmd.Parameters.Add("@object", SqlDbType.NVarChar, -1).Value = objectName;
        cmd.Parameters.Add("@rows", SqlDbType.BigInt).Value =
            ((List<Dictionary<string, object?>>)dump["positions"]!).Count;
        cmd.Parameters.Add("@at", SqlDbType.DateTime2).Value = DateTime.Now;
        cmd.Parameters.Add("@by", SqlDbType.NVarChar, 64).Value = ChangeLog.CurrentUser;
        cmd.Parameters.Add("@data", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(dump, JsonOptions);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Все колонки строк таблицы, подходящих по ключу, по возрастанию id (как trash._rows).</summary>
    private static List<Dictionary<string, object?>> DumpRows(SqlConnection conn, SqlTransaction tx,
        string table, IReadOnlyList<long> ids, string column)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (ids.Count == 0) return rows;
        // В списке только внутренние id — без параметров, как в SQLite-переносе.
        var list = string.Join(", ", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        using var cmd = new SqlCommand(
            $"SELECT * FROM dbo.[{table}] WHERE [{column}] IN ({list}) ORDER BY id", conn, tx);
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

    /// <summary>Пары (id, колонка-ссылка) для relink-таблиц за пределами копии.</summary>
    private static List<Dictionary<string, object?>> DumpLinkRows(SqlConnection conn, SqlTransaction tx,
        string table, string column, long id)
    {
        var rows = new List<Dictionary<string, object?>>();
        using var cmd = new SqlCommand(
            $"SELECT id, [{column}] FROM dbo.[{table}] WHERE [{column}]=@id ORDER BY id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                [column] = r.IsDBNull(1) ? null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
            });
        return rows;
    }

    private static List<long> IdListIn(SqlConnection conn, SqlTransaction tx, string template,
        IReadOnlyList<long> ids)
    {
        var result = new List<long>();
        if (ids.Count == 0) return result;
        var list = string.Join(", ", ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        using var cmd = new SqlCommand(string.Format(CultureInfo.InvariantCulture, template, list), conn, tx);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        return result;
    }

    private static List<long> IdList(SqlConnection conn, SqlTransaction tx, string sql, long id)
    {
        var result = new List<long>();
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        return result;
    }

    private static string? ScalarText(SqlConnection conn, SqlTransaction tx, string sql, long id)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
