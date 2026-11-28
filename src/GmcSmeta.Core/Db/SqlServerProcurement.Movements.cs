using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Atomic contract-scoped movements; the original single-row APIs remain available.</summary>
public static partial class SqlServerProcurement
{
    public sealed record MovementInput(long PositionId, decimal Quantity);

    /// <summary>Offline preflight, before any SQL call. Outgoing issues use fixed shipment lists.</summary>
    public static string? ValidateMovementBatch(IReadOnlyList<MovementInput> rows, string kind)
    {
        if (kind is not ("in" or "out" or "back" or "back_supplier"))
            return "Неизвестный вид движения.";
        if (rows is null || rows.Count == 0)
            return "Не выбраны позиции для движения.";
        if (rows.Count > 2500) return "За один раз можно провести не более 2500 позиций.";
        var ids = new HashSet<long>();
        foreach (var row in rows)
        {
            if (row.PositionId <= 0 || !ids.Add(row.PositionId))
                return "В движении есть повторяющиеся или некорректные позиции.";
            if (row.Quantity <= Eps || row.Quantity > 1_000_000_000m ||
                decimal.Round(row.Quantity, 6) != row.Quantity)
                return "Количество должно быть положительным числом (не более 6 знаков после запятой).";
        }
        return null;
    }

    /// <summary>
    /// Apply receipts/returns in ONE SERIALIZABLE transaction, protecting meta.rev and
    /// checking every position belongs to the supplied contract. No automatic retry after COMMIT.
    /// For "out" use IssueViaListBatch, which preserves shipment-linked history.
    /// </summary>
    public static SqlServerDatabase.SaveResult AddMovements(
        string connectionString, int contractId, IReadOnlyList<MovementInput> rows,
        string kind, DateTime? when, string doc, string note, string expectedRevision)
    {
        var validation = ValidateMovementBatch(rows, kind);
        if (validation is not null)
            return SqlServerDatabase.SaveResult.Fail(validation, expectedRevision);
        if (kind == "out")
            return SqlServerDatabase.SaveResult.Fail(
                "Выдача оформляется только через ведомость выдачи.", expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены. Обновите закупки.", rev);
        foreach (var row in rows)
        {
            string? name = null;
            using (var find = new SqlCommand(
                "SELECT p.name FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
                "JOIN dbo.systems s ON s.id=p.system_id " +
                "JOIN dbo.contracts c WITH (HOLDLOCK) ON c.id=s.contract_id " +
                "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id " +
                "WHERE p.id=@position AND s.contract_id=@contract AND c.locked=0 " +
                "AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0", conn, tx))
            {
                find.Parameters.Add("@position", SqlDbType.BigInt).Value = row.PositionId;
                find.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                name = find.ExecuteScalar() as string;
            }
            if (name is null || EstimateStatistics.IsWorkName(name))
                return SqlServerDatabase.SaveResult.Fail(
                    "Позиция не найдена в выбранном договоре. Операция отменена.", rev);
            using (var insert = new SqlCommand(
                "INSERT dbo.movements (position_id,kind,qty,[date],doc,note,[user]) " +
                "VALUES (@position,@kind,@qty,@date,@doc,@note,@user)", conn, tx))
            {
                insert.Parameters.Add("@position", SqlDbType.BigInt).Value = row.PositionId;
                insert.Parameters.Add("@kind", SqlDbType.NVarChar, 30).Value = kind;
                insert.Parameters.Add("@qty", SqlDbType.Float).Value = (double)row.Quantity;
                insert.Parameters.Add("@date", SqlDbType.Date).Value = (object?)when?.Date ?? DBNull.Value;
                insert.Parameters.Add("@doc", SqlDbType.NVarChar, -1).Value = doc ?? "";
                insert.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = note ?? "";
                insert.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
                insert.ExecuteNonQuery();
            }
            using (var log = new SqlCommand(
                "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
                "VALUES (SYSDATETIME(),@user,N'movement',@position,@kind,@details,@contract)", conn, tx))
            {
                log.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
                log.Parameters.Add("@position", SqlDbType.BigInt).Value = row.PositionId;
                log.Parameters.Add("@kind", SqlDbType.NVarChar, 30).Value = kind;
                log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                    $"{name[..Math.Min(60, name.Length)]}: {MovementKindLabel(kind)} {row.Quantity:0.######}" +
                    (when is null ? "" : $" ({when.Value:dd.MM.yyyy})") +
                    (string.IsNullOrWhiteSpace(doc) ? "" : $", {doc}");
                log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                log.ExecuteNonQuery();
            }
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>
    /// Edit an independent movement, never one tied to a fixed issue sheet.
    /// Revision/contract checks and update/log are atomic.
    /// </summary>
    public static SqlServerDatabase.SaveResult EditMovement(
        string connectionString, int contractId, long positionId, long movementId,
        decimal qty, DateTime? when, string doc, string note, string expectedRevision)
    {
        var error = ValidateMovementBatch(new[] { new MovementInput(positionId, qty) }, "in");
        if (error is not null) return SqlServerDatabase.SaveResult.Fail(error, expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены. Обновите закупки.", rev);
        using (var update = new SqlCommand(
            "UPDATE m SET qty=@qty,[date]=@date,doc=@doc,note=@note " +
            "FROM dbo.movements m WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.positions p ON p.id=m.position_id " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "JOIN dbo.contracts c WITH (HOLDLOCK) ON c.id=sy.contract_id " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id " +
            "WHERE m.id=@id AND m.position_id=@position AND sy.contract_id=@contract AND c.locked=0 " +
            "AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0 " +
            "AND m.shipment_id IS NULL", conn, tx))
        {
            update.Parameters.Add("@qty", SqlDbType.Float).Value = (double)qty;
            update.Parameters.Add("@date", SqlDbType.Date).Value = (object?)when?.Date ?? DBNull.Value;
            update.Parameters.Add("@doc", SqlDbType.NVarChar, -1).Value = doc ?? "";
            update.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = note ?? "";
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = movementId;
            update.Parameters.Add("@position", SqlDbType.BigInt).Value = positionId;
            update.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (update.ExecuteNonQuery() != 1)
                return SqlServerDatabase.SaveResult.Fail(
                    "Движение не найдено или связано с ведомостью. Редактирование отменено.", rev);
        }
        using (var log = new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (SYSDATETIME(),@user,N'movement',@id,N'edit',@details,@contract)", conn, tx))
        {
            log.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = movementId;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                $"Изменено движение позиции {positionId}: {qty:0.######}, {when:dd.MM.yyyy}, {doc}";
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>
    /// Multi-line issue through a fixed dated shipment. All lines, backing movements,
    /// direct-to-site flags and revision are committed together, never partially.
    /// Shipment-linked movements cannot be edited/deleted directly.
    /// </summary>
    public static (long ShipmentId, string Revision) IssueViaListBatch(
        string connectionString, int contractId, IReadOnlyList<MovementInput> rows,
        DateTime when, string recipient, bool directToSite, string expectedRevision)
    {
        var error = ValidateMovementBatch(rows, "out");
        if (error is not null) throw new ArgumentException(error);
        if ((recipient?.Length ?? 0) > 255) throw new ArgumentException("Получатель: не более 255 символов.");
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) throw new InvalidOperationException("Отсутствует meta.rev.");
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            throw new DBConcurrencyException("Данные изменены. Обновите закупки.");

        long objectId;
        using (var obj = new SqlCommand(
            "SELECT object_id FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE id=@contract AND locked=0 AND kind IN (N'main',N'dop')", conn, tx))
        {
            obj.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            var value = obj.ExecuteScalar();
            if (value is null or DBNull) throw new InvalidOperationException("Договор не найден.");
            objectId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        // Validate ALL source positions before touching the shipping ledger.
        foreach (var row in rows)
        {
            using var scope = new SqlCommand(
                "SELECT p.name FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "JOIN dbo.contracts c WITH (HOLDLOCK) ON c.id=sy.contract_id " +
                "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id AND ISNULL(o.archived,0)=0 " +
                "AND ISNULL(c.stage_rest,0)=0 " +
                "LEFT JOIN dbo.positions parent ON parent.id=p.parent_id AND parent.system_id=p.system_id " +
                "WHERE p.id=@id AND sy.contract_id=@contract AND c.locked=0 " +
                "AND (p.parent_id IS NULL OR (parent.id IS NOT NULL AND parent.parent_id IS NULL))", conn, tx);
            scope.Parameters.Add("@id", SqlDbType.BigInt).Value = row.PositionId;
            scope.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (scope.ExecuteScalar() is not string name || EstimateStatistics.IsWorkName(name))
                throw new InvalidOperationException("Позиция вне договора; выдача отменена.");
        }

        long shipmentId = 0;
        var created = false;
        using (var find = new SqlCommand(
            "SELECT TOP(1) id FROM dbo.shipments WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE object_id=@object AND fixed=1 AND [date]=@date " +
            "AND (@recipient=N'' OR ISNULL(recipient,N'')=@recipient) ORDER BY id DESC", conn, tx))
        {
            find.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            find.Parameters.Add("@date", SqlDbType.Date).Value = when.Date;
            find.Parameters.Add("@recipient", SqlDbType.NVarChar, 255).Value = recipient ?? "";
            var result = find.ExecuteScalar();
            if (result is not null and not DBNull)
                shipmentId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        }
        if (shipmentId == 0)
        {
            var number = 1;
            using (var nums = new SqlCommand(
                "SELECT number FROM dbo.shipments WITH (UPDLOCK,HOLDLOCK) WHERE object_id=@object", conn, tx))
            {
                nums.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
                using var reader = nums.ExecuteReader();
                while (reader.Read())
                {
                    var raw = (reader.IsDBNull(0) ? "" : reader.GetString(0)).Trim('№', ' ');
                    if (int.TryParse(raw, out var n) && n >= number)
                        number = n + 1;
                }
            }
            using var insert = new SqlCommand(
                "INSERT dbo.shipments (object_id,number,[date],recipient,issued_by,note," +
                "fixed,fixed_at,fixed_by) OUTPUT INSERTED.id " +
                "VALUES (@object,@number,@date,@recipient,N'',N'создана из «Закупок»',1,SYSDATETIME(),@user)", conn, tx);
            insert.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            insert.Parameters.Add("@number", SqlDbType.NVarChar, 50).Value = number.ToString(CultureInfo.InvariantCulture);
            insert.Parameters.Add("@date", SqlDbType.Date).Value = when.Date;
            insert.Parameters.Add("@recipient", SqlDbType.NVarChar, 255).Value = recipient ?? "";
            insert.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            shipmentId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            created = true;
        }

        foreach (var row in rows)
        {
            var current = SqlServerShipments.LineQty(conn, tx, shipmentId, row.PositionId);
            SqlServerShipments.CheckLinkedLine(conn, tx, shipmentId, row.PositionId, true, current);
            var total = Math.Round(current + row.Quantity, 6);
            if (total > 1_000_000_000m) throw new ArgumentException("Количество превышает допустимый предел.");
            if (current <= Eps)
            {
                using var add = new SqlCommand(
                    "INSERT dbo.shipment_lines (shipment_id,position_id,qty) VALUES (@shipment,@id,@qty)", conn, tx);
                add.Parameters.Add("@shipment", SqlDbType.BigInt).Value = shipmentId;
                add.Parameters.Add("@id", SqlDbType.BigInt).Value = row.PositionId;
                add.Parameters.Add("@qty", SqlDbType.Float).Value = (double)total;
                if (add.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Не удалось добавить строку ведомости.");
            }
            else
            {
                using var upd = new SqlCommand(
                    "UPDATE dbo.shipment_lines SET qty=@qty WHERE shipment_id=@shipment AND position_id=@id", conn, tx);
                upd.Parameters.Add("@shipment", SqlDbType.BigInt).Value = shipmentId;
                upd.Parameters.Add("@id", SqlDbType.BigInt).Value = row.PositionId;
                upd.Parameters.Add("@qty", SqlDbType.Float).Value = (double)total;
                if (upd.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Строка ведомости изменилась.");
            }
            SqlServerShipments.SyncFixedLine(conn, tx, shipmentId, row.PositionId, total);
            if (directToSite)
            {
                using var mark = new SqlCommand(
                    "UPDATE dbo.procurement SET direct_to_site=1,updated_at=SYSDATETIME(),updated_by=@user " +
                    "WHERE position_id=@id; " +
                    "IF @@ROWCOUNT=0 INSERT dbo.procurement " +
                    "(position_id,supplier_id,stage,order_ref,invoice_ref,cost_price,due_date," +
                    "replacement,note,direct_to_site,source,updated_at,updated_by) " +
                    "VALUES (@id,NULL,N'',N'',N'',NULL,NULL,N'',N'',1,N'buy',SYSDATETIME(),@user)", conn, tx);
                mark.Parameters.Add("@id", SqlDbType.BigInt).Value = row.PositionId;
                mark.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
                mark.ExecuteNonQuery();
            }
        }
        {
            using var log = new SqlCommand(
                "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,object_id) " +
                "VALUES (SYSDATETIME(),@user,N'shipment',@id,@action,@details,@object)", conn, tx);
            log.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = shipmentId;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                $"{(created ? "создана" : "дополнена")} из «Закупок», {rows.Count} позиций";
            log.Parameters.Add("@action", SqlDbType.NVarChar, 30).Value = created ? "create" : "edit";
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return (shipmentId, updated);
    }

}
