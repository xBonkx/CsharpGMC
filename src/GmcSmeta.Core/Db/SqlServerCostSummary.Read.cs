using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerCostSummary
{
    // One network command, five ordered result sets. Keep the SELECT
    // projections and row order identical to the separate reads.
    private const string SummarySystemsSql =
        "SELECT id,name,parent_id FROM dbo.systems WHERE contract_id=@id ORDER BY [order],id";

    private const string SummaryPositionsSql =
        "SELECT p.id, p.system_id, p.name, p.unit, p.qty, p.price, p.purchase_price, " +
        "p.work_cost, p.work_sell, p.out_of_kp, p.parent_id, " +
        "pr.cost_price, sup.name, " +
        "c.smr_mode, p.cost_color, p.cost_note " +
        "FROM dbo.positions p " +
        "JOIN dbo.systems sy ON sy.id=p.system_id " +
        "JOIN dbo.contracts c ON c.id=sy.contract_id " +
        "LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " +
        "LEFT JOIN dbo.suppliers sup ON sup.id=pr.supplier_id " +
        "WHERE sy.contract_id=@id AND p.parent_id IS NULL " +
        "AND ISNULL(p.out_of_kp,0)=0 ORDER BY sy.[order], sy.id, p.[order], p.id";

    private const string SummaryComponentsSql =
        "SELECT p.parent_id, p.system_id, p.qty, p.price, p.purchase_price, p.out_of_kp, pr.cost_price " +
        "FROM dbo.positions p " +
        "JOIN dbo.systems sy ON sy.id=p.system_id " +
        "JOIN dbo.contracts c ON c.id=sy.contract_id " +
        "LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " +
        "WHERE sy.contract_id=@id " +
        "AND (p.parent_id IS NOT NULL OR ISNULL(p.out_of_kp,0)=1) " +
        "ORDER BY p.[order], p.id";

    private const string SummaryExpensesSql =
        "SELECT ISNULL(SUM(amount),0) FROM dbo.smr_costs WHERE contract_id=@id";

    private const string SummaryTransportSql =
        "SELECT transport_amount, transport_cost FROM dbo.contracts WHERE id=@id";

    private const string CostSummaryReadBatchSql =
        SummarySystemsSql + ";" + SummaryPositionsSql + ";" +
        SummaryComponentsSql + ";" + SummaryExpensesSql + ";" + SummaryTransportSql + ";";

    public static CostSummaryData Summary(SqlConnection conn, int contractId) =>
        Summary(conn, contractId, out _, out _);

    internal static CostSummaryData Summary(SqlConnection conn, int contractId,
        out ContractTotals? documentTotals, out TagRules rules)
    {
        using var command = new SqlCommand(CostSummaryReadBatchSql, conn);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var r = command.ExecuteReader();

        void Next()
        {
            if (!r.NextResult())
                throw new InvalidOperationException("Неполный пакет себестоимости SQL Server.");
        }

        var systems = new List<(long Id, string Label, bool IsSub)>();
        var systemParents = new Dictionary<long, long>();
        {
            while (r.Read())
            {
                var id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                var isSub = !r.IsDBNull(2);
                systems.Add((id, r.IsDBNull(1) ? "" : r.GetString(1), isSub));
                if (isSub) systemParents[id] = Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture);
            }
        }

        var rows = new List<CostSummaryRow>();
        Next();
        {
            string Text(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            decimal? Dec(int i) => r.IsDBNull(i)
                ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
            while (r.Read())
                rows.Add(new CostSummaryRow(
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                    Text(2), Text(3),
                    r.IsDBNull(4) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture)),
                    Text(12),
                    0m, 0m, 0m, 0m,
                    false, false, false, false,
                    EffectiveUnitCost(Dec(11), Dec(6)),
                    r.IsDBNull(5) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture)),
                    Dec(7), Dec(8),
                    !r.IsDBNull(11),
                    r.IsDBNull(6) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(6), CultureInfo.InvariantCulture)),
                    false,
                    EstimateStatistics.IsWorkName(Text(2)), Text(14), Text(15),
                    Dec(11)));
        }

        // Комплектующие: их закуп прибавляется к своей строке; вне КП — в «Куплено вне КП».
        // Lookup by position ID avoids O(positions × components) scans for large estimates.
        var rowIndex = rows.Select((row, index) => (row.Id, index))
            .ToDictionary(entry => entry.Id, entry => entry.index);
        decimal outCost = 0m;
        Next();
        {
            while (r.Read())
            {
                var isOutOfKp = !r.IsDBNull(5) && Convert.ToBoolean(r.GetValue(5), CultureInfo.InvariantCulture);
                var cost = CostOf(r, 6, 4);
                var qty = r.IsDBNull(2) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture));
                if (isOutOfKp)
                {
                    if (cost is not null) outCost += qty * cost.Value;
                    continue;
                }
                if (cost is null) continue;
                if (r.IsDBNull(0)) continue;
                var parentId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                if (!rowIndex.TryGetValue(parentId, out var index)) continue;
                var owner = rows[index];
                rows[index] = owner with
                {
                    MCost = decimal.Round(owner.MCost + qty * cost.Value, 2),
                    Kids = true
                };
            }
        }

        // Pure projection: assigning to List<T>[i] during foreach invalidates its enumerator.
        // Child component costs have already been folded into their parent's MCost.
        // FinalizeRows already materializes the financial projection.
        var finalized = FinalizeRows(rows);

        var extra = new List<(string, decimal, decimal, string)>();
        decimal other = 0m;
        Next();
        {
            if (r.Read())
                other = Money.FromDouble(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
        }
        if (other != 0m) extra.Add(("Прочие затраты на монтаж", other, 0m, "works"));
        if (outCost != 0m) extra.Add(("Куплено вне КП", decimal.Round(outCost, 2), 0m, "mat"));

        decimal transportSell = 0m, transportCost = 0m;
        bool transportCostMissing = true;
        var notes = new List<string>();
        Next();
        {
            if (r.Read())
            {
                transportSell = r.IsDBNull(0) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
                transportCostMissing = r.IsDBNull(1);
                transportCost = transportCostMissing ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
            }
        }
        extra.Add(("Транспорт", transportCost, transportSell, "trans"));
        if (transportSell != 0m && transportCostMissing)
            notes.Add("наш расход на транспорт не вписан");

        // СМР документа — из проверенного Totals (Contract.stats()["smr"] эталона).
        // Release the reader before the existing contract totals query. No MARS required.
        r.Close();
        rules = new TagRules();
        documentTotals = contractId > 0
            ? Totals.ReadSqlBatch(conn, new ContractReadScope(new[] { contractId }), out rules).SingleOrDefault()
            : null;
        return new CostSummaryData(systems, finalized, extra, notes, documentTotals?.Smr ?? 0m)
            { SystemParents = systemParents };
    }
}
