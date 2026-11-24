using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Python export_request.request_rows: supplier-scoped, status-aware request projection.
/// This is NOT the visible/filtered grid: it always uses the contract's full SQL snapshot.
/// </summary>
public static class ProcurementRequestPlanner
{
    public sealed class RequestRow
    {
        public string Name { get; init; } = "";
        public string Unit { get; init; } = "";
        public decimal Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public string Note { get; set; } = "";
        public List<long> PositionIds { get; } = new();
        public List<string> Systems { get; } = new();
        public string SystemsText => string.Join(", ", Systems);
        public decimal? Amount => UnitPrice is null ? null :
            decimal.Round(Quantity * UnitPrice.Value, 2, MidpointRounding.AwayFromZero);
    }

    public static IReadOnlyList<RequestRow> CreateRows(
        IEnumerable<SqlServerProcurement.BuyRow> positions,
        IReadOnlySet<long> childPositionIds, string supplier,
        bool onlyNew = true, bool merge = true)
    {
        var result = new List<RequestRow>();
        var seen = new Dictionary<string, RequestRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in positions)
        {
            if (childPositionIds.Contains(p.PositionId) || p.OutOfKp || p.Work ||
                !string.Equals(p.Supplier ?? "", supplier, StringComparison.Ordinal) ||
                (onlyNew && p.Status != "none"))
                continue;
            var key = p.Name.Trim().ToLowerInvariant() + "\0" + p.Unit.Trim().ToLowerInvariant();
            if (merge && seen.TryGetValue(key, out var existing))
            {
                if (existing.UnitPrice is not null && p.CostPrice is not null &&
                    Math.Abs(existing.UnitPrice.Value - p.CostPrice.Value) > 0.01m)
                {
                    var total = existing.Quantity + p.Qty;
                    existing.UnitPrice = total == 0m ? existing.UnitPrice :
                        decimal.Round((existing.UnitPrice.Value * existing.Quantity +
                                       p.CostPrice.Value * p.Qty) / total, 2,
                                      MidpointRounding.ToEven);
                }
                existing.Quantity += p.Qty;
                existing.PositionIds.Add(p.PositionId);
                if (!existing.Systems.Contains(p.SystemName))
                    existing.Systems.Add(p.SystemName);
                continue;
            }
            var line = new RequestRow
            {
                Name = p.Name,
                Unit = p.Unit,
                Quantity = p.Qty,
                UnitPrice = p.CostPrice,
                Note = p.Note
            };
            line.PositionIds.Add(p.PositionId);
            line.Systems.Add(p.SystemName);
            result.Add(line);
            if (merge) seen[key] = line;
        }
        return result;
    }

    // Python print_shop_order: selected shop rows take precedence, including already
    // issued rows. With no selected shop rows use all queued/in-production shop rows.
    public static IReadOnlyList<SqlServerProcurement.BuyRow> SelectShopRows(
        IEnumerable<SqlServerProcurement.BuyRow> positions, IReadOnlySet<long> selectedIds)
    {
        var shop = positions.Where(p => !p.Work && p.Source == "shop")
            .DistinctBy(p => p.PositionId).ToArray();
        var selected = shop.Where(p => selectedIds.Contains(p.PositionId)).ToArray();
        return selected.Length > 0 ? selected :
            shop.Where(p => p.Status is "shop_queued" or "shop_work").ToArray();
    }

    // A print-only projection: editing this type cannot write back to procurement or the estimate.
    public sealed record ShopPrintLine(long PositionId, string System, string Name,
        string Unit, decimal Quantity, string Note);

    public static IReadOnlyList<ShopPrintLine> ShopPrintRows(
        IReadOnlyList<SqlServerProcurement.BuyRow> positions)
    {
        if (positions.Any(p => p.Work || p.Source != "shop"))
            throw new ArgumentException("В заявке присутствуют позиции не из цеха.");
        return positions.Select(p => new ShopPrintLine(p.PositionId, p.SystemName, p.Name, p.Unit,
            p.Qty, string.Join("; ", new[] { p.Replacement, p.Note }.Where(x => x.Length > 0))))
            .ToArray();
    }

    public static string? ShopPrintError(IReadOnlyList<ShopPrintLine> rows)
    {
        if (rows.Count == 0) return "Для цеха нет позиций.";
        if (rows.Any(p => p.PositionId <= 0 || string.IsNullOrWhiteSpace(p.Name) ||
            string.IsNullOrWhiteSpace(p.Unit) || p.Quantity < 0m || p.Quantity > 1_000_000_000m ||
            decimal.Round(p.Quantity, 6) != p.Quantity))
            return "Проверьте наименование, единицу и количество (до 6 знаков после запятой).";
        if (rows.Select(p => p.PositionId).Distinct().Count() != rows.Count)
            return "В заявке есть повторные позиции.";
        return null;
    }

    public static IReadOnlySet<long> ChildPositionIds(string connectionString, int contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT p.id FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@contract AND p.parent_id IS NOT NULL", conn);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        using var reader = cmd.ExecuteReader();
        var ids = new HashSet<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    public static IReadOnlyList<(string Supplier, int Count)> SupplierCounts(
        IEnumerable<SqlServerProcurement.BuyRow> positions, IReadOnlySet<long> childPositionIds,
        bool onlyNew = true) =>
        positions.Where(p =>
            !childPositionIds.Contains(p.PositionId) && !p.OutOfKp && !p.Work &&
            !string.IsNullOrWhiteSpace(p.Supplier) && (!onlyNew || p.Status == "none"))
        .GroupBy(p => p.Supplier!, StringComparer.Ordinal)
        .Select(g => (Supplier: g.Key, Count: g.Count()))
        .OrderByDescending(x => x.Count)
        .ThenBy(x => x.Supplier, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>Find next number across all contracts, matching Python's "Заявка №N".</summary>
    public static string NextNumber(string connectionString)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT order_ref FROM dbo.procurement WHERE order_ref LIKE N'%аявка №%'", conn);
        using var reader = cmd.ExecuteReader();
        long max = 0;
        var regex = new Regex(@"аявка №\s*(\d+)", RegexOptions.CultureInvariant);
        while (reader.Read())
        {
            var text = reader.IsDBNull(0) ? "" : reader.GetString(0);
            foreach (Match match in regex.Matches(text))
                if (long.TryParse(match.Groups[1].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var number) && number > max)
                    max = number;
        }
        return (max + 1L).ToString(CultureInfo.InvariantCulture);
    }

    public sealed record DocumentInfo(string ObjectName, string Address, string Title, string CompanyHeader);

    /// <summary>Contract-scoped requisites for the Excel header, read-only.</summary>
    public static DocumentInfo ReadDocumentInfo(string connectionString, int contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT o.name,ISNULL(o.address,N''),ISNULL(c.name,N'')," +
            "ISNULL(co.name,N''),ISNULL(co.legal_full,N'') " +
            "FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id " +
            "LEFT JOIN dbo.companies co ON co.id=c.company_id WHERE c.id=@contract", conn);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Договор не найден.");
        var company = reader.GetString(4);
        if (company.Length == 0) company = reader.GetString(3);
        return new DocumentInfo(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), company);
    }

}
