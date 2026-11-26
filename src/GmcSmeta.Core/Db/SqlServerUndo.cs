using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Снимок документа для отмены/повтора (smeta/undo.py): реквизиты договора, разделы,
/// позиции, закупки, движения без выдачи, акты КС-2 со строками, незакреплённые
/// строки выдачи и СМР-затраты. Равенство — по канонической JSON-строке, поэтому
/// сравнение снимков не зависит от типов конкретной ADO.NET-базы.
/// </summary>
public sealed class UndoSnapshot
{
    private readonly Dictionary<string, List<Dictionary<string, object?>>> _tables;
    private string? _canonical;

    public int ContractId { get; }

    public UndoSnapshot(int contractId, Dictionary<string, List<Dictionary<string, object?>>> tables)
    {
        ContractId = contractId;
        _tables = tables;
    }

    /// <summary>Строки таблицы снимка; для contract — всегда одна строка.</summary>
    public IReadOnlyList<Dictionary<string, object?>> Rows(string table) =>
        _tables.TryGetValue(table, out var rows) ? rows : Array.Empty<Dictionary<string, object?>>();

    public IReadOnlyCollection<string> TableNames => _tables.Keys;
    public string Canonical => _canonical ??= JsonSerializer.Serialize(_tables, JsonOptions);

    public override bool Equals(object? obj) => obj is UndoSnapshot other &&
        other.ContractId == ContractId && other.Canonical == Canonical;

    public override int GetHashCode() => HashCode.Combine(ContractId, Canonical);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>Итог применения undo/redo на сервере — одна транзакция снимает и сравнивает состояние.</summary>
public enum UndoApplyStatus { Applied, Nothing, Moved, Failed, Indeterminate }

public readonly record struct UndoApplyResult(UndoApplyStatus Status, UndoSnapshot? Restored, UndoSnapshot? Now)
{
    // Only populated by the transaction that actually committed the restore.
    public string? Revision { get; init; }
    // Failure diagnostics; null on success.
    public string? Error { get; init; }
}

/// <summary>
/// Отмена/повтор на SQL Server — перенос smeta/undo.py: снимок документа, при отмене
/// строки пишутся обратно с их исходными id (IDENTITY_INSERT), поэтому ссылки
/// (подпозиции, каталог) остаются верными. Закреплённые ведомости защищены: undo,
/// затирающее позицию проведённой выдачи, отвергается. Удалённые и заново вставленные
/// строки сравниваются с сохранённым снимком — расхождение откатывает операцию.
/// </summary>
public static class SqlServerUndo
{
    // Служебные колонки закупки не считаются изменением документа (как в Python).
    private static readonly string[] ProcurementSkip = { "updated_at", "updated_by" };

    // Keep result sets in the same order and with the same SQL scopes as the
    // old individual reads. One round trip instead of ten per checkpoint.
    private static readonly (string Key, string Sql)[] SnapshotQueries =
    {
        ("contract", "SELECT * FROM dbo.contracts WHERE id=@id"),
        ("systems", "SELECT * FROM dbo.systems WHERE contract_id=@id ORDER BY id"),
        ("positions", "SELECT * FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id) ORDER BY id"),
        ("procurement", "SELECT * FROM dbo.procurement WHERE position_id IN " +
            "(SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id)) ORDER BY id"),
        // Shipment movements belong to the issuance history, not document Undo.
        ("movements", "SELECT * FROM dbo.movements WHERE position_id IN " +
            "(SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id)) " +
            "AND shipment_id IS NULL ORDER BY id"),
        ("acts", "SELECT * FROM dbo.acts WHERE contract_id=@id ORDER BY id"),
        ("act_lines", "SELECT * FROM dbo.act_lines WHERE act_id IN " +
            "(SELECT id FROM dbo.acts WHERE contract_id=@id) ORDER BY id"),
        ("shipment_lines", "SELECT * FROM dbo.shipment_lines WHERE position_id IN " +
            "(SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id)) " +
            "AND shipment_id NOT IN (SELECT id FROM dbo.shipments WHERE fixed=1) ORDER BY id"),
        ("smr_costs", "SELECT * FROM dbo.smr_costs WHERE contract_id=@id ORDER BY id"),
        ("spec_revisions", "SELECT * FROM dbo.spec_revisions WHERE contract_id=@id ORDER BY id"),
    };

