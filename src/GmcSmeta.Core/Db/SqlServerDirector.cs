using System.Data;
using System.Globalization;
using System.Threading;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Строка «Сводки руководителя» по объекту (procurement.director_rows).</summary>
public sealed record DirectorRow(long ObjectId, string Object, bool KpOnly,
    decimal Total, decimal Closed, decimal ClosedPct, decimal Revenue, decimal Cost,
    decimal Covered, decimal Margin, decimal? MarginPct, decimal? Coverage,
    decimal OutCost, decimal Smr, decimal? SmrCost, decimal? SmrMargin, decimal Earned,
    int Late, decimal? Progress)
{
    public DirectorLegalScope? LegalScope { get; init; }
}

/// <summary>
/// Главная страница руководителя (procurement.director_rows): по объектам — договор
/// (или последнее КП), сдано по КС, маржа материалов с покрытием закупа, маржа
/// монтажа, докуплено вне сметы, заработано, выдано на объект, просроченные поставки.
/// </summary>
public static class SqlServerDirector
{
    private const decimal Eps = 0.000001m;
    private static readonly HashSet<string> Waiting = new()
    { "none", "ordered", "invoice", "paid", "shop_queued", "shop_work", "part_stock" };

    public sealed record ReadSnapshot(IReadOnlyList<DirectorRow> Rows, string Revision, DateTime Day);

    public static IReadOnlyList<DirectorRow> DirectorRows(string connectionString, string mode) =>
        ReadIfChanged(connectionString, mode, "", null)!.Rows;

    public static bool CanReuseSnapshot(string cachedRevision, string currentRevision,
        DateTime? cachedDay, DateTime day) =>
        !string.IsNullOrWhiteSpace(cachedRevision) && cachedRevision == currentRevision &&
        cachedDay?.Date == day.Date;

