using System.Data.Common;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Итоги документа: материалы, СМР, сумма без НДС, итого, НДС.</summary>
public sealed record ContractTotals(
    int Id,
    string Kind,
    string Form,
    decimal VatRate,
    decimal Materials,
    decimal Smr,
    decimal Net,
    decimal Total,
    decimal Vat,
    int Positions,
    decimal Transport = 0m,
    decimal Pnr = 0m,
    decimal DiscountPercent = 0m,
    decimal DiscountAmount = 0m);

/// <summary>Реквизиты договора для итогов, уже прочитанные батчем загрузки сметы.</summary>
public readonly record struct TotalsContract(int Id, string Kind, string Form,
    decimal VatRate, string SmrMode, decimal SmrAmount, decimal SmrPercent,
    decimal Transport, decimal Pnr, decimal DiscountPercent = 0m);

public readonly record struct TotalsSystem(int Id, decimal SmrAmount);

public readonly record struct TotalsPosition(int SystemId, decimal Qty, decimal Price,
    decimal WorkPrice, bool OutOfKp, bool IsChild);

/// <summary>
/// Расчёт итогов документа — порт Contract.material_sum / smr / net / total / vat.
/// Читает базу напрямую (одним проходом), считает в decimal.
/// </summary>
public static partial class Totals
{
    private sealed class ContractRow
    {
        public int Id;
        public string Kind = "";
        public string Form = "";
        public decimal VatRate;
        public string SmrMode = "";
        public decimal SmrAmount;
        public decimal SmrPercent;
        public decimal Transport;
        public decimal Pnr;
        public decimal DiscountPercent;
    }

    private readonly record struct PositionRow(decimal Qty, decimal Price, decimal WorkPrice, bool OutOfKp);

    public static IReadOnlyList<ContractTotals> ReadAll(DbConnection conn) =>
        conn is Microsoft.Data.SqlClient.SqlConnection
            ? ReadSqlBatch(conn, null, out _)
            : Calculate(TagRules.Load(conn), ReadContracts(conn), ReadSystems(conn),
                ReadPositions(conn), ReadPositionCounts(conn));

    internal static IReadOnlyList<ContractTotals> ReadSqlBatch(DbConnection conn,
        ContractReadScope? scope, out TagRules rules, int? rehearsalSchema = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var schema31 = rehearsalSchema is int version ? version >= 29 : SqlSchemaCompatibility.HasPreviewColumns(conn);
        string Where(string column) => scope is null ? "" : " WHERE " + scope.Predicate(column);
        string And(string column) => scope is null ? "" : " AND " + scope.Predicate(column);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT [key], rules FROM tags;" +
            "SELECT id, kind, form, vat_rate, smr_mode, smr_amount, smr_percent, transport_amount, pnr_amount" +
            (schema31 ? ", discount_percent " : " ") +
            "FROM contracts" + Where("id") + " ORDER BY id;" +
            "SELECT id, contract_id, smr_amount FROM systems" + Where("contract_id") + " ORDER BY id;" +
            "SELECT p.system_id,p.qty,p.price,p.work_price FROM positions p " +
            "JOIN systems sy ON sy.id=p.system_id WHERE p.parent_id IS NULL " +
            "AND (p.out_of_kp=0 OR p.out_of_kp IS NULL)" +
            And("sy.contract_id") + " ORDER BY p.system_id,p.[order],p.id;" +
            "SELECT sy.contract_id,COUNT(*) FROM systems sy JOIN positions p ON p.system_id=sy.id" +
            Where("sy.contract_id") + " GROUP BY sy.contract_id;";
        scope?.Bind(cmd);
        using var cancellation = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        rules = TagRules.Read(r);
        void Next()
        {
            token.ThrowIfCancellationRequested();
            var hasNext = r.NextResult();
            token.ThrowIfCancellationRequested();
            if (!hasNext) throw new InvalidOperationException("Неполный пакет итогов SQL Server.");
        }
        Next();
        var contracts = ReadContractRows(r, schema31);
        Next();
        var systems = ReadSystemRows(r);
        Next();
        var sums = ReadSqlSums(r, rules, contracts, systems, token);
        Next();
        var counts = ReadCountRows(r);
        token.ThrowIfCancellationRequested();
        return FinishSqlSums(rules, contracts, systems, sums, counts);
    }

