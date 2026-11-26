using System.Data.Common;
using System.Globalization;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

public static partial class FinanceReader
{
    // One header and one compensated accumulator per act. No list of
    // (qty, price) tuples is retained. The source order remains act_id,id.
    private sealed class ActReadState
    {
        public int Id, ContractId, GrossAdjustment;
        public string Number = "", Note = "";
        public DateOnly? Date, PeriodFrom, PeriodTo;
        public bool Signed, Exact, RoundRows, VatTop, Schema31;
        public decimal VatRate, DiscountPercent;
        public long ManualTotalCents = -1;
        public double Smr, Transport, Pnr, GrossAdjustmentRaw;
        public FloatSum Sum;
    }

    public static IReadOnlyList<ActSummary> ReadActs(DbConnection conn, IReadOnlyList<ContractTotals>? knownTotals = null)
    {
        return ReadActsCore(conn, knownTotals ?? Totals.ReadAll(conn), TagRules.Load(conn), null);
    }

    private static IReadOnlyList<ActSummary> ReadActsCore(DbConnection conn,
        IReadOnlyList<ContractTotals> totals, TagRules rules, ContractReadScope? scope)
    {
        var contracts = totals.ToDictionary(c => c.Id);
        var schema31 = SqlSchemaCompatibility.HasPreviewColumns(conn);
        Dictionary<int, ActReadState> headers;
        if (conn is Microsoft.Data.SqlClient.SqlConnection)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = ActsSql(scope, schema31) + ";" + ActLinesSql(scope);
            scope?.Bind(cmd);
            using var r = cmd.ExecuteReader();
            headers = ReadActHeaders(r, contracts, rules, schema31);
            NextFinanceResult(r);
            ReadActAmounts(r, headers);
        }
        else
        {
            // Existing offline fixture route; same SELECT projections, no new backend.
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = ActsSql(scope, schema31);
                scope?.Bind(cmd);
                using var r = cmd.ExecuteReader();
                headers = ReadActHeaders(r, contracts, rules, schema31);
            }
            using var lines = conn.CreateCommand();
            lines.CommandText = ActLinesSql(scope);
            scope?.Bind(lines);
            using var reader = lines.ExecuteReader();
            ReadActAmounts(reader, headers);
        }
        return FinishActs(headers);
    }

    private static Dictionary<int, ActReadState> ReadActHeaders(DbDataReader r,
        Dictionary<int, ContractTotals> contracts, TagRules rules, bool schema31)
    {
        var headers = new Dictionary<int, ActReadState>();
        while (r.Read())
        {
            var contractId = Id(r, 1);
            var contract = contracts[contractId];
            DateOnly? Day(int index) => r.IsDBNull(index) ? null :
                r.GetValue(index) is DateTime date ? DateOnly.FromDateTime(date) :
                r.GetValue(index) is DateOnly day ? day :
                DateOnly.Parse(Text(r, index), CultureInfo.InvariantCulture);
            var state = new ActReadState
            {
                Id = Id(r, 0), ContractId = contractId, Number = Text(r, 8),
                Date = Day(9), PeriodFrom = Day(10), PeriodTo = Day(11),
                Signed = Number(r, 7) != 0, Exact = Number(r, 5) != 0, Schema31 = schema31,
                RoundRows = rules.Has(contract.Form, "round_rows"),
                VatTop = rules.Has(contract.Form, "vat_top"), VatRate = contract.VatRate,
                Smr = Number(r, 2), Transport = Number(r, 3), Pnr = Number(r, 4),
                GrossAdjustment = (int)Number(r, 6), GrossAdjustmentRaw = Number(r, 6),
                Note = Text(r, 12),
                DiscountPercent = schema31 ? DiscountPolicy.ReadPercent(r, 13) : 0m,
                ManualTotalCents = schema31 && !r.IsDBNull(14) ? Convert.ToInt64(r.GetValue(14)) : -1,
            };
            headers.Add(state.Id, state);
        }
        return headers;
    }

    private static void ReadActAmounts(DbDataReader r, Dictionary<int, ActReadState> headers)
    {
        while (r.Read())
        {
            if (!headers.TryGetValue(Id(r, 0), out var act)) continue;
            var amount = Number(r, 1) * Number(r, 2);
            act.Sum.Add(act.Exact ? amount : act.RoundRows ? HalfUp(amount) : PythonRound(amount));
        }
    }

    private static IReadOnlyList<ActSummary> FinishActs(Dictionary<int, ActReadState> headers)
    {
        var result = new List<ActSummary>(headers.Count);
        foreach (var act in headers.Values.OrderBy(a => a.Id))
        {
            var material = act.Exact ? HalfUp(act.Sum.Value) : PythonRound(act.Sum.Value);
            var net = PythonRound(material + act.Smr + act.Transport + act.Pnr);
            var total = act.VatTop
                ? PythonRound(net + PythonRound(net * (double)act.VatRate / 100) + act.GrossAdjustmentRaw / 100) : net;
            // Do not convert through decimal on schema 28: preserve the old double path.
            if (act.Schema31)
                total = (double)DiscountPolicy.Payable((decimal)total, act.DiscountPercent, act.ManualTotalCents);
            var vat = act.VatTop
                ? PythonRound(net * (double)act.VatRate / 100)
                : PythonRound(total * (double)act.VatRate / (100 + (double)act.VatRate));
            result.Add(new ActSummary(act.Id, act.ContractId, act.Number,
                act.Date, act.PeriodFrom, act.PeriodTo, act.Signed, (decimal)material, (decimal)net,
                (decimal)total, (decimal)vat, (decimal)act.Smr, (decimal)act.Transport,
                (decimal)act.Pnr, act.GrossAdjustment, act.Note));
        }
        return result;
    }
}
