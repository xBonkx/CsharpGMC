using System.Data;
using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Восстановление из корзины SQL Server — перенос trash.py (restore/list/purge/days_left).
/// Снимок объекта или документа вставляется с НОВЫМИ id (старые могли быть заняты) и
/// перезаписью ссылок: внутри копии — на новые id, наружу — сохраняются, если цель
/// ещё существует, иначе NULL. Ссылки на ещё не вставленные строки копии (FK NO ACTION)
/// откладываются и проставляются отдельным UPDATE, как в Python. Запись корзины
/// удаляется, журнал и новая ревизия пишутся в одной транзакции.
/// </summary>
public static partial class SqlServerTrash
{
    public sealed record TrashEntry(long Id, string Kind, string Title, string ObjectName,
        long Rows, DateTime? DeletedAt, string DeletedBy)
    {
        public int DaysLeft => DeletedAt is DateTime deleted
            ? Math.Max(0, Days - (DateTime.Now - deleted).Days)
            : Days;
    }

    public sealed record RestoredItem(string Kind, long Id);

    // Порядок следует trash.ORDER: родительские таблицы вставляются раньше дочерних.
    private static readonly string[] RestoreOrder =
    {
        "objects", "contracts", "systems", "positions", "procurement", "shipments",
        "movements", "acts", "act_lines", "shipment_lines", "smr_costs",
        "attachments", "spec_revisions", "access_lists",
    };

    // Внешние ключи повторяют trash.FK.
    private static readonly IReadOnlyDictionary<string, string> RestoreForeignKeys =
        new Dictionary<string, string>
        {
            ["object_id"] = "objects", ["contract_id"] = "contracts",
            ["base_contract_id"] = "contracts", ["system_id"] = "systems",
            ["position_id"] = "positions", ["act_id"] = "acts",
            ["shipment_id"] = "shipments", ["stage_of"] = "contracts",
            ["stage_src"] = "positions", ["stage_sys_src"] = "systems",
        };

    /// <summary>parent_id ссылается на ту же таблицу (trash.SELF_PARENT): родители раньше детей.</summary>
    private static readonly string[] SelfParentTables = { "systems", "positions" };

    private sealed record ColumnMeta(string Name, string DataType, bool Nullable);

