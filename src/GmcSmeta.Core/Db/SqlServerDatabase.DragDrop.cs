using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Python builder.place_positions/place_group/place_subsection implemented as
/// server-authoritative, revision-checked SQL Server transactions.
/// Drag-and-drop is enabled only in isolated GmcSmetaPilot_* databases.
/// </summary>
public static partial class SqlServerDatabase
{
    public static SaveResult DragPositions(string connectionString, long contractId,
        IReadOnlyList<long> dragged, long targetSystemId,
        long? afterPositionId, string expectedRevision)
    {
        if (contractId <= 0 || targetSystemId <= 0 ||
            dragged is null || dragged.Count == 0 ||
            dragged.Count > EstimateDragPlan.MaxPositions ||
            dragged.Any(x => x <= 0) || dragged.Distinct().Count() != dragged.Count ||
            afterPositionId is <= 0)
            return SaveResult.Fail("Некорректное выделение или цель перетаскивания.", expectedRevision);
        if (!DragPilotDatabase(connectionString))
            return SaveResult.Fail("Перетаскивание пока разрешено только в GmcSmetaPilot_*.", expectedRevision);

        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx);
        if (revision is null)
            return SaveResult.Fail("В SQL Server отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(expectedRevision, revision, StringComparison.Ordinal))
            return SaveResult.Fail("Смета обновлена другим сотрудником. Повтори перенос после обновления.", revision);

        var contract = DragDocumentGuard(conn, tx, contractId);
        if (contract.Problem is not null)
            return SaveResult.Fail(contract.Problem, revision);
        var systems = DragReadSections(conn, tx, contractId);
        var positions = DragReadPositions(conn, tx, contractId);
        var plan = EstimateDragPlan.PlacePositions(positions, systems, dragged,
            targetSystemId, afterPositionId);
        if (!plan.Changed)
        {
            tx.Commit();
            return new SaveResult(true, null, revision);
        }

        // Single temp table of assignments, batched below SQL Server's 2100
        // parameter limit. Checking ALL assignments also detects data loss
        // in the pure planner; the final update must affect every row.
        using (var cmd = new SqlCommand(
            "CREATE TABLE #drag_positions (id BIGINT PRIMARY KEY, sy BIGINT NOT NULL, ord INT NOT NULL)",
            conn, tx))
            cmd.ExecuteNonQuery();
        for (var offset = 0; offset < plan.Assignments.Count; offset += 200)
        {
            var batch = plan.Assignments.Skip(offset).Take(200).ToArray();
            using var fill = new SqlCommand { Connection = conn, Transaction = tx };
            var placeholders = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                placeholders.Add($"(@id{i},@sy{i},@o{i})");
                fill.Parameters.Add($"@id{i}", SqlDbType.BigInt).Value = batch[i].Id;
                fill.Parameters.Add($"@sy{i}", SqlDbType.BigInt).Value = batch[i].SystemId;
                fill.Parameters.Add($"@o{i}", SqlDbType.Int).Value = batch[i].Order;
            }
            fill.CommandText = "INSERT INTO #drag_positions(id,sy,ord) VALUES " +
                string.Join(",", placeholders);
            fill.ExecuteNonQuery();
        }

        // Reordering in the same system doesn't alter amounts and is allowed
        // even if acts exist; moving between systems can affect system-based
        // SMR and issued documents, so any linked position blocks the move.
        using (var references = new SqlCommand(
            @"SELECT TOP (1) p.id FROM dbo.positions p
              JOIN #drag_positions d ON d.id=p.id
              WHERE p.system_id<>d.sy AND (
                EXISTS(SELECT 1 FROM dbo.act_lines a WHERE a.position_id=p.id) OR
                EXISTS(SELECT 1 FROM dbo.procurement q WHERE q.position_id=p.id) OR
                EXISTS(SELECT 1 FROM dbo.movements m WHERE m.position_id=p.id) OR
                EXISTS(SELECT 1 FROM dbo.shipment_lines s WHERE s.position_id=p.id))",
            conn, tx))
        {
            if (references.ExecuteScalar() is not null)
                return SaveResult.Fail(
                    "Перенос между системами затрагивает КС, закупку или выдачу. " +
                    "Данные не изменены.", revision);
        }

