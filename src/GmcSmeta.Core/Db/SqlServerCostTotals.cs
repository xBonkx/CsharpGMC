using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Строка «Итогов» себестоимости — статья и её цена нам/клиенту (cost_totals_view.LINES).</summary>
public sealed record CostTotalsLine(string Key, string Name, decimal? Cost, decimal? Sell,
    bool CostEditable, bool SellEditable);

/// <summary>Данные «Итогов» документа (cost_totals_view.totals).</summary>
public sealed record CostTotalsData(
    IReadOnlyList<CostTotalsLine> Lines, int NoCost, bool VatTop, decimal VatRate,
    decimal Vat, decimal Total, bool Locked, bool SmrEditable);

/// <summary>
/// «Себестоимость → Итоги» (cost_totals_view.totals): по статьям — что стоит нам и
/// что платит клиент; материалы и СМР считаются из «Сводной» (факт/план закупа +
/// работы), ПНР/транспорт/расходные/прочие/непредвиденные — реквизиты документа.
/// </summary>
public static class SqlServerCostTotals
{
    public static CostTotalsData Totals(string connectionString, int contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        return SummaryAndTotals(conn, contractId).Totals;
    }

    // Both projections use one calculation of the contract totals and its tag rules.
    // The caller must fence the entire operation with the SQL revision.
    public static (CostSummaryData Summary, CostTotalsData Totals) SummaryAndTotals(
        SqlConnection conn, int contractId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var summary = SqlServerCostSummary.Summary(conn, contractId, out var documentTotals, out var rules, token);
        return (summary, BuildTotals(conn, contractId, summary, documentTotals, rules, token));
    }

    // Caller supplies the summary from the same revision-checked read.
    public static CostTotalsData Totals(SqlConnection conn, int contractId, CostSummaryData summary,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (contractId <= 0) throw new ArgumentException("Документ не найден.");
        var documentTotals = GmcSmeta.Core.Domain.Totals.ReadSqlBatch(
            conn, new ContractReadScope(new[] { contractId }), out var rules, token: token).SingleOrDefault();
        return BuildTotals(conn, contractId, summary, documentTotals, rules, token);
    }

    private static CostTotalsData BuildTotals(SqlConnection conn, int contractId,
        CostSummaryData summary, ContractTotals? totals, TagRules rules, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        decimal matCost = 0m, smrCost = 0m;
        var noCost = 0;
        foreach (var row in summary.Rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.NoCost) noCost++;
            if (row.Work) smrCost += row.MCost + row.WCost;
            else { matCost += row.MCost; smrCost += row.WCost; }
        }
        foreach (var (_, cost, _, kind) in summary.Extra)
        {
            token.ThrowIfCancellationRequested();
            if (kind == "works") smrCost += cost;
            else if (kind == "mat") matCost += cost;
        }