    /// <summary>
    /// Calculate one contract without scanning every contract/system/position
    /// in the database. Identical decimal formula to ReadAll.
    /// </summary>
    public static ContractTotals? ReadOne(Microsoft.Data.SqlClient.SqlConnection conn, int contractId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (contractId <= 0) return null;
        return ReadSqlBatch(conn, new ContractReadScope(new[] { contractId }), out _, token: token).SingleOrDefault();
    }

    public static ContractTotals? ReadOne(DbConnection conn, int contractId)
    {
        if (contractId <= 0) return null;
        if (conn is Microsoft.Data.SqlClient.SqlConnection)
            return ReadSqlBatch(conn, new ContractReadScope(new[] { contractId }), out _).SingleOrDefault();
        var contracts = ReadContracts(conn, contractId);
        if (contracts.Count == 0) return null;
        return Calculate(TagRules.Load(conn), contracts,
            ReadSystems(conn, contractId), ReadPositions(conn, contractId),
            ReadPositionCounts(conn, contractId)).Single();
    }

    /// <summary>
    /// Итоги по уже прочитанным системам/позициям сметы: без повторного чтения
    /// systems/positions/счётчика. Числа совпадают с ReadOne.
    /// </summary>
    public static ContractTotals CalculateOne(TagRules tags, TotalsContract contract,
        IReadOnlyList<TotalsSystem> systems, IReadOnlyList<TotalsPosition> positions,
        int positionCount) =>
        CalculateOneFromRows(tags, contract, systems, positions,
            static p => p.SystemId, static p => !p.IsChild && !p.OutOfKp,
            static p => p, positionCount);

    // Keep references to existing estimate rows instead of first creating a
    // full TotalsPosition array and then copying every top row into another list.
    // Preserve the supplied system order and the original order within a system.
    internal static ContractTotals CalculateOneFromRows<TPosition>(TagRules tags,
        TotalsContract contract, IEnumerable<TotalsSystem> systems,
        IReadOnlyList<TPosition> positions, Func<TPosition, int> systemId,
        Func<TPosition, bool> included, Func<TPosition, TotalsPosition> project,
        int positionCount, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var contractRow = new ContractRow
        {
            Id = contract.Id,
            Kind = contract.Kind,
            Form = contract.Form,
            VatRate = contract.VatRate,
            SmrMode = contract.SmrMode,
            SmrAmount = contract.SmrAmount,
            SmrPercent = contract.SmrPercent,
            Transport = contract.Transport,
            Pnr = contract.Pnr,
            DiscountPercent = contract.DiscountPercent,
        };
        var positionsMap = new Dictionary<int, List<TPosition>>();
        foreach (var p in positions)
        {
            token.ThrowIfCancellationRequested();
            if (!included(p)) continue;
            var id = systemId(p);
            if (!positionsMap.TryGetValue(id, out var list))
                positionsMap[id] = list = new List<TPosition>();
            list.Add(p);
        }

        var roundRows = tags.Has(contract.Form, "round_rows");
        decimal materials = 0m, workSum = 0m, systemsSmr = 0m;
        foreach (var sys in systems)
        {
            token.ThrowIfCancellationRequested();
            systemsSmr += sys.SmrAmount;
            if (!positionsMap.TryGetValue(sys.Id, out var list)) continue;
            decimal systemSum = 0m;
            foreach (var row in list)
            {
                token.ThrowIfCancellationRequested();
                var p = project(row);
                AccumulatePosition(ref systemSum, ref workSum, p.Qty, p.Price, p.WorkPrice, roundRows);
            }
            materials += systemSum;
        }
        token.ThrowIfCancellationRequested();
        return FinishContract(contractRow, tags.Has(contract.Form, "vat_top"),
            materials, workSum, systemsSmr, positionCount);
    }

    private static void AccumulatePosition(ref decimal systemSum, ref decimal workSum,
        decimal qty, decimal price, decimal workPrice, bool roundRows)
    {
        var amount = qty * price;
        systemSum += roundRows ? Money.Round2(amount) : amount;
        workSum += qty * workPrice;
    }

