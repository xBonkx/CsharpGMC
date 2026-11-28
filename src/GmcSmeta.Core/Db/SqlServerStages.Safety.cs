using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Transaction-local stage protections. These methods never open a second SQL
/// connection: all checks participate in the caller's SERIALIZABLE transaction.
/// </summary>
public static partial class SqlServerStages
{
    private static string? StageGuardProblem(SqlConnection conn, SqlTransaction tx,
        long fullId, long stageId, StageMutationPolicy.Operation operation)
    {
        using var cmd = new SqlCommand(
            "SELECT s.stage_of,s.stage_rest,s.locked,s.stage_sum," +
            "f.locked,f.kind,f.stage_of,o.archived,fo.archived,s.object_id,f.object_id " +
            "FROM dbo.contracts s WITH (UPDLOCK,HOLDLOCK) " +
            "LEFT JOIN dbo.contracts f WITH (UPDLOCK,HOLDLOCK) ON f.id=s.stage_of " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=s.object_id " +
            "LEFT JOIN dbo.objects fo WITH (HOLDLOCK) ON fo.id=f.object_id " +
            "WHERE s.id=@stage", conn, tx);
        cmd.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return "Этап не найден.";
        if (r.IsDBNull(7) || r.IsDBNull(8) || r.IsDBNull(9) || r.IsDBNull(10) ||
            SqlInteger.ReadInt64(r, 9) != SqlInteger.ReadInt64(r, 10))
            return "Этап не принадлежит объекту полного КП.";
        if (SqlFlag.Read(r, 7) ||
            SqlFlag.Read(r, 8))
            return "Архивный объект нельзя изменять.";
        var state = new StageMutationPolicy.State(
            r.IsDBNull(0) ? null : SqlInteger.ReadInt64(r, 0),
            !r.IsDBNull(1) && SqlFlag.Read(r, 1),
            !r.IsDBNull(2) && SqlFlag.Read(r, 2),
            r.IsDBNull(3) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture)),
            !r.IsDBNull(4) && SqlFlag.Read(r, 4),
            r.IsDBNull(5) ? "" : r.GetString(5),
            r.IsDBNull(6) ? null : SqlInteger.ReadInt64(r, 6));
        return StageMutationPolicy.Denial(fullId, state, operation);
    }

    private static void RequireEditableStage(SqlConnection conn, SqlTransaction tx,
        long fullId, long stageId, StageMutationPolicy.Operation operation)
    {
        if (StageGuardProblem(conn, tx, fullId, stageId, operation) is { } problem)
            throw new InvalidOperationException(problem);
    }

    private static long ParentForStage(SqlConnection conn, SqlTransaction tx, long stageId)
    {
        using var cmd = new SqlCommand(
            "SELECT stage_of FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id",
            conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
        var value = cmd.ExecuteScalar();
        if (value is null or DBNull)
            throw new InvalidOperationException("Выбранный документ не является этапом КП.");
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void RequireEditableFull(SqlConnection conn, SqlTransaction tx, long fullId)
    {
        if (FullMutationProblem(conn, tx, fullId, CancellationToken.None) is { } problem)
            throw new InvalidOperationException(problem);
    }

    /// <summary>Advisory UI permission; callers must fence the read with meta.rev.
    /// Writers repeat the same policy with transaction-local locks.</summary>
    public static string? ReadFullStageMutationProblem(SqlConnection conn, long fullId, CancellationToken token) =>
        FullMutationProblem(conn, null, fullId, token);

    private static string? FullMutationProblem(SqlConnection conn, SqlTransaction? tx,
        long fullId, CancellationToken token)
    {
        var contractHint = tx is null ? "" : " WITH (UPDLOCK,HOLDLOCK)";
        var objectHint = tx is null ? "" : " WITH (HOLDLOCK)";
        using var cmd = new SqlCommand(
            "SELECT c.kind,c.stage_of,c.locked,o.archived FROM dbo.contracts c" + contractHint +
            " JOIN dbo.objects o" + objectHint + " ON o.id=c.object_id WHERE c.id=@id",
            conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
        using var cancellation = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        token.ThrowIfCancellationRequested();
        if (!r.Read() || r.IsDBNull(0) ||
            !string.Equals(r.GetString(0), "kp", StringComparison.OrdinalIgnoreCase) ||
            !r.IsDBNull(1))
            return "Это не полное КП. Операция с остатком отменена.";
        if (!r.IsDBNull(2) && SqlFlag.Read(r, 2))
            return "Полное КП подписано или заблокировано.";
        if (r.IsDBNull(3) || SqlFlag.Read(r, 3))
            return "Архивный объект нельзя изменять.";
        return null;
    }

    private static bool HasReference(SqlConnection conn, SqlTransaction tx, string sql, long id)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Refuse to alter any row or component that has already entered a signed
    /// KS, issued shipment or fixed shipment. Existing unsigned KS references
    /// are still protected from deletion by DropStageRow.
    /// </summary>
    private static void RequireUncommittedQuantity(SqlConnection conn, SqlTransaction tx,
        long stageRowId, decimal requestedQuantity)
    {
        if (!StageMutationPolicy.ChangesQuantity(CurrentQty(conn, tx, stageRowId), requestedQuantity))
            return;
        const string positionSet = "(SELECT id FROM dbo.positions WHERE id=@id OR parent_id=@id)";
        if (HasReference(conn, tx,
            "SELECT TOP(1) al.act_id FROM dbo.act_lines al " +
            "JOIN dbo.acts a ON a.id=al.act_id WHERE a.signed=1 AND al.position_id IN " +
            positionSet, stageRowId))
            throw new InvalidOperationException(
                "Объём позиции проведён в подписанном КС-2. Изменение этапа отменено.");
        if (HasReference(conn, tx,
            "SELECT TOP(1) m.id FROM dbo.movements m WHERE m.shipment_id IS NOT NULL " +
            "AND m.position_id IN " + positionSet, stageRowId))
            throw new InvalidOperationException(
                "Позиция уже связана с проведённой выдачей. Изменение объёма запрещено.");
        if (HasReference(conn, tx,
            "SELECT TOP(1) sl.id FROM dbo.shipment_lines sl " +
            "JOIN dbo.shipments sh ON sh.id=sl.shipment_id WHERE sh.fixed=1 " +
            "AND sl.position_id IN " + positionSet, stageRowId))
            throw new InvalidOperationException(
                "Объём позиции входит в закреплённую ведомость выдачи.");
    }

    private static void RequireNoDependentDocuments(SqlConnection conn, SqlTransaction tx, long stageId)
    {
        if (HasReference(conn, tx,
            "SELECT TOP(1) id FROM dbo.contracts WHERE base_contract_id=@id OR stage_of=@id",
            stageId))
            throw new InvalidOperationException(
                "Этап связан с дочерним документом или ДС. Сначала проверь связи документов.");
        if (HasReference(conn, tx,
            "SELECT TOP(1) id FROM dbo.acts WHERE contract_id=@id", stageId))
            throw new InvalidOperationException(
                "У этапа существуют КС-2. Удаление этапа и актов запрещено.");
        const string scopedRows =
            "(SELECT p.id FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@id)";
        if (HasReference(conn, tx,
            "SELECT TOP(1) id FROM dbo.procurement WHERE position_id IN " + scopedRows,
            stageId))
            throw new InvalidOperationException(
                "У этапа есть закупки. Удаление связанных движений запрещено.");
        if (HasReference(conn, tx,
            "SELECT TOP(1) id FROM dbo.movements WHERE position_id IN " + scopedRows,
            stageId))
            throw new InvalidOperationException(
                "У этапа есть движения материалов. Удаление связанных движений запрещено.");
        if (HasReference(conn, tx,
            "SELECT TOP(1) id FROM dbo.shipment_lines WHERE position_id IN " + scopedRows,
            stageId))
            throw new InvalidOperationException(
                "У этапа есть ведомости выдачи. Удаление связанных строк запрещено.");
    }

    /// <summary>
    /// Computed rest is locked against direct edits, not against internal
    /// recomputation. If an act exists for it, do not mutate its row prices or
    /// quantities indirectly when a different stage is edited.
    /// </summary>
    private static void RequireRestNotUsedInActs(SqlConnection conn, SqlTransaction tx, long restId)
    {
        if (HasReference(conn, tx,
            "SELECT TOP(1) id FROM dbo.acts WHERE contract_id=@id", restId))
            throw new InvalidOperationException(
                "«Остаток» связан с КС-2. Автоматическая синхронизация остановлена, данные сохранены.");
    }
}
