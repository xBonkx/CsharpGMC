using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Atomic, fail-closed deletion of selected out-of-KP ROOT rows.</summary>
public static partial class SqlServerProcurement
{
    public sealed record ExtraDeleteDecision(long PositionId, string Name, string State, string Reason);
    public sealed record ExtraDeletePreview(string Revision, IReadOnlyList<ExtraDeleteDecision> Decisions)
    {
        public int Allowed => Decisions.Count(x => x.State == "READY");
        public int Blocked => Decisions.Count(x => x.State == "BLOCKED");
    }

    public static string? ExtraDeletionBlocker(bool found, bool locked, bool rootExtra,
        bool children, bool inActs, bool movements, bool shipments,
        bool requestedOrPaid, bool manuallyPriced)
    {
        if (!found) return "Позиция не найдена в данном договоре.";
        if (locked) return "Договор/ДС заблокирован.";
        if (!rootExtra) return "Это не корневая дополнительная закупка вне КП.";
        if (children) return "Есть комплектующие; удаляйте их отдельным безопасным сценарием.";
        if (inActs) return "Позиция используется в КС; удаление запрещено.";
        if (movements) return "Есть проведённые движения/выдачи; удаление запрещено.";
        if (shipments) return "Есть ведомости выдачи; удаление запрещено.";
        if (requestedOrPaid) return "Есть заказ, заявка, счёт, оплата или назначенный статус.";
        if (manuallyPriced) return "Есть вручную назначенная цена; удаление запрещено.";
        return null;
    }

    private static ExtraDeleteDecision InspectExtra(
        SqlConnection conn, SqlTransaction? tx, int contractId, long positionId)
    {
        const string failed = "BLOCKED";
        var sql = @"SELECT p.name,c.locked,
            CASE WHEN p.out_of_kp=1 AND p.parent_id IS NULL THEN 1 ELSE 0 END,
            CASE WHEN EXISTS(SELECT 1 FROM dbo.positions ch WITH(UPDLOCK,HOLDLOCK)
                             WHERE ch.parent_id=p.id) THEN 1 ELSE 0 END,
            CASE WHEN EXISTS(SELECT 1 FROM dbo.act_lines a WITH(UPDLOCK,HOLDLOCK)
                             WHERE a.position_id=p.id) THEN 1 ELSE 0 END,
            CASE WHEN EXISTS(SELECT 1 FROM dbo.movements m WITH(UPDLOCK,HOLDLOCK)
                             WHERE m.position_id=p.id) THEN 1 ELSE 0 END,
            CASE WHEN EXISTS(SELECT 1 FROM dbo.shipment_lines sl WITH(UPDLOCK,HOLDLOCK)
                             WHERE sl.position_id=p.id) THEN 1 ELSE 0 END,
            CASE WHEN EXISTS(SELECT 1 FROM dbo.procurement pr WITH(UPDLOCK,HOLDLOCK)
                        WHERE pr.position_id=p.id AND
                        (ISNULL(pr.order_ref,N'')<>N'' OR ISNULL(pr.invoice_ref,N'')<>N'' OR
                         ISNULL(pr.stage,N'')<>N'' OR pr.paid_date IS NOT NULL)) THEN 1 ELSE 0 END,
            CASE WHEN ISNULL(p.price,0)<>0 OR p.purchase_price IS NOT NULL OR p.work_price IS NOT NULL OR p.work_cost IS NOT NULL OR p.work_sell IS NOT NULL OR
                        EXISTS(SELECT 1 FROM dbo.procurement pr WITH(UPDLOCK,HOLDLOCK)
                               WHERE pr.position_id=p.id AND pr.cost_price IS NOT NULL)
                 THEN 1 ELSE 0 END
            FROM dbo.positions p WITH(UPDLOCK,HOLDLOCK)
            JOIN dbo.systems sy ON sy.id=p.system_id
            JOIN dbo.contracts c WITH(UPDLOCK,HOLDLOCK) ON c.id=sy.contract_id
            JOIN dbo.objects o WITH(HOLDLOCK) ON o.id=c.object_id
            WHERE p.id=@id AND sy.contract_id=@contract AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0";
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return new ExtraDeleteDecision(positionId, "", failed,
                ExtraDeletionBlocker(false, false, false, false, false, false, false, false, false)!);
        bool IsTrue(int i) => SqlFlag.Read(r, i);
        var reason = ExtraDeletionBlocker(true, IsTrue(1), IsTrue(2), IsTrue(3),
            IsTrue(4), IsTrue(5), IsTrue(6), IsTrue(7), IsTrue(8));
        return new ExtraDeleteDecision(positionId, r.GetString(0),
            reason is null ? "READY" : failed, reason ?? "Можно безопасно удалить.");
    }