        using (var update = new SqlCommand(
            @"UPDATE p SET system_id=d.sy, [order]=d.ord
              FROM dbo.positions p JOIN #drag_positions d ON d.id=p.id
              JOIN dbo.systems sy ON sy.id=p.system_id
              WHERE sy.contract_id=@contract", conn, tx))
        {
            update.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (update.ExecuteNonQuery() != positions.Count)
                throw new DBConcurrencyException("Не все позиции были перенумерованы; перенос отменён.");
        }

        foreach (var id in plan.MovedRoots)
            LogPositionChange(conn, tx, id, contractId, contract.ObjectId, "move",
                $"Drag-and-drop: позиция → раздел {targetSystemId}, " +
                (afterPositionId is long after ? $"после позиции {after}" : "в начало"));
        var next = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, next);
    }

    public static SaveResult DragSystem(string connectionString, long contractId,
        long draggedSystemId, long? afterSystemId, string expectedRevision) =>
        DragSectionCore(connectionString, contractId, draggedSystemId, null,
            afterSystemId, asSubsection: false, expectedRevision);

    public static SaveResult DragSubsection(string connectionString, long contractId,
        long draggedSectionId, long targetRootSystemId, long? afterSubsectionId,
        string expectedRevision) =>
        DragSectionCore(connectionString, contractId, draggedSectionId,
            targetRootSystemId, afterSubsectionId, asSubsection: true,
            expectedRevision);

    private static SaveResult DragSectionCore(string connectionString, long contractId,
        long draggedId, long? parentId, long? afterId,
        bool asSubsection, string expectedRevision)
    {
        if (contractId <= 0 || draggedId <= 0 ||
            afterId is <= 0 || asSubsection && parentId is not > 0 ||
            !asSubsection && parentId is not null)
            return SaveResult.Fail("Некорректные параметры переноса системы.", expectedRevision);
        if (!DragPilotDatabase(connectionString))
            return SaveResult.Fail("Перетаскивание пока разрешено только в GmcSmetaPilot_*.", expectedRevision);

        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx);
        if (revision is null)
            return SaveResult.Fail("В SQL Server отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(expectedRevision, revision, StringComparison.Ordinal))
            return SaveResult.Fail("Смета изменена другим сотрудником. Обнови и повтори перенос.", revision);
        var contract = DragDocumentGuard(conn, tx, contractId);
        if (contract.Problem is not null)
            return SaveResult.Fail(contract.Problem, revision);

        var sections = DragReadSections(conn, tx, contractId);
        var plan = asSubsection
            ? EstimateDragPlan.PlaceSubsection(sections, draggedId, parentId!.Value, afterId)
            : EstimateDragPlan.PlaceGroup(sections, draggedId, afterId);
        if (!plan.Changed)
        {
            tx.Commit();
            return new SaveResult(true, null, revision);
        }

        // A subsection can change its top-level parent without changing any
        // position.system_id. That still changes system-based financial/KS
        // grouping. Protect linked acts, purchases and issued documents, just
        // as the position-move transaction does for a changed system_id.
        if (asSubsection && EstimateDragPlan.ReparentsSection(sections, plan, draggedId))
        {
            using var links = new SqlCommand(
                @"SELECT TOP (1) p.id FROM dbo.positions p WITH (HOLDLOCK)
                  WHERE p.system_id=@subsection AND (
                    EXISTS (SELECT 1 FROM dbo.act_lines a WHERE a.position_id=p.id) OR
                    EXISTS (SELECT 1 FROM dbo.procurement q WHERE q.position_id=p.id) OR
                    EXISTS (SELECT 1 FROM dbo.movements m WHERE m.position_id=p.id) OR
                    EXISTS (SELECT 1 FROM dbo.shipment_lines s WHERE s.position_id=p.id))",
                conn, tx);
            links.Parameters.Add("@subsection", SqlDbType.BigInt).Value = draggedId;
            if (links.ExecuteScalar() is not null)
                return SaveResult.Fail(
                    "Подраздел содержит позиции с КС, закупками или выдачами. " +
                    "Перенос между системами отменён, данные не изменены.", revision);
        }

        using (var create = new SqlCommand(
            "CREATE TABLE #drag_sections (id BIGINT PRIMARY KEY, parent BIGINT NULL, ord INT NOT NULL)",
            conn, tx))
            create.ExecuteNonQuery();

        for (var offset = 0; offset < plan.Assignments.Count; offset += 250)
        {
            var batch = plan.Assignments.Skip(offset).Take(250).ToArray();
            using var fill = new SqlCommand { Connection = conn, Transaction = tx };
            var values = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                values.Add($"(@id{i},@p{i},@o{i})");
                fill.Parameters.Add($"@id{i}", SqlDbType.BigInt).Value = batch[i].Id;
                fill.Parameters.Add($"@p{i}", SqlDbType.BigInt).Value =
                    batch[i].ParentId is long p ? (object)p : DBNull.Value;
                fill.Parameters.Add($"@o{i}", SqlDbType.Int).Value = batch[i].Order;
            }
            fill.CommandText = "INSERT INTO #drag_sections(id,parent,ord) VALUES " +
                string.Join(",", values);
            fill.ExecuteNonQuery();
        }

