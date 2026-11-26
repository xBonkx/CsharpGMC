using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Read-only sheets for the KS tab (ks_view.py): the act grid by systems with
/// «Сдано ранее / В этом акте / Остаток / Выдано на объект», the fulfilment
/// summary per act and the «Вне подписанной сметы» section. No writes here —
/// every edit goes through the existing transactional SqlServerActs methods.
/// </summary>
public static partial class SqlServerActs
{
    public sealed record ActSheetSystem(long SystemId, string Label, bool IsSub, long ParentId);

    public sealed record ActSheetRow(
        long PositionId, long SystemId, string NumText, string Name, string Unit,
        decimal QtyByKp, decimal Price, decimal WorkPrice, bool OutOfKp, string WorkCode,
        long? LineId, decimal LineQty, string LineFormula, decimal LinePrice, decimal LineWorkPrice,
        decimal ClosedByOthers, decimal Issued, bool TrackIssues);

    public sealed record ActSheetData(
        IReadOnlyList<ActSheetSystem> Systems,
        IReadOnlyList<ActSheetRow> Positions,
        IReadOnlyDictionary<long, string> RefNumbers,
        IReadOnlyDictionary<long, string> RefDisplay,
        bool NumManual, string Form);

    /// <summary>
    /// Строки окна акта (ks_view._fill_act): верхние позиции сметы по системам,
    /// строки этого акта, закрыто другими актами, выдано на объект. Номера «№» —
    /// как kp_numbers/extra_numbers в export_ks.py; RefNumbers/RefDisplay —
    /// ссылки формул «КN» (act_qty.bind/display).
    /// </summary>
    public static ActSheetData ActSheet(SqlConnection conn, long actId, long contractId)
    {
        var form = "";
        using (var read = new SqlCommand("SELECT ISNULL(form,N'') FROM dbo.contracts WHERE id=@id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            var value = read.ExecuteScalar();
            form = value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        var rules = TagRules.Load(conn);
        var numManual = rules.Has(form, "num_manual");

        var systemRows = new List<(long Id, string Name, long Parent)>();
        using (var read = new SqlCommand(
            "SELECT s.id, s.name, ISNULL(s.parent_id,0) FROM dbo.systems s " +
            "WHERE s.contract_id=@id ORDER BY s.[order], s.id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            while (r.Read())
                systemRows.Add((
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture)));
        }
        var nameById = systemRows.ToDictionary(s => s.Id, s => s.Name);
        // System.label: «П1 › отметка +0,000» — подразделы с родителем
        var systems = systemRows
            .Select(s => new ActSheetSystem(s.Id,
                s.Parent != 0 ? nameById.GetValueOrDefault(s.Parent) + " › " + s.Name : s.Name,
                s.Parent != 0, s.Parent))
            .ToList();

        var closedByOthers = ClosedByActs(conn, contractId, actId);
        var issued = ReadIssued(conn, contractId);
        var tracked = ReadTracked(conn, contractId);

        var positions = new List<ActSheetRow>();
        using (var read = new SqlCommand(
            "SELECT p.id, p.system_id, p.num, p.name, p.unit, p.qty, p.price, p.work_price, p.out_of_kp, p.basis, " +
            "al.id, al.qty, al.qty_formula, al.price, al.work_price " +
            "FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "LEFT JOIN dbo.act_lines al ON al.act_id=@act AND al.position_id=p.id " +
            "WHERE sy.contract_id=@cid AND p.parent_id IS NULL " +
            "ORDER BY sy.[order], sy.id, p.[order], p.id", conn))
        {
            read.Parameters.Add("@act", SqlDbType.BigInt).Value = actId;
            read.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            decimal D(int i) => r.IsDBNull(i) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
            string S(int i) => r.IsDBNull(i) ? "" :
                Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            while (r.Read())
                positions.Add(new ActSheetRow(
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                    S(2), S(3), S(4), D(5), D(6), D(7),
                    !r.IsDBNull(8) && Convert.ToBoolean(r.GetValue(8), CultureInfo.InvariantCulture),
                    S(9),
                    r.IsDBNull(10) ? null : Convert.ToInt64(r.GetValue(10), CultureInfo.InvariantCulture),
                    D(11), S(12), D(13), D(14),
                    closedByOthers.GetValueOrDefault(0), 0m, false));
        }
        // закрыто другими актами / выдано / признак «следим за выдачей» — по позиции
        positions = positions.Select(p => p with
        {
            ClosedByOthers = closedByOthers.GetValueOrDefault(p.PositionId),
            Issued = issued.GetValueOrDefault(p.PositionId),
            TrackIssues = tracked.Contains(p.PositionId),
        }).ToList();

        // «№» позиции сметы (kp_numbers): «Без системы» не считается, вне КП пропускается
        var kpNumbers = new Dictionary<long, int>();
        var num = 1;
        foreach (var sy in systemRows)
        {
            if (sy.Name != "Без системы")
                num += 1;
            foreach (var p in positions)
            {
                if (p.SystemId != sy.Id || p.OutOfKp)
                    continue;
                kpNumbers[p.PositionId] = num;
                num += 1;
            }
        }

        // номера строк вне сметы этого акта (extra_numbers): «якорь.счётчик»
        var extraNumbers = new Dictionary<long, string>();
        var anchorNumber = "";
        var suffix = 0;
        foreach (var sy in systemRows)
        {
            foreach (var p in positions)
            {
                if (p.SystemId != sy.Id)
                    continue;
                if (!p.OutOfKp)
                {
                    anchorNumber = (numManual ? p.NumText.Trim() : "") is { Length: > 0 } m ? m
                        : kpNumbers.GetValueOrDefault(p.PositionId).ToString(CultureInfo.InvariantCulture);
                    suffix = 0;
                }
                else if (p.LineId is not null)
                {
                    suffix += 1;
                    extraNumbers[p.PositionId] = $"{anchorNumber}.{suffix}";
                }
            }
        }

        // Ссылки формул «КN»: только однозначные, целиком числовые номера
        var refNumbers = new Dictionary<long, string>();
        foreach (var p in positions)
        {
            if (p.OutOfKp)
                continue;
            var manual = numManual ? p.NumText.Trim() : "";
            refNumbers[p.PositionId] = (manual.Length > 0 ? manual
                    : kpNumbers.GetValueOrDefault(p.PositionId).ToString(CultureInfo.InvariantCulture))
                .Replace(',', '.').Trim();
        }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var v in refNumbers.Values)
            counts[v] = counts.GetValueOrDefault(v) + 1;
        var refDisplay = new Dictionary<long, string>();
        foreach (var (pid, v) in refNumbers)
            if (counts[v] == 1 && System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d+(\.\d+)*$"))
                refDisplay[pid] = v;

        var rows = new List<ActSheetRow>(positions.Count);
        foreach (var p in positions)
        {
            var numText = p.OutOfKp
                ? extraNumbers.GetValueOrDefault(p.PositionId, "")
                : (numManual ? p.NumText : kpNumbers.GetValueOrDefault(p.PositionId).ToString(CultureInfo.InvariantCulture));
            rows.Add(p with { NumText = numText });
        }
        return new ActSheetData(systems, rows, refNumbers, refDisplay, numManual, form);
    }

    /// <summary>Выдано на объект (Position.issued): движения out минус back.</summary>
    public static Dictionary<long, decimal> ReadIssued(SqlConnection conn, long contractId,
        SqlTransaction? transaction = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var map = new Dictionary<long, decimal>();
        using var cmd = new SqlCommand(
            "SELECT m.position_id, " +
            "SUM(CASE WHEN m.kind=N'out' THEN m.qty WHEN m.kind=N'back' THEN -m.qty ELSE 0 END) " +
            "FROM dbo.movements m " +
            "JOIN dbo.positions p ON p.id=m.position_id " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@id GROUP BY m.position_id", conn, transaction);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var cancellation = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            var pid = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            map[pid] = r.IsDBNull(1) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
        }
        return map;
    }

    /// <summary>Позиции с движениями или этапом закупки — по ним «сдаём больше, чем выдано».</summary>
    private static HashSet<long> ReadTracked(SqlConnection conn, long contractId)
    {
        var set = new HashSet<long>();
        using var cmd = new SqlCommand(
            "SELECT DISTINCT p.id FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "LEFT JOIN dbo.movements m ON m.position_id=p.id " +
            "LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " +
            "WHERE sy.contract_id=@id AND (m.id IS NOT NULL OR (pr.id IS NOT NULL AND ISNULL(pr.stage,N'')<>N''))", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            set.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        return set;
    }
}
