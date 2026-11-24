using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Команды каталога на SQL Server — перенос catalog-служебных операций builder
/// (SQLite-референс Domain/CatalogOperations.cs): переименование/слияние элемента,
/// удаление элемента и подстановка стоимости работ из каталога. Каждая операция —
/// одна транзакция Serializable с проверкой и заменой meta.rev и журналом.
/// </summary>
public static class SqlServerCatalog
{
    /// <summary>
    /// Переименовать элемент каталога или, если такой ключ уже есть, слить позиции в него.
    /// Возвращает идентификатор результата: чужой элемент при слиянии, свой — при переименовании.
    /// </summary>
    public static long UpdateCatalogItem(string connectionString, long itemId, string name, string unit,
        string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        name = (name ?? "").Trim();
        unit = (unit ?? "").Trim();
        var key = Catalog.Key(name, unit);
        if (itemId <= 0 || name.Length == 0 || unit.Length > 32 || key.Length > 600)
            throw new ArgumentException("Некорректное имя, единица или ключ каталога.");
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);

        string oldName, oldUnit;
        using (var read = new SqlCommand(
            "SELECT name,unit FROM dbo.catalog_items WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = itemId;
            using var r = read.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Элемент каталога не найден.");
            oldName = r.GetString(0);
            oldUnit = r.IsDBNull(1) ? "" : r.GetString(1);
        }

        long? otherId = null;
        string? otherName = null;
        using (var other = new SqlCommand(
            "SELECT id,name FROM dbo.catalog_items WITH (UPDLOCK,HOLDLOCK) WHERE [key]=@key AND id != @id", conn, tx))
        {
            other.Parameters.Add("@key", SqlDbType.NVarChar, 600).Value = key;
            other.Parameters.Add("@id", SqlDbType.BigInt).Value = itemId;
            using var r = other.ExecuteReader();
            if (r.Read())
            {
                otherId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                otherName = r.IsDBNull(1) ? null : r.GetString(1);
            }
        }

        if (otherId is long mergeInto)
        {
            // Preserve the source item's historic prices when merging duplicate keys.
            // FK cascading deletion would otherwise silently erase its price history.
            using (var prices = new SqlCommand(
                "UPDATE dbo.price_history SET item_id=@other WHERE item_id=@id", conn, tx))
            {
                prices.Parameters.Add("@other", SqlDbType.BigInt).Value = mergeInto;
                prices.Parameters.Add("@id", SqlDbType.BigInt).Value = itemId;
                prices.ExecuteNonQuery();
            }
            using (var redirect = new SqlCommand(
                "UPDATE dbo.positions SET catalog_item_id=@other WHERE catalog_item_id=@id", conn, tx))
            {
                redirect.Parameters.Add("@other", SqlDbType.BigInt).Value = mergeInto;
                redirect.Parameters.Add("@id", SqlDbType.BigInt).Value = itemId;
                redirect.ExecuteNonQuery();
            }
            LogCatalog(conn, tx, itemId, "merge", $"«{oldName}» → «{otherName}»");
            Execute(conn, tx, "DELETE FROM dbo.catalog_items WHERE id=@id", itemId);
            SqlServerWorkspace.BumpRevision(conn, tx);
            tx.Commit();
            return mergeInto;
        }

        if (oldName == name && oldUnit == unit) return itemId;
        LogCatalog(conn, tx, itemId, "update", $"«{oldName}|{oldUnit}» → «{name}|{unit}»");
        using (var update = new SqlCommand(
            "UPDATE dbo.catalog_items SET [key]=@key,name=@name,unit=@unit WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@key", SqlDbType.NVarChar, 600).Value = key;
            update.Parameters.Add("@name", SqlDbType.NVarChar, -1).Value = name;
            update.Parameters.Add("@unit", SqlDbType.NVarChar, 32).Value = unit;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = itemId;
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Элемент каталога был удалён.");
        }
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
        return itemId;
    }

    /// <summary>Удалить элемент каталога: позиции просто теряют привязку (builder.delete_catalog_item).</summary>
    public static void DeleteCatalogItem(string connectionString, long itemId, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);

