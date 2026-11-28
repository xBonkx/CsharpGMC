using System.Data.Common;
using System.Globalization;
using System.Numerics;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

public sealed record ContractProgress(decimal Total, decimal Signed, decimal Pending)
{
    public decimal Remaining => Total - Signed - Pending;
    public double SignedFraction => Total == 0 ? 0 : Math.Clamp((double)(Signed / Total), 0, 1);
    public double PendingFraction => Total == 0 ? 0 : Math.Min(1 - SignedFraction, Math.Clamp((double)(Pending / Total), 0, 1));
    public double RemainingFraction => 1 - SignedFraction - PendingFraction;
    public string SignedPercent => Percent(Signed);
    public string PendingPercent => Percent(Pending);
    public string Caption => Total == 0 ? "—" : SignedPercent + (Pending != 0 ? "  ·  " + Percent(Signed + Pending) + " с поданными" : "");
    public string Legend => Total == 0 ? "" : $"● подписано {Money.Format(Signed)}   ● на сдаче {Money.Format(Pending)}   ○ остаток {Money.Format(Remaining)}";
    private string Percent(decimal amount) => (Total == 0 ? 0 : decimal.Round(amount / Total * 100, 0, MidpointRounding.ToEven)).ToString("0", CultureInfo.InvariantCulture) + "%";
}

public sealed record ContractFinance(decimal Signed = 0, decimal Pending = 0, decimal Cost = 0,
    decimal CoveredRevenue = 0, decimal OutsideCost = 0, decimal SmrCost = 0, int MissingPrices = 0)
{
    // Full material revenue, including positions without a known purchase price.
    public decimal MaterialRevenue { get; init; }
    public static decimal? CoveragePercent(decimal revenue, decimal coveredRevenue) =>
        revenue != 0m ? coveredRevenue / revenue * 100m : null;

    public decimal? Earned(decimal smr) => CoveredRevenue != 0 || SmrCost != 0
        ? CoveredRevenue - Cost + (SmrCost != 0 ? smr - SmrCost : 0) - OutsideCost : null;
    public string Notes(decimal smr) => string.Join("\n", new[]
    {
        MissingPrices > 0 ? $"закуп не указан у {MissingPrices} поз." : "",
        smr != 0 && SmrCost == 0 ? "затраты на монтаж не внесены" : ""
    }.Where(s => s.Length > 0));
    public static ContractFinance Sum(IEnumerable<ContractFinance> rows)
    {
        var list = rows.ToList();
        return new(list.Sum(r => r.Signed), list.Sum(r => r.Pending), list.Sum(r => r.Cost),
            list.Sum(r => r.CoveredRevenue), list.Sum(r => r.OutsideCost), list.Sum(r => r.SmrCost), list.Sum(r => r.MissingPrices))
        { MaterialRevenue = list.Sum(r => r.MaterialRevenue) };
    }
}

/// <summary>Read-only port of Contract.cost_summary/smr_cost and Act.total for object cards.</summary>
public static partial class FinanceReader
{
    private sealed class Costs
    {
        public FloatSum Revenue, Cost, Covered, Outside, Work;
        public int Missing;
        public decimal Signed, Pending;
    }

    // Python >=3.12 sum uses compensated summation. Keep binary products until its rounding point.
    private struct FloatSum
    {
        private double _hi, _lo;
        public void Add(double value)
        {
            var next = _hi + value;
            _lo += Math.Abs(_hi) >= Math.Abs(value) ? (_hi - next) + value : (value - next) + _hi;
            _hi = next;
        }
        public readonly double Value => _hi + _lo;
    }

    /// <summary>Exact round(double, digits) ties-to-even, including Python's round(2.675,2)=2.67.</summary>
    public static double PythonRound(double value, int digits = 2)
    {
        if (!double.IsFinite(value) || digits is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(value));
        if (Math.Abs(value) >= 1e16) return value; // doubles at this scale have no fractional part
        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var fraction = bits & 0x000f_ffff_ffff_ffff;
        var numerator = new BigInteger(exponent == 0 ? fraction : fraction | (1L << 52));
        var power = exponent == 0 ? -1074 : exponent - 1023 - 52;
        var scale = BigInteger.Pow(10, digits);
        numerator *= scale;
        var denominator = BigInteger.One;
        if (power >= 0) numerator <<= power;
        else denominator <<= -power;
        var rounded = BigInteger.DivRem(numerator, denominator, out var remainder);
        var compare = (remainder * 2).CompareTo(denominator);
        if (compare > 0 || compare == 0 && !rounded.IsEven) rounded++;
        return (bits < 0 ? -1 : 1) * (double)rounded / (double)scale;
    }

