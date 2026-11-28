using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Position ordering and deletion in the SQL Server-only editor.
/// All operations lock meta.rev first, then the contract and its positions, and commit
/// data + audit trail + revision together. SQL Server foreign keys use NO ACTION.
/// </summary>
public static partial class SqlServerDatabase
{
    private sealed record OrderedPosition(long Id, long? ParentId, int Order, string Name);

    internal static string? LockRevision(SqlConnection conn, SqlTransaction tx)
    {
        using var cmd = new SqlCommand(
            "SELECT [value] FROM dbo.meta WITH (UPDLOCK,HOLDLOCK) WHERE [key]=N'rev'", conn, tx);
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static (long ObjectId, string DocumentName, bool Locked)? LoadPositionContract(
        SqlConnection conn, SqlTransaction tx, long contractId)
    {
        using var cmd = new SqlCommand(
            "SELECT c.object_id,c.name,c.locked FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id " +
            "WHERE c.id=@id AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0",
            conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            reader.IsDBNull(1) ? "" : reader.GetString(1),
            Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture));
    }

    private static List<OrderedPosition> LoadOrderedPositions(
        SqlConnection conn, SqlTransaction tx, long contractId, long systemId)
    {
        using (var verify = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.systems WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE id=@system AND contract_id=@contract", conn, tx))
        {
            verify.Parameters.Add("@system", SqlDbType.BigInt).Value = systemId;
            verify.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (Convert.ToInt64(verify.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("Раздел не принадлежит этому документу.");
        }
        var rows = new List<OrderedPosition>();
        using var cmd = new SqlCommand(
            "SELECT id,parent_id,[order],name FROM dbo.positions WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE system_id=@system ORDER BY [order],id", conn, tx);
        cmd.Parameters.Add("@system", SqlDbType.BigInt).Value = systemId;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new OrderedPosition(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? "" : reader.GetString(3)));
        return rows;
    }

    private static List<OrderedPosition> TreeOrder(IReadOnlyList<OrderedPosition> positions)
    {
        // Python builder.ordered_positions: top rows, each followed by its components.
        var roots = positions.Where(p => p.ParentId is null)
            .OrderBy(p => p.Order).ThenBy(p => p.Id).ToList();
        var children = positions.Where(p => p.ParentId is not null)
            .GroupBy(p => p.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Order).ThenBy(p => p.Id).ToList());
        var ordered = new List<OrderedPosition>(positions.Count);
        foreach (var root in roots)
        {
            ordered.Add(root);
            if (children.TryGetValue(root.Id, out var subs)) ordered.AddRange(subs);
        }
        // No corrupted/orphaned/nested hierarchy can silently be dropped from the new order.
        if (ordered.Count != positions.Count)
            throw new InvalidOperationException("Некорректная вложенность позиций; изменение отменено.");
        return ordered;
    }

    private static void WritePositionOrder(SqlConnection conn, SqlTransaction tx, long systemId,
        IReadOnlyList<OrderedPosition> rows)
    {
        using (var create = new SqlCommand(
            "CREATE TABLE #position_order (id BIGINT NOT NULL PRIMARY KEY, ord INT NOT NULL)", conn, tx))
            create.ExecuteNonQuery();
        // Only a small bounded number of SQL parameters per command even for 2500+ positions.
        for (var offset = 0; offset < rows.Count; offset += 250)
        {
            var batch = rows.Skip(offset).Take(250).ToList();
            using var fill = new SqlCommand { Connection = conn, Transaction = tx };
            var values = new List<string>(batch.Count);
            for (var i = 0; i < batch.Count; i++)
            {
                var suffix = i.ToString(CultureInfo.InvariantCulture);
                values.Add($"(@id{suffix},@ord{suffix})");
                fill.Parameters.Add($"@id{suffix}", SqlDbType.BigInt).Value = batch[i].Id;
                fill.Parameters.Add($"@ord{suffix}", SqlDbType.Int).Value = offset + i;
            }
            fill.CommandText = "INSERT INTO #position_order(id,ord) VALUES " + string.Join(",", values);
            fill.ExecuteNonQuery();
        }
        using var renumber = new SqlCommand(
            "UPDATE p SET [order]=o.ord FROM dbo.positions AS p " +
            "JOIN #position_order AS o ON o.id=p.id WHERE p.system_id=@system", conn, tx);
        renumber.Parameters.Add("@system", SqlDbType.BigInt).Value = systemId;
        if (renumber.ExecuteNonQuery() != rows.Count)
            throw new DBConcurrencyException("Состав строк изменился при перенумерации.");
    }

    private static void LogPositionChange(SqlConnection conn, SqlTransaction tx, long id,
        long contractId, long objectId, string action, string details)
    {
        using var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@time,@user,N'position',@id,@action,@details,@contract,@object)", conn, tx);
        log.Parameters.Add("@time", SqlDbType.DateTime2).Value = DateTime.Now;
        log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
        log.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        log.Parameters.Add("@action", SqlDbType.NVarChar, 32).Value = action;
        log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
        log.ExecuteNonQuery();
    }