        string name;
        using (var read = new SqlCommand(
            "SELECT name FROM dbo.catalog_items WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = itemId;
            var value = read.ExecuteScalar();
            if (value is null or DBNull) throw new ArgumentException("Элемент каталога не найден.");
            name = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        Execute(conn, tx, "UPDATE dbo.positions SET catalog_item_id=NULL WHERE catalog_item_id=@id", itemId);
        LogCatalog(conn, tx, itemId, "delete", name);
        Execute(conn, tx, "DELETE FROM dbo.catalog_items WHERE id=@id", itemId);
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
    }

    /// <summary>
    /// СМР «по позициям»: стоимость работы — сперва из первой подходящей строки текущей
    /// сметы (та же пара имя/единица), иначе из самой последней записи каталога
    /// (max id по catalog_item_id с work_price &gt; 0) — builder.fill_work_from_catalog.
    /// Формула work_price сбрасывается, строки неподписанных актов и их СМР синхронизируются.
    /// Возвращает количество обработанных строк (как счётчик Python — совпавшая цена
    /// тоже считается обработанной, но журнал не пишет).
    /// </summary>
    public static int FillWorkFromCatalog(string connectionString, long contractId, bool onlyEmpty,
        string expectedRevision) =>
        FillWorkFromCatalogWithRevision(connectionString, contractId, onlyEmpty, expectedRevision).Processed;

    public static (int Processed, int Changed, string Revision) FillWorkFromCatalogWithRevision(
        string connectionString, long contractId, bool onlyEmpty, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);

        var title = "";
        bool locked;
        using (var info = new SqlCommand(
            "SELECT c.name,c.locked,o.archived FROM dbo.contracts c " +
            "JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id", conn, tx))
        {
            info.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = info.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Документ не найден.");
            title = r.IsDBNull(0) ? "" : r.GetString(0);
            locked = Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture);
            if (Convert.ToBoolean(r.GetValue(2), CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Архивный объект нельзя изменять.");
        }
        if (locked) throw new InvalidOperationException("Документ подписан.");

        // Последняя по id запись каталога с работой > 0 для каждого элемента.
        var lastWork = new Dictionary<long, double>();
        using (var history = new SqlCommand(
            "SELECT catalog_item_id, work_price FROM dbo.positions " +
            "WHERE catalog_item_id IS NOT NULL AND work_price > 0 ORDER BY id DESC", conn, tx))
        {
            using var r = history.ExecuteReader();
            while (r.Read())
            {
                var catalogId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                lastWork.TryAdd(catalogId, Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
            }
        }

        // Строки документа в порядке показа; первая строка с тем же именем/единицей приоритетна.
        var byName = new Dictionary<string, double>(StringComparer.Ordinal);
        var rows = new List<(long Id, string Key, long? CatalogId, double Work, bool IsEstimateLine,
                             string SystemName, string PositionName)>();
        using (var read = new SqlCommand(
            "SELECT p.id, p.name, p.unit, p.catalog_item_id, p.work_price, p.parent_id, p.out_of_kp, sy.name " +
            "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@id ORDER BY sy.[order], sy.id, p.[order], p.id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var key = Catalog.Key(
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2));
                var work = r.IsDBNull(4) ? 0d : Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture);
                if (work != 0d) byName.TryAdd(key, work);
                rows.Add((
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), key,
                    r.IsDBNull(3) ? null : Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture),
                    work,
                    r.IsDBNull(5) && (r.IsDBNull(6) || !Convert.ToBoolean(r.GetValue(6), CultureInfo.InvariantCulture)),
                    r.IsDBNull(7) ? "" : r.GetString(7),
                    r.IsDBNull(1) ? "" : r.GetString(1)));
            }
        }

        var touchedActs = new HashSet<long>();
        var processed = 0;
        var changed = 0;
        foreach (var row in rows)
        {
            if (!row.IsEstimateLine) continue;
            if (onlyEmpty && row.Work != 0d) continue;
            double value = 0d;
            if (byName.TryGetValue(row.Key, out var fromDocument)) value = fromDocument;
            else if (row.CatalogId is long catalogId && lastWork.TryGetValue(catalogId, out var fromCatalog))
                value = fromCatalog;
            if (value == 0d) continue;

            if (Math.Abs(row.Work - value) > 0.0000001)
            {
                // Формула work_price устаревает при любой записи значения (update_position).
                string? oldFormulas;
                using (var read = new SqlCommand(
                    "SELECT formulas FROM dbo.positions WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
                {
                    read.Parameters.Add("@id", SqlDbType.BigInt).Value = row.Id;
                    oldFormulas = Convert.ToString(read.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
                JsonObject? formulas;
                try
                {
                    formulas = string.IsNullOrWhiteSpace(oldFormulas) ? new JsonObject() :
                        JsonNode.Parse(oldFormulas) as JsonObject ?? throw new FormatException("Некорректный JSON формул.");
                }
                catch (Exception ex) when (ex is JsonException or FormatException)
                {
                    throw new InvalidOperationException("Нельзя безопасно обновить формулы позиции: " + ex.Message);
                }
                formulas.Remove("work_price");
                using (var update = new SqlCommand(
                    "UPDATE dbo.positions SET work_price=@v, formulas=@formulas WHERE id=@id", conn, tx))
                {
                    update.Parameters.Add("@v", SqlDbType.Float).Value = value;
                    update.Parameters.Add("@formulas", SqlDbType.NVarChar, -1).Value = formulas.ToJsonString();
                    update.Parameters.Add("@id", SqlDbType.BigInt).Value = row.Id;
                    if (update.ExecuteNonQuery() != 1)
                        throw new DBConcurrencyException("Позиция исчезла при заполнении цен работ.");
                }
                using (var acts = new SqlCommand(
                    "UPDATE dbo.act_lines SET work_price=@v WHERE position_id=@id " +
                    "AND act_id IN (SELECT id FROM dbo.acts WHERE signed=0 AND contract_id=@contract)", conn, tx))
                {
                    acts.Parameters.Add("@v", SqlDbType.Float).Value = value;
                    acts.Parameters.Add("@id", SqlDbType.BigInt).Value = row.Id;
                    acts.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                    acts.ExecuteNonQuery();
                }
                foreach (var actId in SqlServerDatabase.TouchedUnsignedActs(conn, tx, row.Id, contractId))
                    touchedActs.Add(actId);
                LogPosition(conn, tx, row.Id,
                    $"{title} / {row.SystemName} / {row.PositionName}: work_price: " +
                    $"«{row.Work.ToString("R", CultureInfo.InvariantCulture)}» → " +
                    $"«{value.ToString("R", CultureInfo.InvariantCulture)}»", contractId);
                changed++;
            }
            // builder.fill_work_from_catalog увеличивает счётчик даже при совпавшей цене.
            processed++;
        }
        SqlServerDatabase.SyncActWork(conn, tx, touchedActs);
        var revision = changed > 0 ? SqlServerDatabase.BumpPositionRevision(conn, tx) : expectedRevision;
        tx.Commit();
        return (processed, changed, revision);
    }

    private static void LogCatalog(SqlConnection conn, SqlTransaction tx, long itemId,
        string action, string details)
    {
        using var cmd = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'catalog',@id,@action,@details,NULL,NULL)", conn, tx);
        cmd.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
        cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = ChangeLog.CurrentUser;
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = itemId;
        cmd.Parameters.Add("@action", SqlDbType.NVarChar, 32).Value = action;
        cmd.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        cmd.ExecuteNonQuery();
    }

    private static void LogPosition(SqlConnection conn, SqlTransaction tx, long positionId,
        string details, long contractId)
    {
        using var cmd = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'position',@id,N'update',@details,@contract,NULL)", conn, tx);
        cmd.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
        cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
        cmd.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqlConnection conn, SqlTransaction tx, string sql, long id)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        cmd.ExecuteNonQuery();
    }
}
