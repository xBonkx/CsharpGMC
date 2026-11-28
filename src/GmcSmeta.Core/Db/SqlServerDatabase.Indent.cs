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
        if (contractId <= 0 || positionId <= 0 || string.IsNullOrWhiteSpace(expectedRevision) || !DragPilotDatabase(connectionString))
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

        // Resolve the source within the requested contract, then validate the
        // complete affected system before issuing any UPDATE.
        long systemId;
        using (var cmd = new SqlCommand(
            "SELECT p.system_id FROM dbo.positions p WITH(UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.systems s ON s.id=p.system_id JOIN dbo.contracts c ON c.id=s.contract_id " +
            "JOIN dbo.objects o ON o.id=c.object_id WHERE p.id=@id AND s.contract_id=@contract AND o.archived=0", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            var value = cmd.ExecuteScalar();
            if (value is null || value == DBNull.Value)
                return SaveResult.Fail("Позиция не принадлежит активному документу.", revision);
            systemId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        var rows = new List<IndentHierarchyPlan.Row>();
        using (var cmd = new SqlCommand(
            "SELECT id,system_id,parent_id,[order],out_of_kp,extra_json FROM dbo.positions WITH(UPDLOCK,HOLDLOCK) " +
            "WHERE system_id=@sys OR parent_id IN (SELECT id FROM dbo.positions WHERE system_id=@sys) " +
            "ORDER BY [order],id", conn, tx))
        {
            cmd.Parameters.Add("@sys", SqlDbType.BigInt).Value = systemId;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture) != systemId)
                    return SaveResult.Fail("Комплектующее находится в другой системе. Вложенность не изменена.", revision);
                var normal = (reader.IsDBNull(4) || !Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture)) &&
                    !Area.IsSummary(reader.IsDBNull(5) ? "" : reader.GetString(5));
                rows.Add(new IndentHierarchyPlan.Row(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture), normal));
            }
        }
        IndentHierarchyPlan.Result plan;
        try { plan = IndentHierarchyPlan.Build(rows, positionId, makeSub); }
        catch (InvalidOperationException ex) { return SaveResult.Fail(ex.Message, revision); }

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

        if (Protected(positionId) ||
            (plan.OldParentId is long oldParent && Protected(oldParent)) ||
            (plan.NewParentId is long newParent && Protected(newParent)))
            return SaveResult.Fail("Вложенность затрагивает КС, закупки или выдачу. Данные не изменены.", revision);

        using (var cmd = new SqlCommand(
            "UPDATE dbo.positions SET parent_id=@parent WHERE id=@id AND system_id=@sys", conn, tx))
        {
            cmd.Parameters.Add("@parent", SqlDbType.BigInt).Value = (object?)plan.NewParentId ?? DBNull.Value;
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            cmd.Parameters.Add("@sys", SqlDbType.BigInt).Value = systemId;
            if (cmd.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Позиция изменилась. Вложенность не сохранена.");
        }
        for (var i = 0; i < plan.OrderedIds.Count; i++)
        {
            using var cmd = new SqlCommand(
                "UPDATE dbo.positions SET [order]=@ord WHERE id=@id AND system_id=@sys", conn, tx);
            cmd.Parameters.Add("@ord", SqlDbType.BigInt).Value = i;
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = plan.OrderedIds[i];
            cmd.Parameters.Add("@sys", SqlDbType.BigInt).Value = systemId;
            if (cmd.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Состав системы изменился. Вложенность не сохранена.");
        }

        LogPositionChange(conn, tx, positionId, contractId, contract.ObjectId, "update",
            makeSub ? "Позиция стала комплектующим" : "Позиция стала обычной строкой");
        var updated = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, updated);
    }
}