    internal static string BumpPositionRevision(SqlConnection conn, SqlTransaction tx)
    {
        var next = $"{Environment.UserName}@{Environment.MachineName}#{Environment.ProcessId}|" +
                   $"{Guid.NewGuid():N}|{DateTime.Now:HH:mm:ss}";
        using var cmd = new SqlCommand(
            "UPDATE dbo.meta SET [value]=@rev WHERE [key]=N'rev'", conn, tx);
        cmd.Parameters.Add("@rev", SqlDbType.NVarChar, -1).Value = next;
        if (cmd.ExecuteNonQuery() != 1)
            throw new DBConcurrencyException("Не удалось обновить meta.rev.");
        return next;
    }

    /// <summary>Move one root position with its components, or one component among its siblings.</summary>
    public static SaveResult MovePosition(string connectionString, long contractId, long systemId,
        long positionId, int direction, string expectedRevision)
    {
        if (contractId <= 0 || systemId <= 0 || positionId <= 0 || direction is not (-1 or 1))
            return SaveResult.Fail("Некорректная команда перемещения.", expectedRevision);
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx);
        if (revision is null) return SaveResult.Fail("Нет meta.rev.", expectedRevision);
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Документ изменён другим сотрудником. Обнови смету.", revision);
        var contract = LoadPositionContract(conn, tx, contractId);
        if (contract is null) return SaveResult.Fail("Документ не найден.", revision);
        if (contract.Value.Locked) return SaveResult.Fail("Подписанную смету менять нельзя.", revision);

