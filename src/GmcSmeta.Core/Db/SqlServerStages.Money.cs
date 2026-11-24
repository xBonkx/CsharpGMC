using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Stage-specific finance editor, isolated from the shared cost/SMR modules.
/// Uses their stored amounts and the existing parent-money synchronization.
/// </summary>
public static partial class SqlServerStages
{
    public sealed record StageMoneyInfo(long StageId, long FullId, string Name,
        string Mode, decimal Smr, decimal Transport, decimal Materials,
        decimal? ContractTarget, bool SignedStageExists, bool IsRest, bool Locked)
    {
        public bool Independent => SignedStageExists || ContractTarget is not null;
        public bool CanEdit => !IsRest && !Locked && ContractTarget is null;
    }

    public sealed record StageSystemMoney(long Id, string Name, decimal Amount);

    private static decimal Amount(SqlDataReader reader, int field) =>
        reader.IsDBNull(field) ? 0m :
        Money.FromDouble(Convert.ToDouble(reader.GetValue(field), CultureInfo.InvariantCulture));

    private static bool HasSignedStage(SqlConnection conn, SqlTransaction tx, long fullId)
    {
        using var check = new SqlCommand(
            "SELECT TOP(1) 1 FROM dbo.contracts WHERE stage_of=@full " +
            "AND ISNULL(stage_rest,0)=0 AND stage_sum IS NOT NULL", conn, tx);
        check.Parameters.Add("@full", SqlDbType.BigInt).Value = fullId;
        return check.ExecuteScalar() is not null;
    }

