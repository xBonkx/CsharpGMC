using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerDatabase
{
    /// <summary>
    /// SQL Server port of builder.make_subposition: turn a position into a component
    /// of the position above it (makeSub=true) or back to a top-level row (false).
    /// </summary>
    public static SaveResult MakeSubposition(string connectionString, long contractId,
        long positionId, bool makeSub, string expectedRevision)
    {
        if (contractId <= 0 || positionId <= 0 || !DragPilotDatabase(connectionString))
            return SaveResult.Fail("Вложенность разрешено менять только в GmcSmetaPilot_*.", expectedRevision);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx);
        if (revision is null)
            return SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Данные изменены другим сотрудником. Сначала обнови смету.", revision);

        var contract = DragDocumentGuard(conn, tx, contractId);
        if (contract.Problem is not null)
            return SaveResult.Fail(contract.Problem, revision);

        // Read the position and verify it belongs to this contract.
        long systemId;
        long? parentId;
        long order;
        using (var cmd = new SqlCommand(
            "SELECT p.system_id, p.parent_id, p.[order], s.contract_id, p.out_of_kp, p.extra_json " +
            "FROM dbo.positions p JOIN dbo.systems s ON s.id = p.system_id " +
            "WHERE p.id = @id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            using var r = cmd.ExecuteReader();
            if (!r.Read())
                return SaveResult.Fail("Позиция не найдена.", revision);
            systemId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            parentId = r.IsDBNull(1) ? (long?)null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
            order = Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture);
            var posContractId = Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture);
            var outside = !r.IsDBNull(4) && Convert.ToBoolean(r.GetValue(4), CultureInfo.InvariantCulture);
            var extra = r.IsDBNull(5) ? "" : r.GetString(5);
            r.Close();
            if (outside || Area.IsSummary(extra))
                return SaveResult.Fail("Вложенность вне сметы и сводных строк м² менять нельзя.", revision);
            if (posContractId != contractId)
                return SaveResult.Fail("Позиция не принадлежит этому документу.", revision);
        }

        // A change of hierarchy affects amounts and document grouping. Refuse
        // linked rows and their parents; all checks occur under the same transaction.
        bool Protected(long id)
        {
            using var check = new SqlCommand(
                "SELECT TOP(1) p.id FROM dbo.positions p WHERE (p.id=@id OR p.parent_id=@id) AND (" +
                "EXISTS(SELECT 1 FROM dbo.act_lines a WHERE a.position_id=p.id) OR " +
                "EXISTS(SELECT 1 FROM dbo.procurement q WHERE q.position_id=p.id) OR " +
                "EXISTS(SELECT 1 FROM dbo.movements m WHERE m.position_id=p.id) OR " +
                "EXISTS(SELECT 1 FROM dbo.shipment_lines sh WHERE sh.position_id=p.id))", conn, tx);
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            return check.ExecuteScalar() is not null;
        }

        if (Protected(positionId) || (parentId is long parent && Protected(parent)))
            return SaveResult.Fail("Вложенность затрагивает КС, закупки или выдачу. Данные не изменены.", revision);

        bool NormalParent(long id)
        {
            using var check = new SqlCommand(
                "SELECT out_of_kp,extra_json FROM dbo.positions WHERE id=@id AND system_id=@sys AND parent_id IS NULL", conn, tx);
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            check.Parameters.Add("@sys", SqlDbType.BigInt).Value = systemId;
            using var reader = check.ExecuteReader();
            return reader.Read() && (reader.IsDBNull(0) || !Convert.ToBoolean(reader.GetValue(0))) &&
                !Area.IsSummary(reader.IsDBNull(1) ? "" : reader.GetString(1));
        }
        if (parentId is long originalParent && !NormalParent(originalParent))
            return SaveResult.Fail("Комплектующие сводной строки м² или вне сметы нельзя отделять этой командой.", revision);

        if (makeSub && parentId is null)
        {
            // Children check.
            using (var cmd = new SqlCommand(
                "SELECT COUNT_BIG(*) FROM dbo.positions WHERE parent_id = @id", conn, tx))
            {
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
                var children = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (children > 0)
                    return SaveResult.Fail("Нельзя сделать компонентом: у строки есть свои компоненты.", revision);
            }
            // Top-level position above this one.
            long? aboveId = null;
            using (var cmd = new SqlCommand(
                "SELECT TOP (1) id FROM dbo.positions " +
                "WHERE system_id = @sys AND parent_id IS NULL AND ([order] < @ord OR ([order]=@ord AND id<@id)) " +
                "ORDER BY [order] DESC,id DESC", conn, tx))
            {
                cmd.Parameters.Add("@sys", SqlDbType.BigInt).Value = systemId;
                cmd.Parameters.Add("@ord", SqlDbType.BigInt).Value = order;
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
                var res = cmd.ExecuteScalar();
                if (res is not null && res != DBNull.Value)
                    aboveId = Convert.ToInt64(res, CultureInfo.InvariantCulture);
            }
            if (aboveId is null)
                return SaveResult.Fail("Нет строки выше, чтобы сделать компонентом.", revision);
            if (!NormalParent(aboveId.Value) || Protected(aboveId.Value))
                return SaveResult.Fail("Строка выше защищена или не является обычной позицией.", revision);
            using (var cmd = new SqlCommand(
                "UPDATE dbo.positions SET parent_id = @parent WHERE id = @id", conn, tx))
            {
                cmd.Parameters.Add("@parent", SqlDbType.BigInt).Value = aboveId.Value;
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
                cmd.ExecuteNonQuery();
            }
        }
        else if (!makeSub && parentId is not null)
        {
            using (var cmd = new SqlCommand(
                "UPDATE dbo.positions SET parent_id = NULL WHERE id = @id", conn, tx))
            {
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
                cmd.ExecuteNonQuery();
            }
        }
        else
        {
            return SaveResult.Fail(makeSub
                ? "Строка уже является компонентом."
                : "Строка уже верхнего уровня.", revision);
        }

        // Renumber: top-level positions sorted by [order], each followed by its children.
        // Build ordered id list.
        var ordered = new List<long>();
        using (var cmd = new SqlCommand(
            "SELECT id, parent_id FROM dbo.positions WHERE system_id = @sys ORDER BY [order],id", conn, tx))
        {
            cmd.Parameters.Add("@sys", SqlDbType.BigInt).Value = systemId;
            using var r = cmd.ExecuteReader();
            var tops = new List<long>();
            var children = new Dictionary<long, List<long>>();
            while (r.Read())
            {
                var id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                var pid = r.IsDBNull(1) ? (long?)null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                if (pid is null) tops.Add(id);
                else
                {
                    if (!children.TryGetValue(pid.Value, out var list)) { list = new List<long>(); children[pid.Value] = list; }
                    list.Add(id);
                }
            }
            r.Close();
            // tops already in [order] order due to ORDER BY; children appended in [order] order.
            // Re-sort tops/children by original order already preserved by query order.
            // Need original order map; re-query order for sorting.
            var orderMap = new Dictionary<long, long>();
            using (var cmd2 = new SqlCommand(
                "SELECT id, [order] FROM dbo.positions WHERE system_id = @sys", conn, tx))
            {
                cmd2.Parameters.Add("@sys", SqlDbType.BigInt).Value = systemId;
                using var r2 = cmd2.ExecuteReader();
                while (r2.Read())
                    orderMap[Convert.ToInt64(r2.GetValue(0), CultureInfo.InvariantCulture)] =
                        Convert.ToInt64(r2.GetValue(1), CultureInfo.InvariantCulture);
            }
            foreach (var top in tops.OrderBy(x => orderMap[x]))
            {
                ordered.Add(top);
                if (children.TryGetValue(top, out var kids))
                    foreach (var k in kids.OrderBy(x => orderMap[x]))
                        ordered.Add(k);
            }
        }
        for (var i = 0; i < ordered.Count; i++)
        {
            using var cmd = new SqlCommand(
                "UPDATE dbo.positions SET [order] = @ord WHERE id = @id", conn, tx);
            cmd.Parameters.Add("@ord", SqlDbType.BigInt).Value = i;
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = ordered[i];
            cmd.ExecuteNonQuery();
        }

        LogPositionChange(conn, tx, positionId, contractId, contract.ObjectId, "update",
            makeSub ? "Позиция стала комплектующим" : "Позиция стала обычной строкой");
        var updated = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, updated);
    }
}
