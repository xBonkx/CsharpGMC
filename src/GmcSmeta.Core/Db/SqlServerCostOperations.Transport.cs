using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerCostOperations
{
    public sealed record SupplierCost(string Name, decimal Total, int Rows, int Missing, bool None);

    /// <summary>
    /// Python procurement.supplier_cost_sums, one SQL read of a contract (no N+1).
    /// Include components and out-of-KP purchased material; exclude installation rows.
    /// </summary>
    public static IReadOnlyList<SupplierCost> SupplierCosts(string connectionString, int contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT p.name, par.name, p.qty, p.purchase_price, pr.cost_price, sup.name, " +
            "p.price, p.out_of_kp " +
            "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "LEFT JOIN dbo.positions par ON par.id=p.parent_id " +
            "LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " +
            "LEFT JOIN dbo.suppliers sup ON sup.id=pr.supplier_id " +
            "WHERE sy.contract_id=@contract", conn);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        var states = new Dictionary<string, (decimal Total, int Rows, int Missing)>(
            StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.IsDBNull(0) ? "" : r.GetString(0);
            var parent = r.IsDBNull(1) ? null : r.GetString(1);
            if (EstimateStatistics.IsWorkName(name) ||
                (parent is not null && EstimateStatistics.IsWorkName(parent)))
                continue;
            decimal Dec(int i) => r.IsDBNull(i) ? 0m :
                Convert.ToDecimal(r.GetValue(i), CultureInfo.InvariantCulture);
            var supplier = r.IsDBNull(5) ? "" : r.GetString(5).Trim();
            var current = states.GetValueOrDefault(supplier);
            var hasFact = !r.IsDBNull(4);
            var hasPlan = !r.IsDBNull(3);
            var qty = Dec(2);
            if (hasFact || hasPlan)
                current = (current.Total + qty * (hasFact ? Dec(4) : Dec(3)),
                    current.Rows + 1, current.Missing);
            else if (parent is null && (r.IsDBNull(7) || !Convert.ToBoolean(r.GetValue(7), CultureInfo.InvariantCulture)) && qty * Dec(6) != 0m)
                current = (current.Total, current.Rows, current.Missing + 1);
            states[supplier] = current;
        }
        return states
            .Where(x => x.Value.Rows != 0 || x.Value.Missing != 0)
            .Select(x => new SupplierCost(
                x.Key.Length == 0 ? "Поставщик не указан" : x.Key,
                decimal.Round(x.Value.Total, 2, MidpointRounding.ToEven),
                x.Value.Rows, x.Value.Missing, x.Key.Length == 0))
            .OrderBy(x => x.None ? 1 : 0).ThenByDescending(x => x.Total)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
