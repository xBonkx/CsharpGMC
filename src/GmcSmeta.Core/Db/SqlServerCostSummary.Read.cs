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

    private const string SummaryPositionsSelectSql =
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
        "AND ISNULL(p.out_of_kp,0)=0";
    private const string SummaryPositionsOrderSql = " ORDER BY sy.[order], sy.id, p.[order], p.id";
    private const string SummaryPositionsSql = SummaryPositionsSelectSql + SummaryPositionsOrderSql;

    private const string SummaryComponentsSelectSql =
        "SELECT p.parent_id, p.system_id, p.qty, p.price, p.purchase_price, p.out_of_kp, pr.cost_price " +
        "FROM dbo.positions p " +
        "JOIN dbo.systems sy ON sy.id=p.system_id " +
        "JOIN dbo.contracts c ON c.id=sy.contract_id " +
        "LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " +
        "WHERE sy.contract_id=@id " +
        "AND (p.parent_id IS NOT NULL OR ISNULL(p.out_of_kp,0)=1)";
    private const string SummaryComponentsOrderSql = " ORDER BY p.[order], p.id";
    private const string SummaryComponentsSql = SummaryComponentsSelectSql + SummaryComponentsOrderSql;

    private const string SummaryExpensesSql =
        "SELECT ISNULL(SUM(amount),0) FROM dbo.smr_costs WHERE contract_id=@id";

    private const string SummaryTransportSql =
        "SELECT transport_amount, transport_cost FROM dbo.contracts WHERE id=@id";

    private const string CostSummaryReadBatchSql =
        SummarySystemsSql + ";" + SummaryPositionsSql + ";" +
        SummaryComponentsSql + ";" + SummaryExpensesSql + ";" + SummaryTransportSql + ";";

    private static CostSummaryRow MapSummaryPosition(SqlDataReader r)
    {
        string Text(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
        decimal? Dec(int i) => r.IsDBNull(i)
            ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
        return new CostSummaryRow(
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
            Dec(11));
    }

    private static CostSummaryRow FoldSummaryComponent(CostSummaryRow owner, decimal qty, decimal cost) =>
        owner with { MCost = decimal.Round(owner.MCost + qty * cost, 2), Kids = true };

    public static CostSummaryData Summary(SqlConnection conn, int contractId) =>
        Summary(conn, contractId, out _, out _);

    /// <summary>Cancellable read-only summary on a guarded SQL connection.</summary>
    public static CostSummaryData Summary(SqlConnection conn, int contractId, CancellationToken token) =>
        Summary(conn, contractId, out _, out _, token);

    internal static CostSummaryData Summary(SqlConnection conn, int contractId,
        out ContractTotals? documentTotals, out TagRules rules, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var readTiming = StartupTrace.Measure("cost_summary_sql_and_mapping");
        using var command = new SqlCommand(CostSummaryReadBatchSql, conn);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var cancellation = SqlReadCancellation.Register(command, token);
        using var r = (SqlDataReader)SqlReadCancellation.ExecuteReader(command, token);

        void Next()
        {
            token.ThrowIfCancellationRequested();
            var hasNext = r.NextResult();
            token.ThrowIfCancellationRequested();
            if (!hasNext)
                throw new InvalidOperationException("Неполный пакет себестоимости SQL Server.");
        }

        var systems = new List<(long Id, string Label, bool IsSub)>();
        var systemParents = new Dictionary<long, long>();
        {
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                var isSub = !r.IsDBNull(2);
                systems.Add((id, r.IsDBNull(1) ? "" : r.GetString(1), isSub));
                if (isSub) systemParents[id] = Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture);
            }
        }

        var rows = new List<CostSummaryRow>();
        Next();
        {
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                rows.Add(MapSummaryPosition(r));
            }
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
                token.ThrowIfCancellationRequested();
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
                rows[index] = FoldSummaryComponent(owner, qty, cost.Value);
            }
        }

        // Pure projection: assigning to List<T>[i] during foreach invalidates its enumerator.
        // Child component costs have already been folded into their parent's MCost.
        // FinalizeRows already materializes the financial projection.
        token.ThrowIfCancellationRequested();
        IReadOnlyList<CostSummaryRow> finalized;
        using (StartupTrace.Measure("cost_summary_financial_projection"))
            finalized = FinalizeRows(rows);

        var extra = new List<(string, decimal, decimal, string)>();
        using var smrTiming = StartupTrace.Measure("cost_smr_expense_projection");
        decimal other = 0m;
        Next();
        {
            if (r.Read())
                other = Money.FromDouble(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
        }
        if (other != 0m) extra.Add(("Прочие затраты на монтаж", other, 0m, "works"));
        if (outCost != 0m) extra.Add(("Куплено вне КП", decimal.Round(outCost, 2), 0m, "mat"));

        smrTiming?.Dispose();
        using var transportTiming = StartupTrace.Measure("cost_transport_projection");
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

        transportTiming?.Dispose();
        // СМР документа — из проверенного Totals (Contract.stats()["smr"] эталона).
        // Release the reader before the existing contract totals query. No MARS required.
        token.ThrowIfCancellationRequested();
        r.Close();
        readTiming?.Dispose();
        token.ThrowIfCancellationRequested();
        rules = new TagRules();
        using var totalsTiming = StartupTrace.Measure("cost_summary_contract_totals");
        documentTotals = contractId > 0
            ? Totals.ReadSqlBatch(conn, new ContractReadScope(new[] { contractId }), out rules, token: token).SingleOrDefault()
            : null;
        token.ThrowIfCancellationRequested();
        return new CostSummaryData(systems, finalized, extra, notes, documentTotals?.Smr ?? 0m)
            { SystemParents = systemParents };
    }
}
