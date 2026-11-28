using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed class ExcelExistingUndoCommitUncertainException : Exception
{
    public string OperationId { get; }
    public ExcelExistingUndoCommitUncertainException(string operationId, Exception inner)
        : base("Исход COMMIT отмены Excel неизвестен. Проверь WasUndone; повтор запрещён. ID=" + operationId, inner)
    {
        OperationId = operationId;
    }
}

public static partial class SqlServerExcelExistingUpdate
{
    /// <summary>
    /// Read-only, versioned evidence; readback itself does not authorize rollback.
    /// </summary>
    public static ExcelExistingUndoSnapshot ReadUndoSnapshot(
        string connectionString, long contractId, string operationId)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        ValidateUndoArgs(contractId, operationId);
        using var conn = SqlServerDatabase.Open(connectionString);
        VerifyConnectedPilotTarget(conn, connectionString);
        return LoadUndoSnapshot(conn, null, contractId, operationId);
    }

    /// <summary>Reconcile an uncertain Undo COMMIT without replaying it.</summary>
    public static bool WasUndone(string connectionString, long contractId, string operationId)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        ValidateUndoArgs(contractId, operationId);
        using var conn = SqlServerDatabase.Open(connectionString);
        VerifyConnectedPilotTarget(conn, connectionString);
        return UndoJournalExists(conn, null, contractId, operationId);
    }

    /// <summary>
    /// Strict, versioned rollback for quantity-only Excel batches. For batches which
    /// inserted positions it refuses to delete anything, because later documents,
    /// purchases, child rows or price history can make deletion destructive.
    /// Must only be called after a separate explicit user approval of snapshot details.
    /// </summary>
    public static SqlServerDatabase.SaveResult UndoQuantities(
        string connectionString, long contractId, string operationId,
        string expectedRevision, CancellationToken cancellationToken = default)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        ValidateUndoArgs(contractId, operationId);
        if (string.IsNullOrWhiteSpace(expectedRevision))
            throw new ArgumentException("Не указана текущая ревизия SQL.");
        cancellationToken.ThrowIfCancellationRequested();

        using var conn = SqlServerDatabase.Open(connectionString);
        VerifyConnectedPilotTarget(conn, connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        // Lock global rev first, then read the journal and validate contract/positions.
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        if (UndoJournalExists(conn, tx, contractId, operationId))
            throw new InvalidOperationException("Эта операция Excel уже была отменена. Повтор запрещён.");
        var snapshot = LoadUndoSnapshot(conn, tx, contractId, operationId);
        if (!string.Equals(expectedRevision, snapshot.AfterRevision, StringComparison.Ordinal))
            throw new DBConcurrencyException(
                "После импорта изменились данные/ревизия. Автоматический Undo небезопасен.");
        if (!snapshot.CanUndoQuantities)
            throw new InvalidOperationException(
                "Эта версия импорта добавляла строки либо не меняла количества. " +
                "Автоматическое удаление запрещено; нужен отдельный подтверждённый откат.");

        long objectId;
        using (var contract = new SqlCommand(
            @"SELECT c.object_id,c.locked,o.archived,
                  CASE WHEN c.base_contract_id IS NOT NULL OR c.stage_of IS NOT NULL
                    OR c.stage_rest=1 OR EXISTS(
                        SELECT 1 FROM dbo.contracts d WITH (HOLDLOCK)
                        WHERE d.base_contract_id=c.id OR d.stage_of=c.id)
                    THEN 1 ELSE 0 END
               FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK)
               JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id
               WHERE c.id=@contract", conn, tx))
        {
            contract.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var reader = contract.ExecuteReader();
            if (!reader.Read()) throw new DBConcurrencyException("Договор для Undo отсутствует.");
            objectId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture) ||
                Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture) ||
                Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Связанный или заблокированный договор нельзя откатить.");
        }

        using (var create = new SqlCommand(
            @"CREATE TABLE #excel_undo_qty (
                id BIGINT NOT NULL PRIMARY KEY, sy BIGINT NOT NULL, parent BIGINT NULL,
                old_qty FLOAT NOT NULL, applied_qty FLOAT NOT NULL,
                old_name NVARCHAR(2000) NOT NULL, old_unit NVARCHAR(32) NOT NULL,
                old_price FLOAT NOT NULL, old_purchase_price FLOAT NULL,
                old_work_price FLOAT NULL, old_note NVARCHAR(MAX) NOT NULL,
                old_formula NVARCHAR(MAX) NOT NULL, old_extra NVARCHAR(MAX) NOT NULL,
                old_source_row INT NULL);", conn, tx))
            create.ExecuteNonQuery();
        var table = new DataTable();
        foreach (var (name, type) in new (string, Type)[] {
            ("id",typeof(long)), ("sy",typeof(long)), ("parent",typeof(long)),
            ("old_qty",typeof(double)), ("applied_qty",typeof(double)),
            ("old_name",typeof(string)), ("old_unit",typeof(string)),
            ("old_price",typeof(double)), ("old_purchase_price",typeof(double)),
            ("old_work_price",typeof(double)), ("old_note",typeof(string)),
            ("old_formula",typeof(string)), ("old_extra",typeof(string)),
            ("old_source_row",typeof(int)) })
            table.Columns.Add(name, type);
        foreach (var old in snapshot.QuantityEdits)
            table.Rows.Add(old.Id, old.SystemId,
                old.ParentId is long parent ? parent : DBNull.Value,
                (double)old.BeforeQuantity, (double)old.AppliedQuantity,
                old.Name, old.Unit, (double)old.Price,
                old.PurchasePrice is decimal purchase ? (double)purchase : DBNull.Value,
                old.WorkPrice is decimal work ? (double)work : DBNull.Value,
                old.Note, old.Formulas, old.ExtraJson,
                old.SourceRow is int source ? source : DBNull.Value);

        BulkCopy(conn, tx, "#excel_undo_qty", table);
        cancellationToken.ThrowIfCancellationRequested();
        // A single changed linked document, child, formula, money field or imported
        // provenance invalidates the ENTIRE undo. We never partially revert rows.
        using (var validate = new SqlCommand(
            @"SELECT COUNT_BIG(*) FROM #excel_undo_qty e
              LEFT JOIN dbo.positions p WITH (UPDLOCK,HOLDLOCK) ON p.id=e.id
              LEFT JOIN dbo.systems s WITH (HOLDLOCK) ON s.id=p.system_id
              WHERE p.id IS NULL OR s.contract_id<>@contract OR p.system_id<>e.sy
                OR ISNULL(p.parent_id,-1)<>ISNULL(e.parent,-1)
                OR p.qty<>e.applied_qty OR p.price<>e.old_price
                OR EXISTS(SELECT p.purchase_price,p.work_price
                          EXCEPT SELECT e.old_purchase_price,e.old_work_price)
                OR p.name<>e.old_name OR p.unit<>e.old_unit
                OR ISNULL(p.note,N'')<>e.old_note
                OR ISNULL(p.formulas,N'')<>e.old_formula
                OR ISNULL(p.extra_json,N'')<>e.old_extra
                OR ISNULL(p.source_row,-1)<>ISNULL(e.old_source_row,-1)
                OR (ISNULL(p.source_row,0)<=0 AND
                    ISNULL(p.extra_json,N'') NOT LIKE N'%""src""%')
                OR (ISNULL(p.formulas,N'') NOT IN (N'',N'{}'))
                OR EXISTS(SELECT 1 FROM dbo.positions child WITH (HOLDLOCK) WHERE child.parent_id=e.id)
                OR EXISTS(SELECT 1 FROM dbo.act_lines a WITH (HOLDLOCK) WHERE a.position_id=e.id)
                OR EXISTS(SELECT 1 FROM dbo.shipment_lines sh WITH (HOLDLOCK) WHERE sh.position_id=e.id)
                OR EXISTS(SELECT 1 FROM dbo.procurement q WITH (HOLDLOCK) WHERE q.position_id=e.id)
                OR EXISTS(SELECT 1 FROM dbo.movements m WITH (HOLDLOCK) WHERE m.position_id=e.id)
                OR EXISTS(SELECT 1 FROM dbo.positions st WITH (HOLDLOCK) WHERE st.stage_src=e.id)
                OR p.stage_src IS NOT NULL OR p.parent_id IS NOT NULL
                OR p.out_of_kp=1 OR p.area_manual IS NOT NULL", conn, tx))
        {
            validate.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            validate.CommandTimeout = 180;
            if (SqlInteger.RequireInt64(validate.ExecuteScalar()) != 0)
                throw new DBConcurrencyException(
                    "Undo отказан: количество, цены, формулы, дети, документы или происхождение строки изменились.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        using (var update = new SqlCommand(
            @"UPDATE p SET qty=e.old_qty
              FROM dbo.positions p JOIN #excel_undo_qty e ON e.id=p.id", conn, tx))
        {
            update.CommandTimeout = 180;
            if (update.ExecuteNonQuery() != snapshot.QuantityEdits.Count)
                throw new DBConcurrencyException("Undo: не все количества восстановлены.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        using (var log = new SqlCommand(
            @"INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id)
              VALUES(SYSDATETIME(),@user,N'contract',@contract,
                N'excel_undo_v1',@details,@contract,@obj)", conn, tx))
        {
            CommonParams(log, contractId, objectId, snapshot.PlanId);
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                UndoJournalDetail(operationId);
            if (log.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Undo-журнал не записан.");
        }
        SqlServerWorkspace.BumpRevision(conn, tx);
        string undoneRevision;
        using (var rev = new SqlCommand(
            "SELECT [value] FROM dbo.meta WITH (HOLDLOCK) WHERE [key]=N'rev'", conn, tx))
            undoneRevision = Convert.ToString(rev.ExecuteScalar(), CultureInfo.InvariantCulture) ??
                throw new DBConcurrencyException("Не найдена ревизия после Undo.");
        // After this point never assume a failed acknowledgement means rollback.
        try { tx.Commit(); }
        catch (Exception ex) { throw new ExcelExistingUndoCommitUncertainException(operationId, ex); }
        return new SqlServerDatabase.SaveResult(true, null, undoneRevision);
    }

    private static ExcelExistingUndoSnapshot LoadUndoSnapshot(
        SqlConnection conn, SqlTransaction? tx, long contractId, string operationId)
    {
        using var cmd = new SqlCommand(
            @"SELECT details FROM dbo.change_log WITH (HOLDLOCK)
              WHERE contract_id=@contract AND entity=N'contract'
                AND action=N'excel_snapshot_v1'
                AND CASE WHEN ISJSON(details)=1 THEN JSON_VALUE(details,'$.OperationId') ELSE NULL END=@operation", conn, tx);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        cmd.Parameters.Add("@operation", SqlDbType.NVarChar, 64).Value = operationId;
        string? details = null;
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
            {
                if (details is not null || reader.IsDBNull(0))
                    throw new InvalidOperationException("Повторяющиеся или пустые undo snapshots.");
                details = reader.GetString(0);
            }
        if (details is null)
            throw new InvalidOperationException("Версионированный снимок для Undo не найден.");
        return ExcelExistingUndoSnapshot.Parse(details, operationId, contractId);
    }

    private static bool UndoJournalExists(
        SqlConnection conn, SqlTransaction? tx, long contractId, string operationId)
    {
        using var cmd = new SqlCommand(
            @"SELECT COUNT_BIG(*) FROM dbo.change_log WITH (HOLDLOCK)
              WHERE contract_id=@contract AND entity=N'contract'
                AND action=N'excel_undo_v1' AND details=@details", conn, tx);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        cmd.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = UndoJournalDetail(operationId);
        return SqlInteger.RequireInt64(cmd.ExecuteScalar()) > 0;
    }

    private static string UndoJournalDetail(string operationId) =>
        "ExcelUndo|" + operationId + "|v1";

    private static void ValidateUndoArgs(long contractId, string operationId)
    {
        if (contractId <= 0 || operationId is null || operationId.Length != 64 ||
            !operationId.All(Uri.IsHexDigit))
            throw new ArgumentException("Некорректный contractId или operationId для Excel Undo.");
    }
}