    private static IReadOnlyList<ContractTotals> Calculate(TagRules tags,
        IReadOnlyList<ContractRow> contracts,
        IReadOnlyDictionary<int, List<SystemRow>> systems,
        IReadOnlyDictionary<int, List<PositionRow>> positions,
        IReadOnlyDictionary<int, int> counts)
    {
        var result = new List<ContractTotals>(contracts.Count);
        foreach (var c in contracts)
        {
            var roundRows = tags.Has(c.Form, "round_rows");
            decimal materials = 0m;
            decimal workSum = 0m;
            decimal systemsSmr = 0m;
            var positionCount = counts.GetValueOrDefault(c.Id);

            foreach (var sys in systems.GetValueOrDefault(c.Id) ?? new List<SystemRow>())
            {
                systemsSmr += sys.SmrAmount;
                var rows = positions.GetValueOrDefault(sys.Id) ?? new List<PositionRow>();
                decimal systemSum = 0m;
                foreach (var p in rows)
                {
                    if (p.OutOfKp) continue;
                    AccumulatePosition(ref systemSum, ref workSum, p.Qty, p.Price, p.WorkPrice, roundRows);
                }
                materials += systemSum;
            }

            result.Add(FinishContract(c, tags.Has(c.Form, "vat_top"),
                materials, workSum, systemsSmr, positionCount));
        }
        return result;
    }

    private static ContractTotals FinishContract(ContractRow c, bool vatTop,
        decimal materials, decimal workSum, decimal systemsSmr, int positionCount)
    {
        materials = Money.Round2(materials);
        var smr = c.SmrMode switch
        {
            "percent" => Money.Round2(materials * c.SmrPercent / 100m),
            "systems" => Money.Round2(systemsSmr),
            "pos" => Money.Round2(workSum),
            _ => c.SmrAmount,
        };

        var net = materials + smr + c.Transport + c.Pnr;
        var beforeDiscount = vatTop ? Money.Round2(net + Money.Round2(net * c.VatRate / 100m)) : net;
        var total = DiscountPolicy.Payable(beforeDiscount, c.DiscountPercent);
        var vat = vatTop
            ? Money.Round2(net * c.VatRate / 100m)
            : Money.VatIncluded(total, c.VatRate);   // как в Python: без округления, его делает показ

        return new ContractTotals(c.Id, c.Kind, c.Form, c.VatRate,
            materials, smr, net, total, vat, positionCount, c.Transport, c.Pnr,
            c.DiscountPercent, DiscountPolicy.Amount(beforeDiscount, c.DiscountPercent));
    }

    private readonly record struct SystemRow(int Id, decimal SmrAmount);

    /// <summary>Publicly testable read-query shapes; all scoped queries bind @id.</summary>
    public static (string Contracts, string Systems, string Positions, string Counts)
        ScopedQueries()
        => (
            "SELECT id, kind, form, vat_rate, smr_mode, smr_amount, smr_percent, transport_amount, pnr_amount FROM contracts WHERE id=@id",
            "SELECT id, contract_id, smr_amount FROM systems WHERE contract_id=@id ORDER BY id",
            "SELECT p.system_id, p.qty, p.price, p.work_price, p.out_of_kp FROM positions p JOIN systems sy ON sy.id=p.system_id WHERE p.parent_id IS NULL AND sy.contract_id=@id ORDER BY p.system_id, p.\"order\", p.id",
            "SELECT sy.contract_id, COUNT(*) FROM systems sy JOIN positions p ON p.system_id=sy.id WHERE sy.contract_id=@id GROUP BY sy.contract_id"
        );

    private static void BindContract(DbCommand cmd, int? contractId)
    {
        if (!contractId.HasValue) return;
        var param = cmd.CreateParameter();
        param.ParameterName = "@id";
        param.Value = contractId.Value;
        cmd.Parameters.Add(param);
    }

