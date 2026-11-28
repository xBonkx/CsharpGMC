using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerStages
{
    /// <summary>Деньги полного КП при разбивке по подписанным этапам (stages.sync_parent_money).</summary>
    private static void SyncParentMoney(SqlConnection conn, SqlTransaction tx, long fullId)
    {
        bool fromStages;
        using (var check = new SqlCommand(
            "SELECT TOP(1) 1 FROM dbo.contracts WHERE stage_of=@id AND stage_sum IS NOT NULL " +
            "AND ISNULL(stage_rest,0)=0", conn, tx))
        {
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
            fromStages = check.ExecuteScalar() is not null;
        }
        if (!fromStages) return;

        var childStages = new List<(long Id, string Mode, decimal Percent,
            decimal StoredSmr, decimal Transport)>();
        using (var stageQuery = new SqlCommand(
            "SELECT id,smr_mode,smr_percent,smr_amount,transport_amount " +
            "FROM dbo.contracts WHERE stage_of=@id ORDER BY id", conn, tx))
        {
            stageQuery.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
            using var reader = stageQuery.ExecuteReader();
            decimal D(int col) => reader.IsDBNull(col) ? 0m :
                Money.FromDouble(Convert.ToDouble(reader.GetValue(col), CultureInfo.InvariantCulture));
            while (reader.Read())
                childStages.Add((
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    D(2), D(3), D(4)));
        }

        decimal smr = 0m, transport = 0m;
        foreach (var stage in childStages)
        {
            decimal contribution = stage.StoredSmr;
            if (stage.Mode == "percent")
            {
                contribution = StageMoneyAllocation.ParentPercent(MaterialsOf(conn, tx, stage.Id), stage.Percent);
            }
            else if (stage.Mode == "systems" || stage.Mode == "pos")
            {
                var query = stage.Mode == "systems"
                    ? "SELECT COALESCE(SUM(smr_amount),0) FROM dbo.systems WHERE contract_id=@stage"
                    : "SELECT COALESCE(SUM(p.qty*ISNULL(p.work_price,0)),0) " +
                      "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
                      "WHERE sy.contract_id=@stage AND p.parent_id IS NULL AND p.out_of_kp=0";
                using var total = new SqlCommand(query, conn, tx);
                total.Parameters.Add("@stage", SqlDbType.BigInt).Value = stage.Id;
                contribution = StageMoneyAllocation.RoundParent(Money.FromDouble(Convert.ToDouble(
                    total.ExecuteScalar() ?? 0d, CultureInfo.InvariantCulture)));
            }
            smr += contribution;
            transport += stage.Transport;
        }
        smr = StageMoneyAllocation.RoundParent(smr);
        transport = StageMoneyAllocation.RoundParent(transport);
        using var update = new SqlCommand(
            "UPDATE dbo.contracts SET smr_mode=N'sum', smr_amount=@smr, transport_amount=@tr WHERE id=@id", conn, tx);
        update.Parameters.Add("@smr", SqlDbType.Float).Value = (double)smr;
        update.Parameters.Add("@tr", SqlDbType.Float).Value = (double)transport;
        update.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
        update.ExecuteNonQuery();
    }

    /// <summary>СМР/транспорт/ПНР этапа пропорционально материалам (stages.refresh_money);
    /// «по поз.» и «процент» считают сами; этап с «Суммой по договору» сохраняет свои суммы.</summary>
    private static void RefreshMoney(SqlConnection conn, SqlTransaction tx, long fullId, long stageId)
    {
        bool signed, isComputedRest;
        decimal? stageSum;
        string fullMode = "sum";
        using (var read = new SqlCommand(
            "SELECT stage_sum, locked, stage_rest FROM dbo.contracts WHERE id=@stage", conn, tx))
        {
            read.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
            using var r = read.ExecuteReader();
            if (!r.Read()) return;
            stageSum = r.IsDBNull(0) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
            signed = !r.IsDBNull(1) && Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture);
            isComputedRest = !r.IsDBNull(2) &&
                Convert.ToBoolean(r.GetValue(2), CultureInfo.InvariantCulture);
        }
        // A computed «Остаток» is locked for users but MUST still have its
        // material-based transport/SMR recalculated by our internal writer.
        if (stageSum is not null || (signed && !isComputedRest)) return;
        using (var read = new SqlCommand("SELECT smr_mode FROM dbo.contracts WHERE id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
            fullMode = Convert.ToString(read.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "sum";
        }
        using (var stages = new SqlCommand(
            "SELECT TOP(1) 1 FROM dbo.contracts WHERE stage_of=@id AND stage_sum IS NOT NULL " +
            "AND ISNULL(stage_rest,0)=0", conn, tx))
        {
            stages.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
            if (stages.ExecuteScalar() is not null) return;
        }
        // The «percent» and «pos» SMR modes calculate their own SMR,
        // but transport and PNR still follow their share of materials.
        // The corresponding SQL update below leaves smr_amount alone.

        // Доли по материалам — из проверенного Totals (Contract.material_sum эталона).
        decimal fullMaterials, stageMaterials, fullSmr, fullTransport, fullPnr;
        using (var totals = new SqlCommand(
            "SELECT smr_amount, transport_amount, pnr_amount FROM dbo.contracts WHERE id=@id", conn, tx))
        {
            totals.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
            using var r = totals.ExecuteReader();
            if (!r.Read()) return;
            fullSmr = r.IsDBNull(0) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
            fullTransport = r.IsDBNull(1) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
            fullPnr = r.IsDBNull(2) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture));
        }
        fullMaterials = MaterialsOf(conn, tx, fullId);
        stageMaterials = MaterialsOf(conn, tx, stageId);
        var share = StageMoneyAllocation.Share(fullMaterials, stageMaterials);
        if (fullMode == "systems") RefreshSystemMoney(conn, tx, fullId, stageId);
        var updateSmr = fullMode == "sum";
        using (var update = new SqlCommand(
            "UPDATE dbo.contracts SET " +
            (updateSmr ? "smr_amount=@smr, " : "") +
            "transport_amount=@tr, pnr_amount=@pnr WHERE id=@id2", conn, tx))
        {
            if (updateSmr)
                update.Parameters.Add("@smr", SqlDbType.Float).Value =
                    (double)StageMoneyAllocation.Proportional(fullSmr, share);
            update.Parameters.Add("@tr", SqlDbType.Float).Value = (double)StageMoneyAllocation.Proportional(fullTransport, share);
            update.Parameters.Add("@pnr", SqlDbType.Float).Value = (double)StageMoneyAllocation.Proportional(fullPnr, share);
            update.Parameters.Add("@id2", SqlDbType.BigInt).Value = stageId;
            update.ExecuteNonQuery();
        }
    }

    private static decimal MaterialsOf(SqlConnection conn, SqlTransaction tx, long contractId)
    {
        // Totals.ReadOne reads with a separate DbCommand and cannot be used
        // while conn has an active SQL Server transaction. Compute the same
        // material rows (and form-specific round_rows rule) within this tx.
        var roundRows = false;
        using (var form = new SqlCommand(
            "SELECT t.rules FROM dbo.contracts c LEFT JOIN dbo.tags t ON t.[key]=c.form " +
            "WHERE c.id=@id", conn, tx))
        {
            form.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            var json = Convert.ToString(form.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using var parsed = System.Text.Json.JsonDocument.Parse(json);
                    roundRows = parsed.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        parsed.RootElement.TryGetProperty("round_rows", out var option) &&
                        option.ValueKind == System.Text.Json.JsonValueKind.True;
                }
                catch (System.Text.Json.JsonException) { /* same as TagRules.Load */ }
            }
        }
        decimal materials = 0m;
        using (var rows = new SqlCommand(
            "SELECT p.qty,p.price FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@id AND p.parent_id IS NULL AND p.out_of_kp=0", conn, tx))
        {
            rows.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var reader = rows.ExecuteReader();
            while (reader.Read())
            {
                var qty = reader.IsDBNull(0) ? 0m :
                    Money.FromDouble(Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture));
                var price = reader.IsDBNull(1) ? 0m :
                    Money.FromDouble(Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture));
                materials += StageMoneyAllocation.MaterialLine(qty, price, roundRows);
            }
        }
        return Money.Round2(materials);
    }

}