        using (var update = new SqlCommand(
            @"UPDATE sy SET parent_id=d.parent, [order]=d.ord
              FROM dbo.systems sy JOIN #drag_sections d ON d.id=sy.id
              WHERE sy.contract_id=@contract", conn, tx))
        {
            update.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (update.ExecuteNonQuery() != sections.Count)
                throw new DBConcurrencyException("Перенос раздела не завершился целиком.");
        }
        using (var log = new SqlCommand(
            "INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES(@ts,@user,N'system',@id,N'move',@details,@contract,@object)", conn, tx))
        {
            log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = draggedId;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = asSubsection
                ? $"Drag-and-drop: подраздел {draggedId} → система {parentId}, после {afterId?.ToString() ?? "начала"}"
                : $"Drag-and-drop: система {draggedId} → после {afterId?.ToString() ?? "начала"}";
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = contract.ObjectId;
            log.ExecuteNonQuery();
        }
        var next = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, next);
    }

    private static bool DragPilotDatabase(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString ?? "").InitialCatalog
                .StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private static (long ObjectId, string? Problem) DragDocumentGuard(
        SqlConnection conn, SqlTransaction tx, long contractId)
    {
        long objectId;
        using (var cmd = new SqlCommand(
            "SELECT object_id,locked,stage_of,kind FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (0, "Документ больше не существует.");
            objectId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            if (Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture))
                return (objectId, "Подписанный договор нельзя перемещать.");
            if (!r.IsDBNull(2))
                return (objectId, "Внутри КП этапа перетаскивание пока запрещено.");
            if (r.IsDBNull(3) || r.GetString(3) is not ("kp" or "main" or "dop"))
                return (objectId, "Недопустимый тип документа для drag-and-drop.");
        }
        using (var stages = new SqlCommand(
            "SELECT TOP (1) id FROM dbo.contracts WITH(HOLDLOCK) WHERE stage_of=@id", conn, tx))
        {
            stages.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            if (stages.ExecuteScalar() is not null)
                return (objectId, "Документ связан с этапами. Перетаскивание структуры пока запрещено.");
        }
        return (objectId, null);
    }

    private static List<EstimateDragPlan.Section> DragReadSections(
        SqlConnection conn, SqlTransaction tx, long contractId)
    {
        var list = new List<EstimateDragPlan.Section>();
        using var cmd = new SqlCommand(
            "SELECT id,parent_id,[order],name FROM dbo.systems WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE contract_id=@id ORDER BY [order],id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new EstimateDragPlan.Section(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                r.IsDBNull(1) ? null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture),
                r.IsDBNull(3) ? "" : r.GetString(3)));
        return list;
    }

    private static List<EstimateDragPlan.Position> DragReadPositions(
        SqlConnection conn, SqlTransaction tx, long contractId)
    {
        var rows = new List<EstimateDragPlan.Position>();
        using var cmd = new SqlCommand(
            "SELECT p.id,p.system_id,p.parent_id,p.[order] FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@id ORDER BY sy.[order],p.[order],p.id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new EstimateDragPlan.Position(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                r.IsDBNull(2) ? null : Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
                Convert.ToInt32(r.GetValue(3), CultureInfo.InvariantCulture)));
        return rows;
    }
}