    private static IReadOnlyList<ExtraDeleteDecision> InspectExtras(
        SqlConnection conn, SqlTransaction? tx, int contractId, IReadOnlyList<long> ids)
    {
        return ids.Select(id => InspectExtra(conn, tx, contractId, id)).ToArray();
    }

    public static ExtraDeletePreview PreviewDeleteExtras(string connectionString,
        int contractId, IReadOnlyList<long> positionIds)
    {
        if (positionIds.Count is 0 or > 2500 || positionIds.Any(x => x <= 0) ||
            positionIds.Distinct().Count() != positionIds.Count)
            throw new ArgumentException("Нужны уникальные положительные ID (1–2500).", nameof(positionIds));
        using var conn = SqlServerDatabase.Open(connectionString);
        var revision = SqlServerDatabase.Revision(conn);
        var decisions = InspectExtras(conn, null, contractId, positionIds);
        if (string.IsNullOrEmpty(revision) || SqlServerDatabase.Revision(conn) != revision)
            throw new InvalidOperationException("Данные изменились при подготовке удаления. Повторите предпросмотр.");
        return new ExtraDeletePreview(revision, decisions);
    }

    public static SqlServerDatabase.SaveResult DeleteExtras(string connectionString,
        int contractId, IReadOnlyList<long> positionIds, string expectedRevision)
    {
        if (positionIds.Count is 0 or > 2500 || positionIds.Any(x => x <= 0) ||
            positionIds.Distinct().Count() != positionIds.Count)
            return SqlServerDatabase.SaveResult.Fail("Выберите 1–2500 уникальных дополнительных позиций.", expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        if (!conn.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Запись запрещена: фактическая БД не GmcSmetaPilot_*.");
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null || !string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Ревизия SQL изменилась. Обновите предпросмотр.", rev ?? "");
        // Never rely on SQL Server ON DELETE CASCADE for any related record.
        using (var cascades = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM sys.foreign_keys WHERE referenced_object_id=OBJECT_ID(N'dbo.positions') " +
            "AND delete_referential_action<>0", conn, tx))
        {
            if (SqlInteger.RequireInt64(cascades.ExecuteScalar()) > 0)
                return SqlServerDatabase.SaveResult.Fail(
                    "В SQL-схеме есть каскадные FK на позиции. Удаление заблокировано до отдельной миграции.", rev);
        }
        var plan = InspectExtras(conn, tx, contractId, positionIds);
        var problems = plan.Where(x => x.State == "BLOCKED").Take(4).ToArray();
        if (problems.Length > 0)
            return SqlServerDatabase.SaveResult.Fail(
                "Удаление всей группы отменено: " +
                string.Join("; ", problems.Select(x => $"#{x.PositionId}: {x.Reason}")), rev);
        var systems = new HashSet<long>();
        foreach (var positionId in positionIds)
        {
            long systemId;
            using (var get = new SqlCommand(
                "SELECT p.system_id FROM dbo.positions p WITH(UPDLOCK,HOLDLOCK) " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "WHERE p.id=@id AND sy.contract_id=@contract AND p.out_of_kp=1 AND p.parent_id IS NULL", conn, tx))
            {
                get.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
                get.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                var id = get.ExecuteScalar();
                if (id is null or DBNull)
                    throw new DBConcurrencyException("Доппозиция изменилась после проверки.");
                systemId = Convert.ToInt64(id, CultureInfo.InvariantCulture);
            }
            systems.Add(systemId);
            using (var procurement = new SqlCommand(
                "DELETE FROM dbo.procurement WHERE position_id=@id", conn, tx))
            {
                procurement.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
                procurement.ExecuteNonQuery();
            }
            using (var remove = new SqlCommand(
                "DELETE FROM dbo.positions WHERE id=@id AND out_of_kp=1 AND parent_id IS NULL", conn, tx))
            {
                remove.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
                if (remove.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Строка изменилась при удалении.");
            }
            using var log = new SqlCommand(
                "INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id) " +
                "VALUES(SYSDATETIME(),@user,N'position',@id,N'delete',@details,@contract)", conn, tx);
            log.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = "Пакетное удаление вне КП без движений и связей";
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.ExecuteNonQuery();
        }
        foreach (var systemId in systems)
        {
            using var renumber = new SqlCommand(
                "WITH ordered AS (SELECT id, ROW_NUMBER() OVER(ORDER BY [order],id)-1 AS n " +
                "FROM dbo.positions WHERE system_id=@sys) UPDATE p SET [order]=o.n " +
                "FROM dbo.positions p JOIN ordered o ON o.id=p.id", conn, tx);
            renumber.Parameters.Add("@sys", SqlDbType.BigInt).Value = systemId;
            renumber.ExecuteNonQuery();
        }
        var next = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, next);
    }
}