        bool locked;
        string smrMode = "sum", form = "";
        int? stageOf;
        decimal pnrCost = 0m, pnrAmount = 0m, transportCost = 0m, transportAmount = 0m,
            consumables = 0m, other = 0m, reserve = 0m;
        {
            bool fromStages;
            using (var read = new SqlCommand(
                "SELECT locked, stage_of, smr_mode, form, pnr_cost, pnr_amount, transport_cost, " +
                "transport_amount, consumables_cost, other_cost, reserve_cost FROM dbo.contracts WHERE id=@id", conn))
            {
                read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
                using var cancellation = SqlReadCancellation.Register(read, token);
                using var r = SqlReadCancellation.ExecuteReader(read, token);
                var found = r.Read();
                token.ThrowIfCancellationRequested();
                if (!found) throw new ArgumentException("Документ не найден.");
                locked = Convert.ToBoolean(r.GetValue(0), CultureInfo.InvariantCulture);
                stageOf = r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture);
                smrMode = r.IsDBNull(2) ? "sum" : r.GetString(2);
                form = r.IsDBNull(3) ? "" : r.GetString(3);
                pnrCost = r.IsDBNull(4) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture));
                pnrAmount = r.IsDBNull(5) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture));
                transportCost = r.IsDBNull(6) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(6), CultureInfo.InvariantCulture));
                transportAmount = r.IsDBNull(7) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(7), CultureInfo.InvariantCulture));
                consumables = r.IsDBNull(8) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(8), CultureInfo.InvariantCulture));
                other = r.IsDBNull(9) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(9), CultureInfo.InvariantCulture));
                reserve = r.IsDBNull(10) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(10), CultureInfo.InvariantCulture));
            }
            using (var check = new SqlCommand(
                "SELECT TOP(1) 1 FROM dbo.contracts WHERE stage_of=@id AND stage_sum IS NOT NULL " +
                "AND ISNULL(stage_rest,0)=0", conn))
            {
                check.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
                token.ThrowIfCancellationRequested();
                fromStages = (token.CanBeCanceled
                    ? check.ExecuteScalarAsync(token).GetAwaiter().GetResult()
                    : check.ExecuteScalar()) is not null;
                token.ThrowIfCancellationRequested();
            }
            var smrEditable = !locked && smrMode == "sum" && (stageOf is not null || !fromStages);

            var vatTop = rules.Has(form, "vat_top");
            var vatRate = totals?.VatRate ?? 0m;
            var vat = vatTop ? Math.Round(totals?.Vat ?? 0m, 2) : 0m;
            var total = totals?.Total ?? 0m;

            decimal Round2(decimal v) => decimal.Round(v, 2);
            var lines = new List<CostTotalsLine>
            {
                new("mat", "Материалы", Round2(matCost), totals?.Materials ?? 0m, false, false),
                new("smr", "СМР (монтаж)", Round2(smrCost), totals?.Smr ?? 0m, false, smrEditable),
                new("pnr", "ПНР (пусконаладка)", pnrCost == 0m ? null : pnrCost, pnrAmount, true, !locked),
                new("trans", "Транспортные", transportCost == 0m ? null : transportCost, transportAmount, true, !locked),
                new("consumables", "Расходные материалы", consumables == 0m ? null : consumables, null, true, false),
                new("other", "Прочие расходы", other == 0m ? null : other, null, true, false),
                new("reserve", "Непредвиденные", reserve == 0m ? null : reserve, null, true, false),
            };
            token.ThrowIfCancellationRequested();
            return new CostTotalsData(lines, noCost, vatTop, vatRate, vat, total, locked, smrEditable);
        }
    }

    private static readonly string[] NumericFields =
    {
        "pnr_cost", "transport_cost", "consumables_cost", "other_cost", "reserve_cost",
        "pnr_amount", "transport_amount", "smr_amount", "smr_percent",
    };

    /// <summary>
    /// Записать статью «Итогов» (cost_totals_view._changed → services.update_contract):
    /// подписанная смета блокирует суммы клиенту; smr_amount вводится как ИТОГ СМР —
    /// от введённого значения отнимаются строки монтажа (work_rows_sum).
    /// </summary>
    public static SqlServerDatabase.SaveResult SaveTotalsField(string connectionString, int contractId,
        string field, decimal? value, string expectedRevision)
    {
        if (!NumericFields.Contains(field))
            return SqlServerDatabase.SaveResult.Fail("Эта статья не редактируется.", expectedRevision);
        if (value < 0m)
            return SqlServerDatabase.SaveResult.Fail("Отрицательные расходы или суммы недопустимы.", expectedRevision);
        if (field == "smr_percent")
        {
            value ??= 0m;
            if (value > NumberText.MaxNumber)
                return SqlServerDatabase.SaveResult.Fail("Слишком большой процент СМР.", expectedRevision);
        }
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.InitialCatalog.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Запись разрешена только в GmcSmetaPilot_*.");
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови окно.", rev);

        bool locked, fromStages = false;
        string smrMode = "sum", form = "";
        int? stageOf;
        decimal old = 0m, workRows = 0m;
        using (var read = new SqlCommand(
            "SELECT locked, stage_of, smr_mode, form, pnr_cost, transport_cost, consumables_cost, " +
            "other_cost, reserve_cost, pnr_amount, transport_amount, smr_amount, smr_percent " +
            "FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Документ не найден.", rev);
            locked = Convert.ToBoolean(r.GetValue(0), CultureInfo.InvariantCulture);
            stageOf = r.IsDBNull(1) ? null : Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture);
            smrMode = r.IsDBNull(2) ? "sum" : r.GetString(2);
            form = r.IsDBNull(3) ? "" : r.GetString(3);
            var values = new Dictionary<string, decimal>();
            for (var i = 0; i < 9; i++)
            {
                var name = i switch
                {
                    0 => "pnr_cost", 1 => "transport_cost", 2 => "consumables_cost", 3 => "other_cost",
                    4 => "reserve_cost", 5 => "pnr_amount", 6 => "transport_amount",
                    7 => "smr_amount", _ => "smr_percent",
                };
                values[name] = r.IsDBNull(4 + i) ? 0m
                    : Money.FromDouble(Convert.ToDouble(r.GetValue(4 + i), CultureInfo.InvariantCulture));
            }
            old = values.GetValueOrDefault(field);
        }
        if ((field == "pnr_amount" || field == "transport_amount") && locked)
            return SqlServerDatabase.SaveResult.Fail("Смета подписана — сумму клиенту здесь не поменять", rev);
        if (field is "smr_amount" or "smr_percent")
        {
            using (var stages = new SqlCommand(
                "SELECT TOP(1) 1 FROM dbo.contracts WHERE stage_of=@id AND stage_sum IS NOT NULL " +
                "AND ISNULL(stage_rest,0)=0", conn, tx))
            {
                stages.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
                fromStages = stages.ExecuteScalar() is not null;
            }
            var requiredMode = field == "smr_percent" ? "percent" : "sum";
            var smrEditable = !locked && smrMode == requiredMode && (stageOf is not null || !fromStages);
            if (!smrEditable)
                return SqlServerDatabase.SaveResult.Fail("Этот итог СМР рассчитывается или заблокирован", rev);
        }
        if (field == "smr_amount")
        {
            // «Монтаж …» строки: их розница уже в СМР — введённое значение уходит в смр_amount.
            using (var rows = new SqlCommand(
                "SELECT COALESCE(SUM(ROUND(p.qty*ISNULL(p.price,0),2)),0) FROM dbo.positions p " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "WHERE sy.contract_id=@id AND p.parent_id IS NULL AND p.out_of_kp=0 " +
                "AND p.name LIKE N'Монтаж%'", conn, tx))
            {
                rows.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
                workRows = Money.FromDouble(Convert.ToDouble(rows.ExecuteScalar() ?? 0d, CultureInfo.InvariantCulture));
            }
            var entered = value ?? 0m;
            var target = Math.Round((decimal)entered - workRows, 2, MidpointRounding.AwayFromZero);
            if (target < 0)
                return SqlServerDatabase.SaveResult.Fail(
                    $"Итог СМР не может быть меньше суммы строк монтажа ({Money.Format((decimal)workRows)} ₽)", rev);
            value = (decimal)target;
        }

        var details = $"{field}: «{(old == 0m ? "" : old.ToString(CultureInfo.InvariantCulture))}» → " +
                      $"«{(value is null or 0m ? "" : value.Value.ToString(CultureInfo.InvariantCulture))}»";
        using (var update = new SqlCommand(
            $"UPDATE dbo.contracts SET [{field}]=@v WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@v", SqlDbType.Float).Value =
                value is null ? DBNull.Value : (object)(double)value.Value;
            update.Parameters.AddWithValue("@id", contractId);
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Документ исчез при сохранении итога.");
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'contract',@id,N'update',@details,@contract,NULL)", conn, tx))
        {
            log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            log.Parameters.AddWithValue("@id", contractId);
            log.Parameters.AddWithValue("@details", details);
            log.Parameters.AddWithValue("@contract", contractId);
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }
}