    /// <summary>Записи корзины по убыванию времени удаления (Python UI trash_dialog.reload).</summary>
    public static IReadOnlyList<TrashEntry> List(string connectionString)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        return List(conn);
    }

    public static (IReadOnlyList<TrashEntry> Entries, string Revision) ReadSnapshot(string connectionString)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = SqlServerDatabase.Revision(conn);
            var entries = List(conn);
            if (!string.IsNullOrWhiteSpace(before) && before == SqlServerDatabase.Revision(conn))
                return (entries, before);
        }
        throw new InvalidOperationException("Корзина изменилась при чтении. Откройте её повторно.");
    }

    private static IReadOnlyList<TrashEntry> List(SqlConnection conn)
    {
        var entries = new List<TrashEntry>();
        using var cmd = new SqlCommand(
            "SELECT id,kind,title,object_name,[rows],deleted_at,deleted_by FROM dbo.trash " +
            "ORDER BY deleted_at DESC, id DESC", conn);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            entries.Add(new TrashEntry(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), Text(1), Text(2), Text(3),
                r.IsDBNull(4) ? 0 : Convert.ToInt64(r.GetValue(4), CultureInfo.InvariantCulture),
                r.IsDBNull(5) ? null : Convert.ToDateTime(r.GetValue(5), CultureInfo.InvariantCulture),
                Text(6)));
        }
        return entries;
    }

    /// <summary>
    /// Вернуть удалённый объект или документ с новыми id (trash.restore). Для одного
    /// документа можно указать существующий объект назначения; если объект документа
    /// удалён, создаётся замена с названием из записи корзины. Атомарно.
    /// </summary>
    public static RestoredItem Restore(string connectionString, long trashId,
        string expectedRevision, long? intoObjectId = null)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);

        // Python: lock_write (rev под UPDLOCK — уже сделано) + s.refresh(item).
        string kind, title, objectName, payload;
        using (var cmd = new SqlCommand(
            "SELECT kind,title,object_name,data FROM dbo.trash WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = trashId;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Запись корзины не найдена.");
            string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            kind = Text(0);
            title = Text(1);
            objectName = Text(2);
            payload = Text(3);
        }
        if (kind is not ("object" or "contract"))
            throw new InvalidOperationException($"Неизвестный тип записи корзины: {kind}");
        if (intoObjectId is long destination && (kind != "contract" || !Exists(conn, tx, "objects", destination)))
            throw new ArgumentException("Объект назначения не найден или не подходит для восстановления.");

        using var snapshot = JsonDocument.Parse(payload);
        if (snapshot.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Неверный формат снимка корзины.");
        // P1 #67: no partial restoration that silently drops protected history or FK.
        // Runs under the existing SERIALIZABLE/meta.rev transaction, before INSERT.
        TrashRestoreSafety.ValidateSnapshot(snapshot.RootElement, kind);

        var columns = LoadColumns(conn, tx, RestoreOrder);
        var decoded = RestoreOrder.ToDictionary(t => t, t => SnapshotRows(snapshot.RootElement, t, columns[t]));
        var maps = RestoreOrder.ToDictionary(t => t, _ => new Dictionary<long, long>());
        var copied = decoded.ToDictionary(p => p.Key,
            p => p.Value.Select(r => Convert.ToInt64(r["id"]!, CultureInfo.InvariantCulture)).ToHashSet());
        if ((kind == "object" && decoded["objects"].Count == 0)
            || (kind == "contract" && decoded["contracts"].Count == 0))
            throw new InvalidOperationException("Снимок корзины неполный: отсутствует корневая запись.");

        // trash.restore: одиночный документ остаётся на живом объекте; иначе — объект назначения
        // или новый объект с названием из записи корзины.
        if (kind == "contract" && decoded["objects"].Count == 0)
        {
            var oldObjectId = RequiredId(decoded["contracts"][0], "object_id");
            if (intoObjectId is null && !Exists(conn, tx, "objects", oldObjectId))
                intoObjectId = InsertReplacementObject(conn, tx,
                    string.IsNullOrEmpty(objectName) ? "Восстановленный объект" : objectName);
            maps["objects"][oldObjectId] = intoObjectId ?? oldObjectId;
        }

        // Отложенные ссылки: цель в копии, но ещё не вставлена — NULL сейчас, UPDATE после всех вставок.
        var pending = new List<(string Table, long OldId, Dictionary<string, (string Target, long Ref)> Links)>();
        foreach (var table in RestoreOrder)
        {
            var rows = decoded[table];
            if (Array.IndexOf(SelfParentTables, table) >= 0)
                rows = rows.OrderBy(r => r.TryGetValue("parent_id", out var p) && p is not null ? 1 : 0)
                    .ThenBy(r => RequiredId(r, "id")).ToList();

            foreach (var row in rows)
            {
                var oldId = RequiredId(row, "id");
                row.Remove("id");
                var delayed = new Dictionary<string, (string Target, long Ref)>();
                foreach (var (column, target) in RestoreForeignKeys)
                {
                    if (!row.TryGetValue(column, out var fkValue) || fkValue is null) continue;
                    var oldForeignId = Convert.ToInt64(fkValue, CultureInfo.InvariantCulture);
                    if (maps[target].TryGetValue(oldForeignId, out var newForeignId))
                        row[column] = newForeignId;
                    else if (copied[target].Contains(oldForeignId) && Nullable(columns[table], column))
                    {
                        delayed[column] = (target, oldForeignId);
                        row[column] = null;
                    }
                    else if (!Exists(conn, tx, target, oldForeignId))
                        row[column] = null;          // цель удалена и не в копии
                }
                if (Array.IndexOf(SelfParentTables, table) >= 0 && row.TryGetValue("parent_id", out var parent)
                    && parent is not null)
                {
                    var oldParentId = Convert.ToInt64(parent, CultureInfo.InvariantCulture);
                    row["parent_id"] = maps[table].TryGetValue(oldParentId, out var newParentId) ? newParentId : null;
                }
                foreach (var (column, target) in new[]
                {
                    ("catalog_item_id", "catalog_items"), ("supplier_id", "suppliers"),
                    ("company_id", "companies"),
                })
                {
                    if (row.TryGetValue(column, out var value) && value is not null &&
                        !Exists(conn, tx, target, Convert.ToInt64(value, CultureInfo.InvariantCulture)))
                        row[column] = null;
                }
                if (table == "act_lines" && row.TryGetValue("qty_formula", out var formula)
                    && formula is string text && text.Contains('@', StringComparison.Ordinal))
                    row["qty_formula"] = ActQuantityFormulas.RemapIds(text, maps["positions"]);

                maps[table][oldId] = InsertRow(conn, tx, table, columns[table], row);
                if (delayed.Count > 0) pending.Add((table, oldId, delayed));
            }
        }

        foreach (var (table, oldId, delayed) in pending)
            foreach (var (column, (target, oldRef)) in delayed)
            {
                using var cmd = new SqlCommand(
                    $"UPDATE dbo.[{table}] SET [{column}]=@ref WHERE id=@id", conn, tx);
                cmd.Parameters.Add("@ref", SqlDbType.BigInt).Value = maps[target][oldRef];
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = maps[table][oldId];
                cmd.ExecuteNonQuery();
            }

        // Связи строк за пределами копии (trash.restore, финальный блок): «держатель» удалённой
        // стороны обнулялся при удалении — восстанавливаем ссылку, если поле до сих пор пусто.
        Relink(conn, tx, snapshot.RootElement, "contract_links", "contracts", "base_contract_id", maps["contracts"]);
        Relink(conn, tx, snapshot.RootElement, "price_links", "price_history", "contract_id", maps["contracts"]);
        Relink(conn, tx, snapshot.RootElement, "metal_links", "metal_entries", "object_id", maps["objects"]);

        using (var del = new SqlCommand("DELETE dbo.trash WHERE id=@id", conn, tx))
        {
            del.Parameters.Add("@id", SqlDbType.BigInt).Value = trashId;
            del.ExecuteNonQuery();
        }

        var restoredId = kind == "object" ? maps["objects"].Values.First() : maps["contracts"].Values.First();
        Log(conn, tx, kind, restoredId, "restore", $"из корзины: {title}");
        SqlServerWorkspace.BumpRevision(conn, tx);
        try
        {
            tx.Commit();
        }
        catch (Exception error) when (error is SqlException or InvalidOperationException or TimeoutException)
        {
            // A network error can occur AFTER SQL Server has committed. Do not replay.
            if (!RestoreReadbackConfirmed(connectionString, kind, restoredId, trashId))
                throw new TrashCommitUnknownException(error);
        }
        return new RestoredItem(kind, restoredId);
    }

    /// <summary>Удалить навсегда одну запись из корзины (данные были удалены раньше).</summary>
    public static bool Forget(string connectionString, long trashId, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        // Irreversible removal must not discard the only snapshot of protected rows.
        string kind, payload;
        using (var read = new SqlCommand(
            "SELECT kind,data FROM dbo.trash WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = trashId;
            using var r = read.ExecuteReader();
            if (!r.Read()) return false;
            kind = r.GetString(0);
            payload = r.GetString(1);
        }
        using (var snapshot = JsonDocument.Parse(payload))
            TrashRestoreSafety.ValidateSnapshot(snapshot.RootElement, kind);
        long removed;
        using (var cmd = new SqlCommand("DELETE dbo.trash WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = trashId;
            removed = cmd.ExecuteNonQuery();
        }
        if (removed > 0)
        {
            if (removed != 1) throw new DBConcurrencyException("Изменено неожиданное число записей корзины.");
            SqlServerWorkspace.BumpRevision(conn, tx);
            try { tx.Commit(); }
            catch (Exception error) when (error is SqlException or InvalidOperationException or TimeoutException)
            {
                throw new TrashCommitUnknownException(error);
            }
        }
        return removed > 0;
    }

    /// <summary>Очистка просроченных снимков (trash.purge). В Python вызывается только в файловом режиме.</summary>
    public static int Purge(string connectionString, int days = Days)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        // A bulk DELETE cannot distinguish protected signed/issued snapshots and
        // has no caller-provided meta.rev. Keep the records until a reviewed,
        // per-item retention policy can run in a single audited transaction.
        throw new InvalidOperationException(
            "Автоматическая очистка корзины SQL Server отключена: требуется проверка каждого снимка.");
    }

    // ---------------------------------------------------------------- внутреннее

    private static long InsertReplacementObject(SqlConnection conn, SqlTransaction tx, string name)
    {
        // SQLAlchemy defaults применяются на клиенте: заполняем все NOT NULL колонки явно
        // (как SqlServerWorkspace.CreateObject); журнал не пишется — Python его не делает.
        using var cmd = new SqlCommand(
            @"INSERT dbo.objects
              (name,customer,address,folder,note,created_at,customer_full,site_full,
               cust_org,cust_post,cust_name,second_role,second_org,second_post,second_name,
               work_title,archived,form,archived_on,works_start)
              OUTPUT INSERTED.id
              VALUES (@name,N'',N'',N'',N'',SYSDATETIME(),N'',N'',
                      N'',N'',N'',N'',N'',N'',N'',N'Монтаж системы вентиляции',0,N'',NULL,N'')",
            conn, tx);
        cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = name;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Relink(SqlConnection conn, SqlTransaction tx, JsonElement root,
        string key, string table, string column, IReadOnlyDictionary<long, long> map)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(key, out var links)
            || links.ValueKind != JsonValueKind.Array) return;
        foreach (var link in links.EnumerateArray())
        {
            if (link.ValueKind != JsonValueKind.Object) continue;
            if (!link.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number
                || !link.TryGetProperty(column, out var refEl) || refEl.ValueKind != JsonValueKind.Number) continue;
            var oldId = idEl.GetInt64();
            var oldRef = refEl.GetInt64();
            if (!map.TryGetValue(oldRef, out var newRef)) continue;
            using var cmd = new SqlCommand(
                $"UPDATE dbo.[{table}] SET [{column}]=@ref WHERE id=@id AND [{column}] IS NULL", conn, tx);
            cmd.Parameters.Add("@ref", SqlDbType.BigInt).Value = newRef;
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = oldId;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Строки одной таблицы снимка, приведённые к колонкам реальной таблицы (trash._decode).</summary>
    private static List<Dictionary<string, object?>> SnapshotRows(JsonElement root, string table,
        IReadOnlyList<ColumnMeta> columns)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(table, out var array)
            || array.ValueKind is JsonValueKind.Null) return rows;
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Неверная таблица в снимке корзины: {table}");
        var known = columns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Неверная строка в снимке корзины: {table}");
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in item.EnumerateObject())
            {
                if (!known.TryGetValue(property.Name, out var meta)) continue;   // колонки нет в схеме
                if (meta.DataType is "varbinary" or "binary" or "image") continue; // бинарь исключён при снимке
                // null хранится как null: DBNull появится только при привязке параметров
                // (проверки ссылок в restore различают null и DBNull).
                var value = JsonToSql(property.Value, meta.DataType);
                row[meta.Name] = value is DBNull ? null : value;
            }
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Значение JSON → значение параметра SQL Server по типу колонки (trash._decode).</summary>
    public static object? JsonToSql(JsonElement value, string dataType)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return DBNull.Value;
        return dataType switch
        {
            "bit" => value.ValueKind == JsonValueKind.True ? true
                : value.ValueKind == JsonValueKind.False ? false
                : Convert.ToBoolean(ToDouble(value), CultureInfo.InvariantCulture),
            "bigint" => value.TryGetInt64(out var big) ? big : Convert.ToInt64(ToDouble(value)),
            "int" => checked((int)(value.TryGetInt64(out var n) ? n : Convert.ToInt64(ToDouble(value)))),
            "smallint" => checked((short)(value.TryGetInt64(out var s) ? s : Convert.ToInt64(ToDouble(value)))),
            "tinyint" => checked((byte)(value.TryGetInt64(out var t) ? t : Convert.ToInt64(ToDouble(value)))),
            "float" or "real" => ToDouble(value),
            "decimal" or "numeric" or "money" or "smallmoney" =>
                Convert.ToDecimal(ToDouble(value), CultureInfo.InvariantCulture),
            "date" or "datetime" or "datetime2" or "smalldatetime" => ParseDate(value),
            "uniqueidentifier" => Guid.Parse(value.GetString() ?? ""),
            _ => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText(),
        };
    }

    private static double ToDouble(JsonElement value) => value.ValueKind == JsonValueKind.Number
        ? value.GetDouble()
        : Convert.ToDouble(value.GetRawText(), CultureInfo.InvariantCulture);

    private static DateTime ParseDate(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        return DateTime.Parse(text ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static long RequiredId(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value) || value is null or DBNull)
            throw new InvalidOperationException($"В снимке отсутствует обязательное поле {column}.");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static bool Nullable(IReadOnlyList<ColumnMeta> columns, string name)
    {
        foreach (var c in columns)
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c.Nullable;
        return false;
    }

    private static bool Exists(SqlConnection conn, SqlTransaction tx, string table, long id)
    {
        using var cmd = new SqlCommand($"SELECT 1 FROM dbo.[{table}] WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Колонки таблиц восстановления из INFORMATION_SCHEMA — типы и nullability берутся из базы.</summary>
    private static Dictionary<string, List<ColumnMeta>> LoadColumns(SqlConnection conn, SqlTransaction tx,
        IReadOnlyList<string> tables)
    {
        var names = string.Join(", ", tables.Select(t => $"'{t}'"));
        var result = tables.ToDictionary(t => t, _ => new List<ColumnMeta>());
        using var cmd = new SqlCommand(
            "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS " +
            $"WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME IN ({names}) ORDER BY TABLE_NAME, ORDINAL_POSITION", conn, tx);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var table = r.GetString(0);
            if (!result.ContainsKey(table)) continue;
            result[table].Add(new ColumnMeta(r.GetString(1), r.GetString(2), r.GetString(3) == "YES"));
        }
        foreach (var table in tables)
            if (result[table].Count == 0)
                throw new InvalidOperationException($"В базе нет таблицы dbo.{table}.");
        return result;
    }

    /// <summary>Вставить строку с НОВЫМ id (IDENTITY) и вернуть его.</summary>
    private static long InsertRow(SqlConnection conn, SqlTransaction tx, string table,
        IReadOnlyList<ColumnMeta> columns, Dictionary<string, object?> row)
    {
        var names = new List<string>();
        var values = new List<string>();
        var parameters = new List<(string Name, ColumnMeta Meta, object? Value)>();
        var index = 0;
        foreach (var c in columns)
        {
            if (!row.TryGetValue(c.Name, out var value)) continue;
            names.Add($"[{c.Name}]");
            values.Add($"@p{index}");
            parameters.Add(($"@p{index}", c, value));
            ++index;
        }
        if (names.Count == 0)
            throw new InvalidOperationException($"Пустая строка снимка для таблицы dbo.{table}.");

        using var insert = new SqlCommand(
            $"INSERT dbo.[{table}] ({string.Join(", ", names)}) OUTPUT INSERTED.id " +
            $"VALUES ({string.Join(", ", values)})", conn, tx);
        foreach (var (name, meta, value) in parameters)
            insert.Parameters.Add(name, SqlDbTypeFor(meta.DataType)).Value = value ?? DBNull.Value;
        return Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static SqlDbType SqlDbTypeFor(string dataType) => dataType switch
    {
        "bit" => SqlDbType.Bit,
        "bigint" => SqlDbType.BigInt,
        "int" => SqlDbType.Int,
        "smallint" => SqlDbType.SmallInt,
        "tinyint" => SqlDbType.TinyInt,
        "float" => SqlDbType.Float,
        "real" => SqlDbType.Real,
        "decimal" or "numeric" => SqlDbType.Decimal,
        "money" => SqlDbType.Money,
        "smallmoney" => SqlDbType.SmallMoney,
        "date" => SqlDbType.Date,
        "datetime" or "smalldatetime" => SqlDbType.DateTime,
        "datetime2" => SqlDbType.DateTime2,
        "uniqueidentifier" => SqlDbType.UniqueIdentifier,
        "varbinary" or "binary" or "image" => SqlDbType.VarBinary,
        _ => SqlDbType.NVarChar,
    };

    private static void Log(SqlConnection conn, SqlTransaction tx,
        string entity, long id, string action, string details)
    {
        // Владелец журнала — как в Python log_change/_log_owner: для документа ещё и его объект.
        using var cmd = new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,@entity,@id,@action,@details,@contract,@obj)", conn, tx);
        cmd.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
        var user = Environment.UserName;
        cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = user[..Math.Min(64, user.Length)];
        cmd.Parameters.Add("@entity", SqlDbType.NVarChar, 32).Value = entity;
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        cmd.Parameters.Add("@action", SqlDbType.NVarChar, 32).Value = action;
        cmd.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value =
            entity == "contract" ? id : DBNull.Value;
        cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = entity == "object" ? id
            : entity == "contract" ? ScalarObject(conn, tx, id) : DBNull.Value;
        cmd.ExecuteNonQuery();
    }

    private static object ScalarObject(SqlConnection conn, SqlTransaction tx, long contractId)
    {
        using var cmd = new SqlCommand(
            "SELECT object_id FROM dbo.contracts WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        return cmd.ExecuteScalar() ?? DBNull.Value;
    }
}