    public static StageMoneyInfo ReadStageMoney(string connectionString, long stageId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        long fullId;
        bool rest, locked;
        string name, mode;
        decimal smr, transport, smrPercent;
        decimal? target;
        using (var read = new SqlCommand(
            "SELECT stage_of,stage_rest,locked,name,smr_mode,smr_amount,transport_amount,stage_sum,smr_percent " +
            "FROM dbo.contracts WHERE id=@stage", conn, tx))
        {
            read.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
            using var r = read.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0))
                throw new InvalidOperationException("Этап больше не существует.");
            fullId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            rest = !r.IsDBNull(1) && r.GetBoolean(1);
            locked = !r.IsDBNull(2) && r.GetBoolean(2);
            name = r.IsDBNull(3) ? "" : r.GetString(3);
            mode = r.IsDBNull(4) ? "" : r.GetString(4);
            smr = Amount(r, 5);
            transport = Amount(r, 6);
            target = r.IsDBNull(7) ? null : Amount(r, 7);
            smrPercent = Amount(r, 8);
        }
        var materials = MaterialsOf(conn, tx, stageId);
        if (mode == "percent")
            smr = Money.Round2(materials * smrPercent / 100m);
        else if (mode == "pos")
        {
            using var sum = new SqlCommand(
                "SELECT COALESCE(SUM(p.qty * ISNULL(p.work_price,0)),0) " +
                "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
                "WHERE sy.contract_id=@stage AND p.parent_id IS NULL AND p.out_of_kp=0",
                conn, tx);
            sum.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
            smr = Money.Round2(Money.FromDouble(Convert.ToDouble(
                sum.ExecuteScalar() ?? 0d, CultureInfo.InvariantCulture)));
        }
        else if (mode == "systems")
        {
            using var sum = new SqlCommand(
                "SELECT COALESCE(SUM(smr_amount),0) FROM dbo.systems WHERE contract_id=@stage",
                conn, tx);
            sum.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
            smr = Money.FromDouble(Convert.ToDouble(sum.ExecuteScalar() ?? 0d, CultureInfo.InvariantCulture));
        }
        var signed = HasSignedStage(conn, tx, fullId);
        tx.Commit();
        return new StageMoneyInfo(stageId, fullId, name, mode,
            smr, transport, materials, target, signed, rest, locked);
    }

    /// <summary>
    /// Set the contractual target once; a non-null target then freezes the
    /// contractual stage. When any stage is signed, SMR/transport belong to
    /// the stage rather than being redistributed from the full КП.
    /// </summary>
    public static void SaveStageMoney(string connectionString, long fullId, long stageId,
        decimal smr, decimal transport, decimal? target, string expectedRevision)
    {
        if (smr < 0m || transport < 0m || smr > NumberText.MaxNumber ||
            transport > NumberText.MaxNumber ||
            target is < 0m || target > NumberText.MaxNumber)
            throw new ArgumentOutOfRangeException(nameof(smr), "Недопустимые денежные значения.");

        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        RequireEditableStage(conn, tx, fullId, stageId, StageMutationPolicy.Operation.Target);
        var independent = HasSignedStage(conn, tx, fullId) || target is not null;
        using (var update = new SqlCommand(
            "UPDATE dbo.contracts SET stage_sum=@target, " +
            "smr_mode=CASE WHEN @own=1 AND smr_mode<>N'systems' THEN N'sum' ELSE smr_mode END, " +
            "smr_amount=CASE WHEN @own=1 THEN @smr ELSE smr_amount END, " +
            "transport_amount=CASE WHEN @own=1 THEN @tr ELSE transport_amount END " +
            "WHERE id=@stage", conn, tx))
        {
            update.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
            update.Parameters.Add("@target", SqlDbType.Float).Value =
                target is null ? DBNull.Value : (object)(double)target.Value;
            update.Parameters.Add("@own", SqlDbType.Bit).Value = independent;
            update.Parameters.Add("@smr", SqlDbType.Float).Value = (double)Money.Round2(smr);
            update.Parameters.Add("@tr", SqlDbType.Float).Value = (double)Money.Round2(transport);
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Этап больше не существует.");
        }
        if (!independent) RefreshMoney(conn, tx, fullId, stageId);
        SyncParentMoney(conn, tx, fullId);
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
    }

    public static IReadOnlyList<StageSystemMoney> ReadStageSystems(string connectionString,
        long stageId)
    {
        var list = new List<StageSystemMoney>();
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT id,name,ISNULL(smr_amount,0) FROM dbo.systems " +
            "WHERE contract_id=@stage ORDER BY [order],id", conn);
        cmd.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new StageSystemMoney(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                r.IsDBNull(1) ? "" : r.GetString(1), Amount(r, 2)));
        return list;
    }

    public static void SaveStageSystems(string connectionString, long fullId, long stageId,
        IReadOnlyDictionary<long, decimal> amounts, string expectedRevision)
    {
        if (amounts.Count == 0 || amounts.Any(x => x.Key <= 0 || x.Value < 0m ||
                                                  x.Value > NumberText.MaxNumber))
            throw new ArgumentException("Некорректный список систем или сумм.");

        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        RequireEditableStage(conn, tx, fullId, stageId, StageMutationPolicy.Operation.Quantity);
        if (!HasSignedStage(conn, tx, fullId))
            throw new InvalidOperationException(
                "Сначала укажите сумму по договору одного из этапов.");

        var actual = new List<long>();
        using (var list = new SqlCommand(
            "SELECT id FROM dbo.systems WITH (UPDLOCK,HOLDLOCK) WHERE contract_id=@stage",
            conn, tx))
        {
            list.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
            using var r = list.ExecuteReader();
            while (r.Read())
                actual.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        }
        if (actual.Count != amounts.Count || actual.Any(id => !amounts.ContainsKey(id)))
            throw new InvalidOperationException("Состав систем этапа изменился. Обновите окно.");

        using (var update = new SqlCommand(
            "UPDATE dbo.systems SET smr_amount=@amount WHERE id=@id AND contract_id=@stage",
            conn, tx))
        {
            update.Parameters.Add("@amount", SqlDbType.Float);
            update.Parameters.Add("@id", SqlDbType.BigInt);
            update.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
            foreach (var entry in amounts)
            {
                update.Parameters["@amount"].Value = (double)Money.Round2(entry.Value);
                update.Parameters["@id"].Value = entry.Key;
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Система больше не принадлежит этапу.");
            }
        }
        var total = Money.Round2(amounts.Values.Sum(x => Money.Round2(x)));
        using (var update = new SqlCommand(
            "UPDATE dbo.contracts SET smr_mode=N'systems',smr_amount=@total WHERE id=@id",
            conn, tx))
        {
            update.Parameters.Add("@total", SqlDbType.Float).Value = (double)total;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
            update.ExecuteNonQuery();
        }
        SyncParentMoney(conn, tx, fullId);
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
    }
}
