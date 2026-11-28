using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Режим СМР редактора сметы (kp_editor._save_smr_mode): «сумма» и «по поз.»
/// (против «процент»/«по системам» и m²-сворачивания — они не перенесены).
/// Переход в «по поз.» переносит цены работ из сводной (copy_summary_work_prices);
/// переход в «сумму» сохраняет текущие работы как введённую сумму (c.smr()).
/// Стадии с «Суммой по договору» блокируют смену (money_from_stages).
/// </summary>
public static class SqlServerSmrMode
{

    /// <summary>
    /// Read-only preview. Does not change totals, formulas, signed acts or meta.rev.
    /// A subsequent save MUST re-evaluate the entire snapshot inside its own
    /// SERIALIZABLE transaction; the preview alone is never an authorization.
    /// </summary>
    public static SmrModeSwitchPlan PreviewSmrMode(string connectionString, long contractId,
        string targetMode)
    {
        if (contractId <= 0) throw new ArgumentOutOfRangeException(nameof(contractId));
        RequirePilot(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        VerifyActualDatabase(conn, connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx)
            ?? throw new InvalidOperationException("Отсутствует meta.rev.");
        var snapshot = ReadSnapshot(conn, tx, contractId, rev);
        var plan = SmrModeSwitchPlanning.Build(snapshot, targetMode);
        tx.Commit();
        return plan;
    }

    public static SqlServerDatabase.SaveResult SetSmrMode(string connectionString, long contractId,
        string mode, string expectedRevision)
    {
        if (contractId <= 0 || mode is not ("sum" or "pos" or "percent" or "systems"))
            return SqlServerDatabase.SaveResult.Fail("Недопустимый документ или целевой режим СМР.", expectedRevision);
        RequirePilot(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        VerifyActualDatabase(conn, connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null)
            return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail(
                "Данные изменены другим сотрудником. Сначала обнови смету.", rev);

        // No trust in a preview taken before another client's edits: repeat
        // every predicate under the SAME transaction used for the UPDATE.
        var snapshot = ReadSnapshot(conn, tx, contractId, rev);
        var plan = SmrModeSwitchPlanning.Build(snapshot, mode);
        if (!plan.Allowed)
            return SqlServerDatabase.SaveResult.Fail(plan.RefusalReason ?? "Смена режима запрещена.", rev);
        if (plan.IsNoOp)
        {
            tx.Commit();
            return new SqlServerDatabase.SaveResult(true, null, rev);
        }

        // SAFE is deliberately narrower than Python's destructive conversion.
        // Persist only the mode; smr_amount, work_price, work_sell, all formulas,
        // purchase prices, transport, PNR and act_lines stay byte-for-byte intact.
        long objectId;
        using (var read = new SqlCommand(
            "SELECT object_id FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            var value = read.ExecuteScalar();
            if (value is null or DBNull) throw new DBConcurrencyException("Документ исчез.");
            objectId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        using (var update = new SqlCommand(
            "UPDATE dbo.contracts SET smr_mode=@mode WHERE id=@id AND smr_mode=@old", conn, tx))
        {
            update.Parameters.Add("@mode", SqlDbType.NVarChar, 8).Value = mode;
            update.Parameters.Add("@old", SqlDbType.NVarChar, 8).Value = snapshot.CurrentMode;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Режим документа изменился во время записи.");
        }
        // An operation marker lets a separate connection distinguish a committed
        // transaction from a lost acknowledgement. No automatic repeat of UPDATE.
        var operationId = Guid.NewGuid().ToString("N");
        Log(conn, tx, contractId, objectId,
            "SAFE metadata-only SMR: " + plan.CurrentMode + " → " + plan.TargetMode +
            "; work_price/work_sell/formulas/smr_amount untouched; rev=" + rev +
            "; op=" + operationId);
        try
        {
            var next = SqlServerTransactionFinalizer.BumpAndCommit(
                () => SqlServerDatabase.BumpPositionRevision(conn, tx), tx.Commit);
            return new SqlServerDatabase.SaveResult(true, null, next);
        }
        catch (Exception ex)
        {
            // Release any remaining locks before opening the independent
            // readback connection; commit may have succeeded or rolled back.
            try { tx.Dispose(); } catch { /* outcome still unknown */ }
            try { conn.Dispose(); } catch { /* outcome still unknown */ }
            return ReconcileAfterUnknownCommit(connectionString, contractId, rev, mode,
                operationId, ex);
        }
    }

    private static SqlServerDatabase.SaveResult ReconcileAfterUnknownCommit(
        string connectionString, long contractId, string previousRevision,
        string expectedMode, string operationId, Exception originalError)
    {
        try
        {
            using var conn = SqlServerDatabase.Open(connectionString);
            VerifyActualDatabase(conn, connectionString);
            using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
            var now = SqlServerDatabase.LockRevision(conn, tx);
            string? mode;
            long auditCount;
            using (var read = new SqlCommand(
                "SELECT smr_mode, (SELECT COUNT_BIG(*) FROM dbo.change_log " +
                "WHERE contract_id=@id AND details LIKE @marker) " +
                "FROM dbo.contracts WHERE id=@id", conn, tx))
            {
                read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
                read.Parameters.Add("@marker", SqlDbType.NVarChar, 64).Value =
                    "%op=" + operationId + "%";
                using var r = read.ExecuteReader();
                if (!r.Read())
                    return SqlServerDatabase.SaveResult.Fail(
                        "COMMIT неизвестен: договор отсутствует при проверке.", now ?? previousRevision);
                mode = r.IsDBNull(0) ? "sum" : r.GetString(0);
                auditCount = r.IsDBNull(1) ? 0 : SqlInteger.ReadInt64(r, 1);
            }
            tx.Commit();
            if (auditCount == 1 && mode == expectedMode && now is not null &&
                now != previousRevision)
                return new SqlServerDatabase.SaveResult(true, null, now);
            return SqlServerDatabase.SaveResult.Fail(
                "COMMIT не подтверждён reconciliation: режим=" + mode +
                ", записей журнала=" + auditCount +
                ". Автоповтор запрещён; обновите данные и проверьте журнал.",
                now ?? previousRevision);
        }
        catch (Exception readbackError)
        {
            return SqlServerDatabase.SaveResult.Fail(
                "Неизвестный результат COMMIT. Автоповтор запрещён. " +
                "Ошибка записи: " + originalError.Message +
                "; readback: " + readbackError.Message, previousRevision);
        }
    }

    private static void RequirePilot(string connectionString)
    {
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (string.IsNullOrEmpty(database) ||
            !database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Смена режима СМР разрешена только в GmcSmetaPilot_*.");
    }

    private static void VerifyActualDatabase(SqlConnection conn, string connectionString)
    {
        var requested = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!string.Equals(conn.Database, requested, StringComparison.OrdinalIgnoreCase) ||
            !conn.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Фактическая SQL-база не совпала с тестовой базой подключения.");
    }

    private static SmrModeSwitchSnapshot ReadSnapshot(
        SqlConnection conn, SqlTransaction tx, long contractId, string revision)
    {
        string current;
        bool locked, stageChild;
        decimal stored;
        using (var read = new SqlCommand(
            "SELECT smr_mode,locked,stage_of,smr_amount FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id",
            conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Документ не найден.", nameof(contractId));
            current = r.IsDBNull(0) ? "sum" : r.GetString(0);
            locked = !r.IsDBNull(1) && Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture);
            stageChild = !r.IsDBNull(2);
            stored = r.IsDBNull(3) ? 0m : Convert.ToDecimal(r.GetValue(3), CultureInfo.InvariantCulture);
        }

        bool Exists(string sql)
        {
            using var command = new SqlCommand(sql, conn, tx);
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            return command.ExecuteScalar() is not null;
        }
        var stageMoney = Exists("SELECT TOP(1) 1 FROM dbo.contracts WITH (HOLDLOCK) " +
            "WHERE stage_of=@id AND stage_sum IS NOT NULL AND ISNULL(stage_rest,0)=0");
        var acts = Exists("SELECT TOP(1) 1 FROM dbo.acts a WITH (HOLDLOCK) " +
            "WHERE a.contract_id=@id AND (a.signed=1 OR EXISTS " +
            "(SELECT 1 FROM dbo.act_lines al WITH (HOLDLOCK) WHERE al.act_id=a.id))");
        var linkedDs = Exists("SELECT TOP(1) 1 FROM dbo.contracts WITH (HOLDLOCK) " +
            "WHERE (id=@id AND base_contract_id IS NOT NULL) OR base_contract_id=@id");

        var reprices = 0;
        var formulaRisks = 0;
        using (var rows = new SqlCommand(
            "SELECT p.work_price,p.work_sell,p.formulas FROM dbo.positions p WITH (HOLDLOCK) " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@id AND p.parent_id IS NULL AND ISNULL(p.out_of_kp,0)=0",
            conn, tx))
        {
            rows.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = rows.ExecuteReader();
            while (r.Read())
            {
                var price = r.IsDBNull(0) ? 0d :
                    Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture);
                if (!r.IsDBNull(1) &&
                    price != Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture))
                    reprices++;
                if (r.IsDBNull(2) || string.IsNullOrWhiteSpace(r.GetString(2))) continue;
                try
                {
                    var node = JsonNode.Parse(r.GetString(2));
                    if (node is not JsonObject objectNode)
                        throw new FormatException("Формулы строки не являются JSON-объектом.");
                    if (objectNode.ContainsKey("work_price")) formulaRisks++;
                }
                catch (JsonException ex)
                {
                    throw new FormatException("Нельзя проверить формулы СМР перед сменой режима.", ex);
                }
            }
        }
        var positionSmr = Convert.ToDecimal(WorkSum(conn, tx, contractId));
        return new SmrModeSwitchSnapshot(current, revision, locked, stageChild, stageMoney,
            acts, linkedDs, stored, positionSmr, reprices, formulaRisks);
    }

    /// <summary>СМР «по поз.»: Σ qty × цена работы верхних строк без «вне КП» (System.work_sum → Contract.smr).</summary>
    private static double WorkSum(SqlConnection conn, SqlTransaction tx, long contractId)
    {
        double sum = 0;
        using var cmd = new SqlCommand(
            "SELECT p.qty, p.work_price FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@id AND p.parent_id IS NULL AND p.out_of_kp=0", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var qty = r.IsDBNull(0) ? 0d : Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture);
            var work = r.IsDBNull(1) ? 0d : Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture);
            sum += qty * work;
        }
        return FinanceReader.PythonRound(sum);
    }

    private static void Log(SqlConnection conn, SqlTransaction tx, long contractId,
        long objectId, string details)
    {
        using var cmd = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'contract',@id,N'update',@details,@contract,@object)", conn, tx);
        cmd.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
        cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        cmd.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        cmd.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
        cmd.ExecuteNonQuery();
    }
}