    private static List<ContractRow> ReadContracts(DbConnection conn, int? contractId = null)
    {
        using var cmd = conn.CreateCommand();
        var schema31 = SqlSchemaCompatibility.HasPreviewColumns(conn);
        cmd.CommandText =
            "SELECT id, kind, form, vat_rate, smr_mode, smr_amount, smr_percent, transport_amount, pnr_amount" +
            (schema31 ? ", discount_percent " : " ") +
            (contractId.HasValue ? "FROM contracts WHERE id=@id" : "FROM contracts ORDER BY id");
        BindContract(cmd, contractId);
        using var r = cmd.ExecuteReader();
        return ReadContractRows(r, schema31);
    }

    private static List<ContractRow> ReadContractRows(DbDataReader r, bool schema31)
    {
        var list = new List<ContractRow>();
        while (r.Read())
        {
            list.Add(new ContractRow
            {
                Id = Convert.ToInt32(r.GetValue(0)),
                Kind = Str(r, 1),
                Form = Str(r, 2),
                VatRate = Dec(r, 3),
                SmrMode = Str(r, 4),
                SmrAmount = Dec(r, 5),
                SmrPercent = Dec(r, 6),
                Transport = Dec(r, 7),
                Pnr = Dec(r, 8),
                DiscountPercent = schema31 ? DiscountPolicy.ReadPercent(r, 9) : 0m,
            });
        }
        return list;
    }

    private static Dictionary<int, List<SystemRow>> ReadSystems(DbConnection conn, int? contractId = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = contractId.HasValue
            ? ScopedQueries().Systems
            : "SELECT id, contract_id, smr_amount FROM systems ORDER BY id";
        BindContract(cmd, contractId);
        using var r = cmd.ExecuteReader();
        return ReadSystemRows(r);
    }

    private static Dictionary<int, List<SystemRow>> ReadSystemRows(DbDataReader r)
    {
        var map = new Dictionary<int, List<SystemRow>>();
        while (r.Read())
        {
            var loadedContractId = Convert.ToInt32(r.GetValue(1));
            if (!map.TryGetValue(loadedContractId, out var list))
                map[loadedContractId] = list = new List<SystemRow>();
            list.Add(new SystemRow(Convert.ToInt32(r.GetValue(0)), Dec(r, 2)));
        }
        return map;
    }

    private static Dictionary<int, List<PositionRow>> ReadPositions(DbConnection conn, int? contractId = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = contractId.HasValue
            ? ScopedQueries().Positions
            : "SELECT system_id, qty, price, work_price, out_of_kp FROM positions " +
              "WHERE parent_id IS NULL ORDER BY system_id, \"order\", id";
        BindContract(cmd, contractId);
        using var r = cmd.ExecuteReader();
        return ReadPositionRows(r);
    }

    private static Dictionary<int, List<PositionRow>> ReadPositionRows(DbDataReader r)
    {
        var map = new Dictionary<int, List<PositionRow>>();
        while (r.Read())
        {
            var systemId = Convert.ToInt32(r.GetValue(0));
            if (!map.TryGetValue(systemId, out var list))
                map[systemId] = list = new List<PositionRow>();
            list.Add(new PositionRow(Dec(r, 1), Dec(r, 2), Dec(r, 3), !r.IsDBNull(4) && Convert.ToBoolean(r.GetValue(4))));
        }
        return map;
    }

    /// <summary>Сколько позиций (включая подпозиции) в каждом документе — как all_positions() в Python.</summary>
    private static Dictionary<int, int> ReadPositionCounts(DbConnection conn, int? contractId = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = contractId.HasValue
            ? ScopedQueries().Counts
            : "SELECT sy.contract_id, COUNT(*) FROM systems sy " +
              "JOIN positions p ON p.system_id = sy.id GROUP BY sy.contract_id";
        BindContract(cmd, contractId);
        using var r = cmd.ExecuteReader();
        return ReadCountRows(r);
    }

    private static Dictionary<int, int> ReadCountRows(DbDataReader r)
    {
        var map = new Dictionary<int, int>();
        while (r.Read()) map[Convert.ToInt32(r.GetValue(0))] = Convert.ToInt32(r.GetValue(1));
        return map;
    }

    private static string Str(DbDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static decimal Dec(DbDataReader r, int i) =>
        r.IsDBNull(i) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(i)));
}