    private static readonly string SnapshotSql = string.Join(";\n", SnapshotQueries.Select(q => q.Sql));

    public static UndoSnapshot? Snapshot(SqlConnection conn, SqlTransaction? tx, int contractId)
    {
        var tables = new Dictionary<string, List<Dictionary<string, object?>>>();
        using var cmd = Exec(conn, tx, SnapshotSql, contractId);
        using var reader = cmd.ExecuteReader();
        for (var i = 0; i < SnapshotQueries.Length; i++)
        {
            if (i != 0 && !reader.NextResult())
                throw new InvalidOperationException("Неполный снимок Undo: " + SnapshotQueries[i].Key);
            var key = SnapshotQueries[i].Key;
            tables[key] = ReadRows(reader, key == "procurement" ? ProcurementSkip : null);
            if (i == 0 && tables[key].Count == 0) return null;
        }
        if (reader.NextResult())
            throw new InvalidOperationException("Неожиданный результат SQL в снимке Undo.");
        return new UndoSnapshot(contractId, tables);
    }

    /// <summary>Отменить/повторить под одной транзакцией: сравнение снимка, восстановление, журнал, верификация.</summary>
    public static UndoApplyResult Apply(string connectionString, int contractId,
        UndoSnapshot target, UndoSnapshot? expectedCurrent, bool undo)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (contractId <= 0 || target.ContractId != contractId ||
            expectedCurrent is null || expectedCurrent.ContractId != contractId)
            return new UndoApplyResult(UndoApplyStatus.Failed, null, null);

        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);