    private static double HalfUp(double value) => (double)decimal.Round(
        decimal.Parse(PythonRound(value, 9).ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture),
        2, MidpointRounding.AwayFromZero);
    internal static double RoundHalfUp(double value) => HalfUp(value);
    private static decimal Cents(double value) => (decimal)PythonRound(value);
    /// <summary>Число из любой ADO.NET-базы: BIT SQL Server (signed, out_of_kp) читается как 0/1.</summary>
    private static double Number(DbDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetValue(i) switch
    {
        bool b => b ? 1d : 0d,
        var v => Convert.ToDouble(v, CultureInfo.InvariantCulture),
    };

    private static string Text(DbDataReader r, int i) => r.IsDBNull(i) ? "" :
        Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";

    private static int Id(DbDataReader r, int i) => SqlInteger.ReadInt32(r, i);

    public static IReadOnlyDictionary<int, ContractFinance> ReadAll(DbConnection conn) =>
        ReadAll(conn, out _);

    // The director needs these exact totals too; do not re-read each contract.
    public static IReadOnlyDictionary<int, ContractFinance> ReadAll(DbConnection conn,
        out IReadOnlyList<ContractTotals> contractTotals)
    {
        TagRules rules;
        if (conn is Microsoft.Data.SqlClient.SqlConnection)
            contractTotals = Totals.ReadSqlBatch(conn, null, out rules);
        else
        {
            contractTotals = Totals.ReadAll(conn);
            rules = TagRules.Load(conn);
        }
        return ReadFinance(conn, contractTotals, rules, null);
    }

    /// <summary>Read only selected SQL Server documents, in bounded batches.</summary>
    public static IReadOnlyDictionary<int, ContractFinance> ReadSelected(
        Microsoft.Data.SqlClient.SqlConnection conn, IEnumerable<int> contractIds,
        out IReadOnlyList<ContractTotals> contractTotals, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var ids = contractIds.Distinct().OrderBy(id => id).ToArray();
        if (ids.Any(id => id <= 0)) throw new ArgumentOutOfRangeException(nameof(contractIds));
        var result = new Dictionary<int, ContractFinance>();
        var totals = new List<ContractTotals>();
        foreach (var batch in ids.Chunk(ContractReadScope.BatchSize))
        {
            token.ThrowIfCancellationRequested();
            var scope = new ContractReadScope(batch);
            var batchTotals = Totals.ReadSqlBatch(conn, scope, out var rules, token: token);
            totals.AddRange(batchTotals);
            foreach (var pair in ReadFinance(conn, batchTotals, rules, scope, token))
                result.Add(pair.Key, pair.Value);
        }
        token.ThrowIfCancellationRequested();
        contractTotals = totals;
        return result;
    }

    // Object cards already own the current estimate totals and tag rules.
    // ReadFinance/ReadActsCore consume only Id, Form and VatRate from totals;
    // do not fetch positions/systems/counts a second time for those fields.
    // Caller owns the revision fence over both projections.
    internal static IReadOnlyDictionary<int, ContractFinance> ReadForCards(
        Microsoft.Data.SqlClient.SqlConnection conn,
        IReadOnlyCollection<ContractTotals> cardTotals, TagRules rules)
    {
        var ordered = cardTotals.OrderBy(t => t.Id).ToArray();
        if (ordered.Any(t => t.Id <= 0) || ordered.Select(t => t.Id).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Некорректный набор документов карточек.", nameof(cardTotals));
        var result = new Dictionary<int, ContractFinance>(ordered.Length);
        foreach (var batch in ordered.Chunk(ContractReadScope.BatchSize))
        {
            var scope = new ContractReadScope(batch.Select(t => t.Id));
            foreach (var pair in ReadFinance(conn, batch, rules, scope))
                result.Add(pair.Key, pair.Value);
        }
        return result;
    }

    private static IReadOnlyDictionary<int, ContractFinance> ReadFinance(DbConnection conn,
        IReadOnlyList<ContractTotals> contractTotals, TagRules rules, ContractReadScope? scope,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var contracts = contractTotals.ToDictionary(c => c.Id);
        var costs = contracts.Keys.ToDictionary(id => id, _ => new Costs());
        var extraWork = costs.Keys.ToDictionary(id => id, _ => new FloatSum());
        IReadOnlyList<ActSummary> acts;

        if (conn is Microsoft.Data.SqlClient.SqlConnection)
        {
            // One round trip per bounded scope. Keep independent result sets
            // and the original order; MARS and server-side money rounding are not needed.
            var schema31 = SqlSchemaCompatibility.HasPreviewColumns(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = FinancePositionsSql(scope) + ";" + ExtraWorkSql(scope) + ";" +
                ActsSql(scope, schema31) + ";" + ActLinesSql(scope);
            scope?.Bind(cmd);
            using var cancellation = SqlReadCancellation.Register(cmd, token);
            using var r = SqlReadCancellation.ExecuteReader(cmd, token);
            ReadFinancePositions(r, contracts, costs, rules, token);
            NextFinanceResult(r, token);
            ReadExtraWork(r, extraWork, token);
            NextFinanceResult(r, token);
            var headers = ReadActHeaders(r, contracts, rules, schema31, token);
            NextFinanceResult(r, token);
            ReadActAmounts(r, headers, token);
            acts = FinishActs(headers, token);
        }
        else
        {
            // Preserve the existing offline fixture route and use the same parsers.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = FinancePositionsSql(scope);
                scope?.Bind(cmd);
                using var r = cmd.ExecuteReader();
                ReadFinancePositions(r, contracts, costs, rules);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = ExtraWorkSql(scope);
                scope?.Bind(cmd);
                using var r = cmd.ExecuteReader();
                ReadExtraWork(r, extraWork);
            }
            acts = ReadActsCore(conn, contractTotals, rules, scope);
        }

        foreach (var act in acts)
        {
            token.ThrowIfCancellationRequested();
            if (act.Signed) costs[act.ContractId].Signed += act.Total;
            else costs[act.ContractId].Pending += act.Total;
        }
        token.ThrowIfCancellationRequested();
        return costs.ToDictionary(x => x.Key, x => new ContractFinance(x.Value.Signed, x.Value.Pending,
            Cents(x.Value.Cost.Value), Cents(x.Value.Covered.Value), Cents(x.Value.Outside.Value),
            Cents(x.Value.Work.Value + extraWork[x.Key].Value), x.Value.Missing)
        { MaterialRevenue = Cents(x.Value.Revenue.Value) });
    }

    private static void NextFinanceResult(DbDataReader reader, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var hasNext = reader.NextResult();
        token.ThrowIfCancellationRequested();
        if (!hasNext)
            throw new InvalidOperationException("Неполный финансовый пакет SQL Server.");
    }

    private static string FinancePositionsSql(ContractReadScope? scope) =>
        "SELECT sy.contract_id, p.parent_id, p.name, parent.name, p.qty, p.price, " +
        "p.purchase_price, pr.cost_price, p.out_of_kp, p.work_cost " +
        "FROM positions p JOIN systems sy ON sy.id=p.system_id " +
        "LEFT JOIN positions parent ON parent.id=p.parent_id " +
        "LEFT JOIN procurement pr ON pr.position_id=p.id" +
        (scope is null ? "" : " WHERE " + scope.Predicate("sy.contract_id")) +
        " ORDER BY sy.\"order\",sy.id,p.\"order\",p.id";

    private static string ExtraWorkSql(ContractReadScope? scope) =>
        "SELECT contract_id,amount FROM smr_costs" +
        (scope is null ? "" : " WHERE " + scope.Predicate("contract_id")) + " ORDER BY id";

    private static string ActLinesSql(ContractReadScope? scope) =>
        scope is null
        ? "SELECT act_id,qty,price FROM act_lines ORDER BY act_id,id"
        : "SELECT al.act_id,al.qty,al.price FROM act_lines al JOIN acts a ON a.id=al.act_id WHERE " +
            scope.Predicate("a.contract_id") + " ORDER BY al.act_id,al.id";

    private static string ActsSql(ContractReadScope? scope, bool schema31) =>
        "SELECT id,contract_id,smr_amount,transport_amount,pnr_amount,rows_exact,gross_adjustment_cents,signed,number,date,period_from,period_to,note" +
        (schema31 ? ",discount_percent,manual_total_cents" : "") + " FROM acts" +
        (scope is null ? "" : " WHERE " + scope.Predicate("contract_id")) + " ORDER BY id";

    private static void ReadFinancePositions(DbDataReader r,
        Dictionary<int, ContractTotals> contracts, Dictionary<int, Costs> costs, TagRules rules,
        CancellationToken token = default)
    {
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            var id = Id(r, 0);
            var c = costs[id];
            var qty = Number(r, 4);
            var outside = Number(r, 8) != 0;
            var top = r.IsDBNull(1);
            if (top && !outside) c.Work.Add(qty * Number(r, 9));
            double? price = !r.IsDBNull(7) ? r.GetDouble(7) : !r.IsDBNull(6) ? r.GetDouble(6) : null;
            if (outside)
            {
                if (price is not null) c.Outside.Add(qty * price.Value);
                continue;
            }
            if (EstimateStatistics.IsWorkName(Text(r, 2)) ||
                !r.IsDBNull(3) && EstimateStatistics.IsWorkName(Text(r, 3))) continue;
            var amount = top ? qty * Number(r, 5) : 0;
            var revenue = rules.Has(contracts[id].Form, "round_rows") ? HalfUp(amount) : PythonRound(amount);
            c.Revenue.Add(revenue);
            if (price is null)
            {
                if (revenue != 0) c.Missing++;
                continue;
            }
            c.Cost.Add(qty * price.Value);
            c.Covered.Add(revenue);
        }
    }

    private static void ReadExtraWork(DbDataReader r, Dictionary<int, FloatSum> extraWork,
        CancellationToken token = default)
    {
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            var id = Id(r, 0);
            var sum = extraWork[id]; sum.Add(Number(r, 1)); extraWork[id] = sum;
        }
    }

}