        var positions = LoadOrderedPositions(conn, tx, contractId, systemId);
        var row = positions.FirstOrDefault(p => p.Id == positionId);
        if (row is null) return SaveResult.Fail("Позиция не принадлежит выбранному разделу.", revision);
        var ordered = TreeOrder(positions);
        List<OrderedPosition> result;
        if (row.ParentId is long parent)
        {
            var siblings = ordered.Where(p => p.ParentId == parent).ToList();
            var index = siblings.FindIndex(p => p.Id == positionId);
            var target = index + direction;
            if (target < 0 || target >= siblings.Count)
            {
                tx.Commit();
                return new SaveResult(true, null, revision);
            }
            (siblings[index], siblings[target]) = (siblings[target], siblings[index]);
            var next = 0;
            result = new List<OrderedPosition>(ordered.Count);
            foreach (var p in ordered)
                result.Add(p.ParentId == parent ? siblings[next++] : p);
        }
        else
        {
            var roots = ordered.Where(p => p.ParentId is null).ToList();
            var index = roots.FindIndex(p => p.Id == positionId);
            var target = index + direction;
            if (target < 0 || target >= roots.Count)
            {
                tx.Commit();
                return new SaveResult(true, null, revision);
            }
            (roots[index], roots[target]) = (roots[target], roots[index]);
            var children = ordered.Where(p => p.ParentId is not null)
                .GroupBy(p => p.ParentId!.Value).ToDictionary(g => g.Key, g => g.ToList());
            result = new List<OrderedPosition>(ordered.Count);
            foreach (var root in roots)
            {
                result.Add(root);
                if (children.TryGetValue(root.Id, out var subs)) result.AddRange(subs);
            }
        }
        WritePositionOrder(conn, tx, systemId, result);
        LogPositionChange(conn, tx, row.Id, contractId, contract.Value.ObjectId, "move",
            $"{row.Name[..Math.Min(60, row.Name.Length)]}: {(direction < 0 ? "вверх" : "вниз")}");
        var updated = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, updated);
    }

    /// <summary>
    /// Delete a row together with its components. Reject all KS-2 references and issued rows;
    /// explicitly remove the three dependent tables because the pilot's SQL Server FKs are NO ACTION.
    /// </summary>
    public static SaveResult DeletePosition(string connectionString, long contractId,
        long systemId, long positionId, string expectedRevision)
    {
        if (contractId <= 0 || systemId <= 0 || positionId <= 0)
            return SaveResult.Fail("Не выбрана позиция.", expectedRevision);
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx);
        if (revision is null) return SaveResult.Fail("Нет meta.rev.", expectedRevision);
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Документ изменён другим сотрудником. Обнови смету.", revision);
        var contract = LoadPositionContract(conn, tx, contractId);
        if (contract is null) return SaveResult.Fail("Документ не найден.", revision);
        if (contract.Value.Locked) return SaveResult.Fail("Подписанную смету менять нельзя.", revision);
        var rows = LoadOrderedPositions(conn, tx, contractId, systemId);
        var requested = rows.FirstOrDefault(p => p.Id == positionId);
        if (requested is null) return SaveResult.Fail("Позиция не принадлежит разделу.", revision);
        TreeOrder(rows); // Ensure a valid hierarchy before deleting any rows.
        var doomed = new HashSet<long> { positionId };
        if (requested.ParentId is null)
            foreach (var sub in rows.Where(p => p.ParentId == positionId)) doomed.Add(sub.Id);

        using (var table = new SqlCommand("CREATE TABLE #delete_positions(id BIGINT NOT NULL PRIMARY KEY)", conn, tx))
            table.ExecuteNonQuery();
        var ids = doomed.Order().ToList();
        for (var offset = 0; offset < ids.Count; offset += 250)
        {
            using var fill = new SqlCommand { Connection = conn, Transaction = tx };
            var placeholders = new List<string>();
            for (var i = 0; i < Math.Min(250, ids.Count - offset); i++)
            {
                var param = "@p" + i.ToString(CultureInfo.InvariantCulture);
                placeholders.Add("(" + param + ")");
                fill.Parameters.Add(param, SqlDbType.BigInt).Value = ids[offset + i];
            }
            fill.CommandText = "INSERT INTO #delete_positions(id) VALUES " + string.Join(",", placeholders);
            fill.ExecuteNonQuery();
        }

        // Python builder._guard_acts rejects *any* reference, even in an unsigned act.
        using (var check = new SqlCommand(
            "SELECT TOP(1) p.name,a.number FROM dbo.act_lines AS al WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #delete_positions AS d ON d.id=al.position_id " +
            "JOIN dbo.positions AS p ON p.id=al.position_id " +
            "JOIN dbo.acts AS a ON a.id=al.act_id", conn, tx))
        {
            using var reader = check.ExecuteReader();
            if (reader.Read())
            {
                var name = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var number = reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "";
                return SaveResult.Fail($"«{name[..Math.Min(60, name.Length)]}» есть в КС-2 № {number}. Сначала убери строку из акта.", revision);
            }
        }
        using (var check = new SqlCommand(
            "SELECT TOP(1) m.id FROM dbo.movements AS m WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #delete_positions AS d ON d.id=m.position_id WHERE m.shipment_id IS NOT NULL", conn, tx))
            if (check.ExecuteScalar() is not null)
                return SaveResult.Fail("Позиция связана с проведённой выдачей. Проверь ведомость выдачи.", revision);
        using (var check = new SqlCommand(
            "SELECT TOP(1) sl.id FROM dbo.shipment_lines AS sl WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #delete_positions AS d ON d.id=sl.position_id " +
            "JOIN dbo.shipments AS sh ON sh.id=sl.shipment_id WHERE sh.fixed=1", conn, tx))
            if (check.ExecuteScalar() is not null)
                return SaveResult.Fail("Позиция связана с закреплённой ведомостью выдачи.", revision);

        foreach (var table in new[] { "procurement", "movements", "shipment_lines" })
        {
            using var delete = new SqlCommand(
                $"DELETE t FROM dbo.[{table}] AS t JOIN #delete_positions AS d ON d.id=t.position_id",
                conn, tx);
            delete.ExecuteNonQuery();
        }
        // Children first: SQL Server's self-referencing parent_id has NO ACTION.
        using (var deleteSubs = new SqlCommand(
            "DELETE p FROM dbo.positions AS p JOIN #delete_positions AS d ON d.id=p.id " +
            "WHERE p.parent_id IS NOT NULL AND p.id<>@root", conn, tx))
        {
            deleteSubs.Parameters.Add("@root", SqlDbType.BigInt).Value = positionId;
            deleteSubs.ExecuteNonQuery();
        }
        using (var deleteRoot = new SqlCommand(
            "DELETE FROM dbo.positions WHERE id=@id AND system_id=@system", conn, tx))
        {
            deleteRoot.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            deleteRoot.Parameters.Add("@system", SqlDbType.BigInt).Value = systemId;
            if (deleteRoot.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Позиция исчезла во время удаления.");
        }

        // Repack order once without issuing one UPDATE per row.
        using (var renumber = new SqlCommand(
            "WITH ord AS (SELECT id,ROW_NUMBER() OVER (ORDER BY [order],id)-1 AS n " +
            "FROM dbo.positions WHERE system_id=@system) " +
            "UPDATE p SET [order]=CONVERT(INT,ord.n) " +
            "FROM dbo.positions AS p JOIN ord ON ord.id=p.id", conn, tx))
        {
            renumber.Parameters.Add("@system", SqlDbType.BigInt).Value = systemId;
            renumber.ExecuteNonQuery();
        }
        LogPositionChange(conn, tx, positionId, contractId, contract.Value.ObjectId, "delete",
            $"{contract.Value.DocumentName} / {requested.Name[..Math.Min(80, requested.Name.Length)]}");
        var updated = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, updated);
    }
}
