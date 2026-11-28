using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerCostSummary
{
    // These writers touch one top-level cost row and its procurement entry.
    // Customer price, quantity, name, unit, transport, group and structure
    // writes can affect stages/areas/other rows and keep the full readback.
    public static bool SupportsCostRowDelta(string field) =>
        field is "supplier" or "purchase_price" or "work_cost" or "work_sell";

    public static bool CanPatchCostRow(CostSummaryRow previous, CostSummaryRow observed, string field)
    {
        var normalized = field switch
        {
            "supplier" => observed with { Supplier = previous.Supplier },
            "purchase_price" => observed with { Plan = previous.Plan, UCost = previous.UCost,
                MCost = previous.MCost, NoCost = previous.NoCost },
            "work_cost" => observed with { UWcost = previous.UWcost, WCost = previous.WCost,
                NowCost = previous.NowCost },
            "work_sell" => observed with { UWsell = previous.UWsell, WSell = previous.WSell,
                NowSell = previous.NowSell },
            _ => null
        };
        return normalized == previous;
    }

    /// <summary>Caller supplies the pre-write confirmed summary and the exact
    /// successful receipt revision. Any unexpected field, identity, missing row
    /// or revision requires full readback, never a guessed local monetary patch.</summary>
    public static CostSummaryData? ReadChangedCostRow(SqlConnection conn, int contractId, CostSummaryData baseline,
        long positionId, string field, string committedRevision, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (contractId <= 0 || positionId <= 0 || !SupportsCostRowDelta(field) ||
            string.IsNullOrWhiteSpace(committedRevision)) return null;
        var rows = baseline.Rows.ToArray();
        var index = Array.FindIndex(rows, row => row.Id == positionId);
        if (index < 0 || rows.Count(row => row.Id == positionId) != 1) return null;
        string Revision() => SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult();
        if (Revision() != committedRevision) return null;
        using var timing = StartupTrace.Measure("cost_targeted_row_readback");
        CostSummaryRow observed;
        using (var cmd = new SqlCommand(SummaryPositionsSelectSql + " AND p.id=@position" + SummaryPositionsOrderSql + ";" +
            SummaryComponentsSelectSql + " AND p.parent_id=@position" + SummaryComponentsOrderSql, conn))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            cmd.Parameters.Add("@position", SqlDbType.BigInt).Value = positionId;
            using var registration = SqlReadCancellation.Register(cmd, token);
            using var reader = (SqlDataReader)SqlReadCancellation.ExecuteReader(cmd, token);
            if (!reader.Read()) return null;
            observed = MapSummaryPosition(reader);
            if (reader.Read() || !reader.NextResult()) return null;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (!reader.IsDBNull(5) && Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture))
                    continue; // global outside-estimate extra does not belong to this root
                var cost = CostOf(reader, 6, 4);
                if (cost is null) continue;
                var qty = reader.IsDBNull(2) ? 0m :
                    Money.FromDouble(Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture));
                observed = FoldSummaryComponent(observed, qty, cost.Value);
            }
        }
        token.ThrowIfCancellationRequested();
        observed = FinalizeRows(new[] { observed }).Single();
        if (Revision() != committedRevision || !CanPatchCostRow(rows[index], observed, field)) return null;
        rows[index] = observed;
        return baseline with { Rows = rows };
    }
}