    // Null means the caller's same-mode snapshot is current. The day matters
    // because procurement due-state can change without a database revision.
    public static ReadSnapshot? ReadIfChanged(string connectionString, string mode,
        string cachedRevision, DateTime? cachedDay, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(connectionString);
        string Revision() => token.CanBeCanceled
            ? SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Revision(conn);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var revision = Revision();
            var day = DateTime.Today;
            token.ThrowIfCancellationRequested();
            if (attempt == 0 && CanReuseSnapshot(cachedRevision, revision, cachedDay, day) &&
                day == DateTime.Today) return null;
            var rows = ReadRows(conn, mode, day, token);
            token.ThrowIfCancellationRequested();
            var confirmed = Revision();
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(revision) && confirmed == revision && day == DateTime.Today)
                return new ReadSnapshot(rows, revision, day);
        }
        throw new InvalidOperationException("Данные сводки изменились при чтении. Обновите страницу.");
    }

    private static IReadOnlyList<DirectorRow> ReadRows(SqlConnection conn, string mode,
        DateTime day, CancellationToken token)
    {
        var objects = new List<(long Id, string Name)>();
        var kpOnly = mode == "kp";
        var legalEnabled = !kpOnly && SqlServerAgreements.Enabled(conn, token: token);
        var contracts = new Dictionary<long, List<(long Id, int Order, string Kind, int? StageOf, long? LegalId)>>();
        using (var read = new SqlCommand(
            "SELECT id,name FROM dbo.objects WHERE archived=0 ORDER BY name;" +
            "SELECT c.id,c.object_id,c.[order],c.kind,c.stage_of," +
            (legalEnabled ? "a.id " : "CAST(NULL AS bigint) ") +
            "FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id " +
            (legalEnabled ? "LEFT JOIN dbo.agreements a ON a.id=c.agreement_id AND a.object_id=c.object_id AND a.archived=0 " : "") +
            "WHERE o.archived=0 ORDER BY c.[order],c.id;", conn))
        using (var cancellation = SqlReadCancellation.Register(read, token))
        using (var r = SqlReadCancellation.ExecuteReader(read, token))
        {
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                objects.Add((Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), r.GetString(1)));
            }
            token.ThrowIfCancellationRequested();
            if (!r.NextResult()) throw new InvalidOperationException("Нет списка договоров сводки в ответе SQL.");
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var objectId = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                if (!contracts.TryGetValue(objectId, out var list))
                    contracts[objectId] = list = new List<(long, int, string, int?, long?)>();
                list.Add((
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture),
                    r.IsDBNull(3) ? "kp" : r.GetString(3),
                    r.IsDBNull(4) ? null : Convert.ToInt32(r.GetValue(4), CultureInfo.InvariantCulture),
                    r.IsDBNull(5) ? null : Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture)));
            }
        }

        token.ThrowIfCancellationRequested();
        var scopes = objects.Select(o =>
        {
            token.ThrowIfCancellationRequested();
            var candidates = contracts.GetValueOrDefault(o.Id) ?? new();
            var docs = kpOnly
                ? candidates.Where(c => c.Kind == "kp" && c.StageOf is null)
                    .OrderBy(c => c.Order).ThenBy(c => c.Id).TakeLast(1).ToList()
                : candidates.Where(c => c.Kind is "main" or "dop").ToList();
            return (o.Id, o.Name, Docs: docs);
        }).Where(o => o.Docs.Count > 0).ToList();
        if (scopes.Count == 0) return Array.Empty<DirectorRow>();
        var selected = scopes.SelectMany(o => o.Docs).Select(d => d.Id).Distinct().ToArray();
        var finance = FinanceReader.ReadSelected(conn, selected.Select(id => checked((int)id)), out var totals, token);
        token.ThrowIfCancellationRequested();
        var totalsById = totals.ToDictionary(t => t.Id);
        var metrics = ReadContractMetrics(conn, selected, !kpOnly, day, token);
        var rows = new List<DirectorRow>(scopes.Count);
        foreach (var (objectId, objectName, docs) in scopes)
        {
            token.ThrowIfCancellationRequested();
            decimal total = 0m, closed = 0m, revenue = 0m, cost = 0m, covered = 0m, outCost = 0m,
                smr = 0m, smrCost = 0m, matTotal = 0m, issuedValue = 0m;
            var missing = 0;
            var late = 0;
            foreach (var doc in docs)
            {
                token.ThrowIfCancellationRequested();
                if (finance.TryGetValue((int)doc.Id, out var f))
                {
                    revenue += f.MaterialRevenue;
                    cost += f.Cost;
                    covered += f.CoveredRevenue;
                    outCost += f.OutsideCost;
                    missing += f.MissingPrices;
                    closed += f.Signed;
                }
                if (totalsById.TryGetValue((int)doc.Id, out var totalRow))
                {
                    total += totalRow.Total;
                    smr += totalRow.Smr;
                }
                if (metrics.TryGetValue(doc.Id, out var m))
                {
                    // Preserve per-document rounding, not rounding on object sums.
                    smrCost += Math.Round(m.WorkCost + m.OtherCost, 2);
                    matTotal += m.MaterialTotal;
                    issuedValue += m.IssuedValue;
                    late += m.Late;
                }
            }
            var margin = covered - cost;
            var marginPct = covered != 0m ? margin / covered * 100m : (decimal?)null;
            var coverage = ContractFinance.CoveragePercent(revenue, covered);
            var smrMargin = smrCost != 0m ? smr - smrCost : (decimal?)null;
            var earned = margin + (smrMargin ?? 0m) - outCost;
            var closedPct = total != 0m ? closed / total * 100m : (decimal?)null;
            var progress = matTotal != 0m ? issuedValue / matTotal * 100m : (decimal?)null;
            rows.Add(new DirectorRow(objectId, objectName, kpOnly, total,
                closed, closedPct ?? 0m, revenue, cost, covered, margin, marginPct, coverage,
                outCost, smr, smrCost, smrMargin, earned, late, progress)
            {
                LegalScope = legalEnabled ? DirectorLegalScope.FromLinks(docs.Select(d => d.LegalId)) : null
            });
        }
        return rows;
    }

    private sealed class ContractMetrics
    {
        public decimal WorkCost, OtherCost, MaterialTotal, IssuedValue;
        public int Late;
    }

    // Bound parameter count; no temp tables, schema edits or writes.
    internal const int ContractBatchSize = 512;

    private static Dictionary<long, ContractMetrics> ReadContractMetrics(SqlConnection conn,
        IReadOnlyList<long> contractIds, bool countLate, DateTime today, CancellationToken token)
    {
        var result = contractIds.ToDictionary(id => id, _ => new ContractMetrics());
        foreach (var batch in contractIds.Chunk(ContractBatchSize))
        {
            token.ThrowIfCancellationRequested();
            using var read = new SqlCommand("", conn);
            var names = new List<string>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                var name = "@cid" + i.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                read.Parameters.Add(name, SqlDbType.BigInt).Value = batch[i];
            }
            read.CommandText = ContractMetricsSql(string.Join(",", names), countLate);
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            decimal Number(int index) => r.IsDBNull(index) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(index), CultureInfo.InvariantCulture));
            long Id(int index) => Convert.ToInt64(r.GetValue(index), CultureInfo.InvariantCulture);

            // Keep the two original SQL SUMs separate: each is converted to
            // decimal before the original per-contract rounding.
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                result[Id(0)].WorkCost = Number(1);
            }
            token.ThrowIfCancellationRequested();
            if (!r.NextResult()) throw new InvalidOperationException("Нет затрат СМР в ответе SQL.");
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                result[Id(0)].OtherCost = Number(1);
            }
            token.ThrowIfCancellationRequested();
            if (!r.NextResult()) throw new InvalidOperationException("Нет остатков в ответе SQL.");
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var m = result[Id(0)];
                var name = r.IsDBNull(1) ? "" : r.GetString(1);
                var qty = Number(2);
                var price = Number(3);
                var issued = Number(4);
                if (!EstimateStatistics.IsWorkName(name))
                {
                    m.MaterialTotal += Money.Round2(qty * price);
                    m.IssuedValue += Math.Min(issued, qty) * price;
                }
                if (countLate)
                {
                    var received = Number(5);
                    var dueDate = r.IsDBNull(6) ? (DateTime?)null :
                        Convert.ToDateTime(r.GetValue(6), CultureInfo.InvariantCulture);
                    var stage = r.IsDBNull(7) ? "" : r.GetString(7);
                    if (DueState(qty, received, issued, stage, dueDate, today) == "late")
                        m.Late++;
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    // The caller constructs the IN list exclusively from bound parameter names.
    // Scope movement aggregation to selected top-level positions and preserve
    // separate SUM(out) - SUM(back), SUM(in) - SUM(back_supplier).
    // KP never counts late deliveries; skip inbound movement kinds and the
    // procurement join only there. Issued value and both SMR sums stay intact.
    private static string ContractMetricsSql(string ids, bool countLate) =>
        "SELECT sy.contract_id, COALESCE(SUM(p.qty*ISNULL(p.work_cost,0)),0) " +
        "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
        $"WHERE sy.contract_id IN ({ids}) AND p.parent_id IS NULL AND p.out_of_kp=0 GROUP BY sy.contract_id; " +
        $"SELECT contract_id, COALESCE(SUM(amount),0) FROM dbo.smr_costs WHERE contract_id IN ({ids}) GROUP BY contract_id; " +
        "WITH selected_positions AS (SELECT p.id, sy.contract_id, p.name, p.qty, p.price " +
        "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
        $"WHERE sy.contract_id IN ({ids}) AND p.parent_id IS NULL AND ISNULL(p.out_of_kp,0)=0), " +
        "movement_totals AS (SELECT m.position_id, " +
        "SUM(CASE WHEN m.kind=N'out' THEN m.qty END) AS qty_out, " +
        "SUM(CASE WHEN m.kind=N'back' THEN m.qty END) AS qty_back " +
        (countLate ? ", SUM(CASE WHEN m.kind=N'in' THEN m.qty END) AS qty_in, " +
            "SUM(CASE WHEN m.kind=N'back_supplier' THEN m.qty END) AS qty_back_supplier " : "") +
        "FROM dbo.movements m JOIN selected_positions p ON p.id=m.position_id " +
        (countLate ? "WHERE m.kind IN (N'out',N'back',N'in',N'back_supplier') " :
            "WHERE m.kind IN (N'out',N'back') ") +
        "GROUP BY m.position_id) " +
        "SELECT p.contract_id,p.name,p.qty,p.price, " +
        "ISNULL(m.qty_out,0)-ISNULL(m.qty_back,0) " +
        (countLate ? ", ISNULL(m.qty_in,0)-ISNULL(m.qty_back_supplier,0),pr.due_date,ISNULL(pr.stage,N'') " : "") +
        "FROM selected_positions p LEFT JOIN movement_totals m ON m.position_id=p.id " +
        (countLate ? "LEFT JOIN dbo.procurement pr ON pr.position_id=p.id;" : ";");

    /// <summary>late/soon/ok/done/none — procurement.due_state (статусы из stage закупки).</summary>
    private static string? DueState(decimal qty, decimal received, decimal issued, string stage,
        DateTime? dueDate, DateTime today)
    {
        if (dueDate is null) return null;
        if (qty <= Eps && received <= Eps && issued <= Eps)
            return stage == "skip" ? "done" : "none";
        if (issued > Eps) return issued >= qty - Eps ? "done" : "none";
        if (received > Eps) return received >= qty - Eps ? "done" : "none";
        if (!Waiting.Contains(stage)) return "done";
        var days = (dueDate.Value.Date - today.Date).Days;
        return days < 0 ? "late" : days <= 3 ? "soon" : "ok";
    }
}
