using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerActs
{
    public sealed record SummarySystem(long SystemId, string Name, bool IsSub, long ParentId = 0);

    public sealed record SummaryRow(
        long PositionId, long SystemId, string NumText, string Name, string Unit,
        decimal QtyByKp, decimal Price, decimal Done, decimal DoneSum, decimal Left, decimal LeftSum,
        decimal Issued, IReadOnlyDictionary<long, decimal> PerAct);

    public sealed record SummaryData(
        IReadOnlyList<SqlServerActs.ActInfo> Acts,
        IReadOnlyList<(SummarySystem System, bool SectionHeader)> Systems,
        IReadOnlyList<SummaryRow> Positions);

    /// <summary>
    /// «Сводная по выполнению» (acts.summary_rows): разделы/подразделы, позиции
    /// с количеством по каждому акту, сдано всего, остаток (может быть
    /// отрицательным), остаток ₽, выдано на объект.
    /// </summary>
    public static SummaryData SummarySheet(SqlConnection conn, long contractId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var acts = ActsOf(conn, contractId, token: token);
        var form = "";
        using (var read = new SqlCommand("SELECT ISNULL(form,N'') FROM dbo.contracts WHERE id=@id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            token.ThrowIfCancellationRequested();
            var value = token.CanBeCanceled
                ? read.ExecuteScalarAsync(token).GetAwaiter().GetResult() : read.ExecuteScalar();
            form = value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        token.ThrowIfCancellationRequested();
        TagRules rules;
        using (var read = new SqlCommand("SELECT [key], rules FROM tags", conn))
        {
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var reader = SqlReadCancellation.ExecuteReader(read, token);
            rules = TagRules.Read(reader);
        }
        token.ThrowIfCancellationRequested();
        var numManual = rules.Has(form, "num_manual");

        var systemRows = new List<(long Id, string Name, string Heading, long Parent)>();
        using (var read = new SqlCommand(
            "SELECT s.id, s.name, s.heading, ISNULL(s.parent_id,0) FROM dbo.systems s " +
            "WHERE s.contract_id=@id ORDER BY s.[order], s.id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                systemRows.Add((
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture)));
            }
        }

        var positions = new List<(long Id, long SystemId, string Num, string Name, string Unit,
            decimal Qty, decimal Price)>();
        using (var read = new SqlCommand(
            "SELECT p.id, p.system_id, p.num, p.name, p.unit, p.qty, p.price " +
            "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@cid AND p.parent_id IS NULL AND ISNULL(p.out_of_kp,0)=0 " +
            "ORDER BY sy.[order], sy.id, p.[order], p.id", conn))
        {
            read.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            decimal D(int i) => r.IsDBNull(i) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
            string S(int i) => r.IsDBNull(i) ? "" :
                Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                positions.Add((
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                    S(2), S(3), S(4), D(5), D(6)));
            }
        }

        var qtyByAct = new Dictionary<(long Pid, long ActId), decimal>();
        var sumByAct = new Dictionary<(long Pid, long ActId), decimal>();
        using (var read = new SqlCommand(
            "SELECT al.position_id, al.act_id, SUM(al.qty), SUM(al.qty*al.price) FROM dbo.act_lines al " +
            "JOIN dbo.acts a ON a.id=al.act_id WHERE a.contract_id=@cid " +
            "GROUP BY al.position_id, al.act_id", conn))
        {
            read.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            decimal D(int i) => r.IsDBNull(i) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var key = (
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture));
                qtyByAct[key] = D(2);
                sumByAct[key] = D(3);
            }
        }

        var issued = ReadIssued(conn, contractId, token: token);
        var systemById = systemRows.ToDictionary(s => s.Id, s => s);
        // Preserve the SQL ordering inside each system, including for KP numbering.
        var positionsBySystem = positions.ToLookup(p => p.SystemId);

        // «№» — как kp_numbers: разделы считаются тоже
        var kp = new Dictionary<long, string>();
        var n = 1;
        foreach (var sy in systemRows)
        {
            token.ThrowIfCancellationRequested();
            if (sy.Name != "Без системы")
                n += 1;
            foreach (var p in positionsBySystem[sy.Id])
            {
                kp[p.Id] = numManual ? p.Num : n.ToString(CultureInfo.InvariantCulture);
                n += 1;
            }
        }

        var rows = new List<SummaryRow>();
        var systemsOut = new List<(SummarySystem, bool)>();
        var shown = new HashSet<long>();
        foreach (var sy in systemRows)
        {
            token.ThrowIfCancellationRequested();
            var mine = positionsBySystem[sy.Id];
            if (!mine.Any())
                continue;
            if (sy.Parent != 0 && !shown.Contains(sy.Parent))
            {
                shown.Add(sy.Parent);
                var parent = systemById.GetValueOrDefault(sy.Parent);
                systemsOut.Add((new SummarySystem(sy.Parent,
                    parent.Heading != "" ? parent.Heading : parent.Name, false, 0), true));
            }
            if (sy.Parent == 0)
                shown.Add(sy.Id);
            systemsOut.Add((new SummarySystem(sy.Id, sy.Name, sy.Parent != 0, sy.Parent), false));
            foreach (var p in mine)
            {
                token.ThrowIfCancellationRequested();
                var per = new Dictionary<long, decimal>();
                decimal done = 0m, doneSum = 0m;
                foreach (var act in acts)
                {
                    token.ThrowIfCancellationRequested();
                    var q = qtyByAct.GetValueOrDefault((p.Id, act.Id));
                    per[act.Id] = q;
                    done += q;
                    doneSum += sumByAct.GetValueOrDefault((p.Id, act.Id));
                }
                var left = p.Qty - done;
                rows.Add(new SummaryRow(p.Id, p.SystemId, kp.GetValueOrDefault(p.Id, ""),
                    p.Name, p.Unit, p.Qty, p.Price, done, decimal.Round(doneSum, 2), left,
                    decimal.Round(left * p.Price, 2), issued.GetValueOrDefault(p.Id), per));
            }
        }
        token.ThrowIfCancellationRequested();
        return new SummaryData(acts, systemsOut, rows);
    }

    public sealed record ExtraRow(
        long PositionId, string System, string Name, string Unit,
        decimal SignedQty, decimal DraftQty, decimal? Price, decimal Amount,
        decimal SignedSum, decimal DraftSum, IReadOnlyList<string> Acts,
        IReadOnlyList<(long ActId, string Act, bool Signed, decimal Qty, decimal Price, decimal Amount)> Lines);

    /// <summary>
    /// «Вне подписанной сметы» (acts.extra_rows): строки актов, у которых позиция
    /// вне КП, группировано по позиции; подписанные отдельно от черновиков.
    /// </summary>
    public static IReadOnlyList<ExtraRow> ExtraRows(SqlConnection conn, long contractId) =>
        ReadExtraRows(conn, contractId, ActsOf(conn, contractId));

    /// <summary>One act list for both projections. Caller must fence the entire
    /// read with the SQL revision, as for the individual read APIs.</summary>
    public static (SummaryData Summary, IReadOnlyList<ExtraRow> Extras) SummaryWithExtras(
        SqlConnection conn, long contractId, CancellationToken token = default)
    {
        var summary = SummarySheet(conn, contractId, token);
        return (summary, ReadExtraRows(conn, contractId, summary.Acts, token));
    }

    private static IReadOnlyList<ExtraRow> ReadExtraRows(SqlConnection conn, long contractId,
        IReadOnlyList<ActInfo> acts, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var actById = acts.ToDictionary(a => a.Id);
        var groups = new Dictionary<long, List<(long ActId, decimal Qty, decimal Price, decimal Amount)>>();
        using (var read = new SqlCommand(
            "SELECT al.position_id, al.act_id, al.qty, al.price " +
            "FROM dbo.act_lines al " +
            "JOIN dbo.acts a ON a.id=al.act_id " +
            "JOIN dbo.positions p ON p.id=al.position_id " +
            "WHERE a.contract_id=@cid AND ISNULL(p.out_of_kp,0)=1 " +
            "ORDER BY a.[date], a.id, al.id", conn))
        {
            read.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            decimal D(int i) => r.IsDBNull(i) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var pid = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                if (!groups.TryGetValue(pid, out var list))
                    groups[pid] = list = new List<(long, decimal, decimal, decimal)>();
                list.Add((Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture), D(2), D(3),
                    decimal.Round(D(2) * D(3), 2)));
            }
        }

        var meta = new Dictionary<long, (string System, string Name, string Unit)>();
        if (groups.Count > 0)
        {
            var ids = string.Join(",", groups.Keys);
            using var read = new SqlCommand(
                "SELECT p.id, sy.name, parent.name, p.name, p.unit FROM dbo.positions p " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "LEFT JOIN dbo.systems parent ON parent.id=sy.parent_id " +
                "WHERE p.id IN (" + ids + ")", conn);
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var systemName = r.IsDBNull(1) ? "" : r.GetString(1);
                if (!r.IsDBNull(2) && !string.IsNullOrWhiteSpace(r.GetString(2)))
                    systemName = r.GetString(2) + " › " + systemName;
                meta[Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture)] =
                    (systemName, r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4));
            }
        }

        var result = new List<ExtraRow>();
        foreach (var (pid, lines) in groups)
        {
            token.ThrowIfCancellationRequested();
            var (system, name, unit) = meta.GetValueOrDefault(pid, ("", "", ""));
            var signed = lines.Where(l => actById.GetValueOrDefault(l.ActId)?.Signed == true).ToList();
            var draft = lines.Where(l => actById.GetValueOrDefault(l.ActId)?.Signed != true).ToList();
            var prices = lines.Select(l => decimal.Round(l.Price, 6)).Distinct().ToList();
            result.Add(new ExtraRow(pid, system, name, unit,
                signed.Sum(l => l.Qty), draft.Sum(l => l.Qty),
                prices.Count == 1 ? prices[0] : null,
                lines.Sum(l => l.Amount),
                signed.Sum(l => l.Amount), draft.Sum(l => l.Amount),
                lines.Select(l => actById.GetValueOrDefault(l.ActId)?.Number ?? "").ToList(),
                lines.Select(l => (l.ActId, actById.GetValueOrDefault(l.ActId)?.Number ?? "",
                    actById.GetValueOrDefault(l.ActId)?.Signed == true, l.Qty, l.Price, l.Amount)).ToList()));
        }
        token.ThrowIfCancellationRequested();
        return result;
    }
}