        // Match every other editor writer's lock order. Without the meta.rev lock
        // a second client can mutate rows between comparing and restoring a snapshot.
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null)
            return new UndoApplyResult(UndoApplyStatus.Failed, null, null);
        var now = Snapshot(conn, tx, contractId);
        if (now is null)
            return new UndoApplyResult(UndoApplyStatus.Failed, null, null);
        if (!now.Equals(expectedCurrent))
            return new UndoApplyResult(UndoApplyStatus.Moved, null, now);

        // Restore currently rebuilds the document tree. Never delete and recreate
        // an immutable signed act, a fixed delivery, or a stage-linked document.
        // A revision check alone cannot establish permission to rewrite these.
        using (var guard = new SqlCommand(@"
            SELECT TOP(1) c.id FROM dbo.contracts AS c WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.objects AS o WITH (HOLDLOCK) ON o.id=c.object_id
            WHERE c.id=@id AND (
                c.locked=1 OR o.archived=1 OR c.stage_of IS NOT NULL OR
                EXISTS(SELECT 1 FROM dbo.contracts s WHERE s.stage_of=c.id) OR
                EXISTS(SELECT 1 FROM dbo.acts a WHERE a.contract_id=c.id AND a.signed=1) OR
                EXISTS(
                    SELECT 1 FROM dbo.shipment_lines sl
                    JOIN dbo.shipments sh ON sh.id=sl.shipment_id
                    JOIN dbo.positions p ON p.id=sl.position_id
                    JOIN dbo.systems sy ON sy.id=p.system_id
                    WHERE sy.contract_id=c.id AND sh.fixed=1) OR
                EXISTS(
                    SELECT 1 FROM dbo.movements m
                    JOIN dbo.positions p ON p.id=m.position_id
                    JOIN dbo.systems sy ON sy.id=p.system_id
                    WHERE sy.contract_id=c.id AND m.shipment_id IS NOT NULL))",
            conn, tx))
        {
            guard.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            if (guard.ExecuteScalar() is not null)
                return new UndoApplyResult(UndoApplyStatus.Failed, null, now);
        }
        bool commitAttempted = false;
        try
        {
            Restore(conn, tx, contractId, target);
            using (var log = new SqlCommand(
                "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details) " +
                "VALUES (@ts,@user,N'contract',@id,@action,@details)", conn, tx))
            {
                log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
                log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = ChangeLog.CurrentUser;
                log.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
                log.Parameters.Add("@action", SqlDbType.NVarChar, 32).Value = undo ? "undo" : "redo";
                log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = undo ? "Ctrl+Z" : "Ctrl+Y";
                log.ExecuteNonQuery();
            }
            var restored = Snapshot(conn, tx, contractId);
            if (restored is null || !restored.Equals(target))
                throw new InvalidOperationException(
                    "Восстановленный документ не совпал со снимком: " +
                    (restored is null ? "снимок пуст" : SnapshotDifference(target, restored)));
            var committedRevision = SqlServerDatabase.BumpPositionRevision(conn, tx);
            commitAttempted = true;
            tx.Commit();
            return new UndoApplyResult(UndoApplyStatus.Applied, restored, null)
                { Revision = committedRevision };
        }
        catch (Exception ex)
        {
            // A network fault during COMMIT has an uncertain outcome. Do not
            // advertise a safe retry of this non-idempotent restoration.
            if (commitAttempted)
                return new UndoApplyResult(UndoApplyStatus.Indeterminate, null, null)
                    { Error = "Неизвестен результат COMMIT." };
            try { tx.Rollback(); } catch { /* SQL connection may already be lost. */ }
            return new UndoApplyResult(UndoApplyStatus.Failed, null, now)
                { Error = ex.Message };
        }
    }

    // ---------------------------------------------------------------- восстановление

    private static string SnapshotDifference(UndoSnapshot expected, UndoSnapshot actual)
    {
        foreach (var table in expected.TableNames.Union(actual.TableNames))
        {
            var left = expected.Rows(table);
            var right = actual.Rows(table);
            if (left.Count != right.Count)
                return $"{table}: строк {left.Count} → {right.Count}";
            for (var i = 0; i < left.Count; i++)
            {
                foreach (var key in left[i].Keys.Union(right[i].Keys))
                {
                    left[i].TryGetValue(key, out var lv);
                    right[i].TryGetValue(key, out var rv);
                    if (!Equals(lv, rv))
                        return $"{table}[{i}].{key}: {Show(lv)} → {Show(rv)}";
                }
            }
        }
        return "значения совпадают; различается порядок или тип";
        static string Show(object? value) => value is null ? "NULL" : $"{value} ({value.GetType().Name})";
    }

    private static void Restore(SqlConnection conn, SqlTransaction tx, int contractId, UndoSnapshot target)
    {
        var positionIds = IdList(conn, tx,
            "SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id)", contractId);
        var keepMoves = ReadRows(Exec(conn, tx,
            "SELECT * FROM dbo.movements WHERE position_id IN " +
            "(SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id)) " +
            "AND shipment_id IS NOT NULL ORDER BY id", contractId));
        var keepLines = ReadRows(Exec(conn, tx,
            "SELECT * FROM dbo.shipment_lines WHERE position_id IN " +
            "(SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id)) " +
            "AND shipment_id IN (SELECT id FROM dbo.shipments WHERE fixed=1) ORDER BY id", contractId));
        var restored = target.Rows("positions").Select(r => SqlInteger.RequireInt64(r["id"])).ToHashSet();
        if (keepMoves.Concat(keepLines).Any(r => !restored.Contains(SqlInteger.RequireInt64(r["position_id"]))))
            throw new InvalidOperationException("Отмена затёрла бы позицию в закреплённой ведомости.");

        // SQL Server каскадов не обещает: зависимые записи удаляем явно, детей первыми.
        Execute(conn, tx, "DELETE dbo.act_lines WHERE act_id IN " +
            "(SELECT id FROM dbo.acts WHERE contract_id=@id)", contractId);
        foreach (var detail in new[] { "procurement", "movements", "shipment_lines" })
            Execute(conn, tx, $"DELETE dbo.[{detail}] WHERE position_id IN " +
                "(SELECT id FROM dbo.positions WHERE system_id IN " +
                "(SELECT id FROM dbo.systems WHERE contract_id=@id))", contractId);
        // dbo.positions.parent_id uses NO ACTION: remove components first, then roots.
        Execute(conn, tx, "DELETE dbo.positions WHERE parent_id IS NOT NULL AND system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id)", contractId);
        Execute(conn, tx, "DELETE dbo.positions WHERE parent_id IS NULL AND system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id)", contractId);
        Execute(conn, tx, "DELETE dbo.acts WHERE contract_id=@id", contractId);
        Execute(conn, tx, "DELETE dbo.smr_costs WHERE contract_id=@id", contractId);
        Execute(conn, tx, "DELETE dbo.spec_revisions WHERE contract_id=@id", contractId);
        Execute(conn, tx, "DELETE dbo.systems WHERE contract_id=@id", contractId);

        UpdateContract(conn, tx, target);
        InsertRows(conn, tx, "systems", target.Rows("systems").ToList());
        InsertRows(conn, tx, "positions", target.Rows("positions").ToList());
        InsertRows(conn, tx, "procurement", target.Rows("procurement").ToList(), new Dictionary<string, object?>
        {
            ["updated_at"] = DateTime.Now,
            ["updated_by"] = ChangeLog.CurrentUser,
        });
        InsertRows(conn, tx, "movements", keepMoves.Where(r => restored.Contains(SqlInteger.RequireInt64(r["position_id"]))).ToList());
        InsertRows(conn, tx, "shipment_lines", keepLines.Where(r => restored.Contains(SqlInteger.RequireInt64(r["position_id"]))).ToList());
        InsertRows(conn, tx, "movements", target.Rows("movements").ToList());
        InsertRows(conn, tx, "acts", target.Rows("acts").ToList());
        InsertRows(conn, tx, "smr_costs", target.Rows("smr_costs").ToList());
        InsertRows(conn, tx, "spec_revisions", target.Rows("spec_revisions").ToList());
        InsertRows(conn, tx, "act_lines", target.Rows("act_lines").ToList());
        // Строки выдачи возвращаются только в черновики; закреплённые хранят свои (выше).
        var draftShipments = new List<long>();
        using (var draft = new SqlCommand(
            "SELECT id FROM dbo.shipments WHERE fixed=0", conn, tx))
        using (var dr = draft.ExecuteReader())
            while (dr.Read())
                draftShipments.Add(Convert.ToInt64(dr.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        InsertRows(conn, tx, "shipment_lines", target.Rows("shipment_lines")
            .Where(r => draftShipments.Contains(SqlInteger.RequireInt64(r["shipment_id"]))).ToList());
    }

    private static void UpdateContract(SqlConnection conn, SqlTransaction tx, UndoSnapshot target)
    {
        var contract = target.Rows("contract").Single();
        var cols = contract.Keys.Where(k => k != "id").ToList();
        var set = string.Join(", ", cols.Select((c, i) => $"[{c}]=@c{i}"));
        using var cmd = new SqlCommand($"UPDATE dbo.contracts SET {set} WHERE id=@id", conn, tx);
        for (var i = 0; i < cols.Count; i++)
        {
            var value = contract[cols[i]] ?? DBNull.Value;
            SqlParameter parameter;
            if (value is DateTime dateTime)
                parameter = new SqlParameter($"@c{i}", SqlDbType.DateTime2) { Value = dateTime };
            else if (value is DateTimeOffset dateTimeOffset)
                parameter = new SqlParameter($"@c{i}", SqlDbType.DateTimeOffset) { Value = dateTimeOffset };
            else
                parameter = new SqlParameter($"@c{i}", value ?? DBNull.Value);
            cmd.Parameters.Add(parameter);
        }
        cmd.Parameters.AddWithValue("@id", contract["id"] ?? DBNull.Value);
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Документ исчез во время отмены.");
    }

    /// <summary>Вставить строки с исходными id: IDENTITY_INSERT на время операции; подразделы после родителей.</summary>
    private static void InsertRows(SqlConnection conn, SqlTransaction tx, string table,
        List<Dictionary<string, object?>> rows, Dictionary<string, object?>? overrides = null)
    {
        if (rows.Count == 0) return;
        // Дерево (systems/positions) нельзя вставить в произвольном порядке — FK на parent_id.
        var pending = new Queue<Dictionary<string, object?>>(rows);
        var inserted = new HashSet<long?> { null };
        var guard = 0;
        while (pending.Count > 0)
        {
            if (++guard > rows.Count + 1)
                throw new InvalidOperationException("Испорченные ссылки на родителей в снимке.");
            var batch = new List<Dictionary<string, object?>>();
            var count = pending.Count;
            for (var i = 0; i < count; i++)
            {
                var row = pending.Dequeue();
                object? parent = row.TryGetValue("parent_id", out var value) ? value : null;
                var parentKey = parent is long l ? l : (long?)null;
                if (inserted.Contains(parentKey)) batch.Add(row);
                else pending.Enqueue(row);
            }
            if (batch.Count == 0) continue;   // защита циклом выше

            var cols = (overrides is null
                    ? batch[0].Keys
                    : batch[0].Keys.Concat(overrides.Keys))
                .Distinct().ToList();
            using (var on = new SqlCommand($"SET IDENTITY_INSERT dbo.[{table}] ON", conn, tx))
                on.ExecuteNonQuery();
            foreach (var row in batch)
            {
                var names = string.Join(", ", cols.Select(c => $"[{c}]"));
                var pars = string.Join(", ", cols.Select((c, i) => $"@p{i}"));
                using var cmd = new SqlCommand(
                    $"INSERT dbo.[{table}] ({names}) VALUES ({pars})", conn, tx);
                for (var i = 0; i < cols.Count; i++)
                {
                    object? value = overrides is not null && overrides.TryGetValue(cols[i], out var over)
                        ? over : row.TryGetValue(cols[i], out var v) ? v : DBNull.Value;
                    SqlParameter parameter;
                    if (value is DateTime dateTime)
                        parameter = new SqlParameter($"@p{i}", SqlDbType.DateTime2) { Value = dateTime };
                    else if (value is DateTimeOffset dateTimeOffset)
                        parameter = new SqlParameter($"@p{i}", SqlDbType.DateTimeOffset) { Value = dateTimeOffset };
                    else
                        parameter = new SqlParameter($"@p{i}", value ?? DBNull.Value);
                    cmd.Parameters.Add(parameter);
                }
                cmd.ExecuteNonQuery();
            }
            using (var off = new SqlCommand($"SET IDENTITY_INSERT dbo.[{table}] OFF", conn, tx))
                off.ExecuteNonQuery();
            foreach (var row in batch)
                inserted.Add(SqlInteger.RequireInt64(row["id"]));
        }
    }

    // ---------------------------------------------------------------- чтение

    private static SqlCommand Exec(SqlConnection conn, SqlTransaction? tx, string sql, long id)
    {
        var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        return cmd;
    }

    private static List<Dictionary<string, object?>> ReadRows(SqlCommand cmd, string[]? skip = null)
    {
        using (cmd)
        using (var reader = cmd.ExecuteReader())
            return ReadRows(reader, skip);
    }

    private static List<Dictionary<string, object?>> ReadRows(SqlDataReader reader, string[]? skip = null)
    {
        // Resolve field names and skipped columns once per result set, not once per row.
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => (Index: i, Name: reader.GetName(i)))
            .Where(c => skip is null || !skip.Contains(c.Name)).ToArray();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(columns.Length);
            foreach (var column in columns)
            {
                var value = reader.IsDBNull(column.Index) ? null : reader.GetValue(column.Index);
                if (value is byte[]) continue;
                row[column.Name] = value;
            }
            rows.Add(row);
        }
        return rows;
    }

    private static List<long> IdList(SqlConnection conn, SqlTransaction tx, string sql, long id)
    {
        var list = new List<long>();
        using var cmd = Exec(conn, tx, sql, id);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        return list;
    }

    private static void Execute(SqlConnection conn, SqlTransaction tx, string sql, long id)
    {
        using var cmd = Exec(conn, tx, sql, id);
        cmd.ExecuteNonQuery();
    }
}
