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
    public static ActSheetData ActSheet(SqlConnection conn, long actId, long contractId,
        CancellationToken token = default, TagRules? snapshotRules = null, string? snapshotForm = null)
    {
        token.ThrowIfCancellationRequested();
        var form = snapshotForm ?? "";
        if (snapshotForm is null)
        using (var read = new SqlCommand("SELECT ISNULL(form,N'') FROM dbo.contracts WHERE id=@id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            var value = token.CanBeCanceled ? read.ExecuteScalarAsync(token).GetAwaiter().GetResult() : read.ExecuteScalar();
            form = value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        var rules = snapshotRules ?? TagRules.Load(conn, token);
        var numManual = rules.Has(form, "num_manual");

        var systemRows = new List<(long Id, string Name, long Parent)>();
        using (var read = new SqlCommand(
            "SELECT s.id, s.name, ISNULL(s.parent_id,0) FROM dbo.systems s " +
            "WHERE s.contract_id=@id ORDER BY s.[order], s.id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                systemRows.Add((
                    SqlInteger.ReadInt64(r, 0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    SqlInteger.ReadInt64(r, 2)));
            }
        }
        var nameById = systemRows.ToDictionary(s => s.Id, s => s.Name);
        // System.label: «П1 › отметка +0,000» — подразделы с родителем
        var systems = systemRows
            .Select(s => new ActSheetSystem(s.Id,
                s.Parent != 0 ? nameById.GetValueOrDefault(s.Parent) + " › " + s.Name : s.Name,
                s.Parent != 0, s.Parent))
            .ToList();

        var closedByOthers = ClosedByActs(conn, contractId, actId, token: token);
        var issued = ReadIssued(conn, contractId, token: token);
        var tracked = ReadTracked(conn, contractId, token);

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
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            decimal D(int i) => r.IsDBNull(i) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
            string S(int i) => r.IsDBNull(i) ? "" :
                Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var positionId = SqlInteger.ReadInt64(r, 0);
                positions.Add(new ActSheetRow(
                    positionId,
                    SqlInteger.ReadInt64(r, 1),
                    S(2), S(3), S(4), D(5), D(6), D(7),
                    !r.IsDBNull(8) && SqlFlag.Read(r, 8),
                    S(9),
                    r.IsDBNull(10) ? null : SqlInteger.ReadInt64(r, 10),
                    D(11), S(12), D(13), D(14),
                    closedByOthers.GetValueOrDefault(positionId),
                    issued.GetValueOrDefault(positionId), tracked.Contains(positionId)));
            }
        }

        var (rows, refNumbers, refDisplay) = KsActSheetProjection.NumberRows(systemRows, positions, numManual, token);
        token.ThrowIfCancellationRequested();
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
            var pid = SqlInteger.ReadInt64(r, 0);
            map[pid] = r.IsDBNull(1) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
        }
        return map;
    }

    /// <summary>Позиции с движениями или этапом закупки — по ним «сдаём больше, чем выдано».</summary>
    private static HashSet<long> ReadTracked(SqlConnection conn, long contractId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var set = new HashSet<long>();
        using var cmd = new SqlCommand(
            "SELECT DISTINCT p.id FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "LEFT JOIN dbo.movements m ON m.position_id=p.id " +
            "LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " +
            "WHERE sy.contract_id=@id AND (m.id IS NOT NULL OR (pr.id IS NOT NULL AND ISNULL(pr.stage,N'')<>N''))", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var cancellation = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            set.Add(SqlInteger.ReadInt64(r, 0));
        }
        return set;
    }
}
