using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Строка вкладки «Материалы» (cost_view._pos_item): верхняя позиция или её комплектующее.</summary>
public sealed record CostMaterialsPosition(
    long Id, long SystemId, long? ParentId, string Name, string Unit, decimal Qty,
    decimal Price, decimal? Plan, decimal? Fact, string Supplier,
    string CostColor, string CostNote, bool Work)
{
    /// <summary>Закупочная за единицу: факт по счёту, иначе плановый закуп (Position.cost_unit).</summary>
    public decimal? UnitCost => Fact ?? Plan;
}

/// <summary>Данные вкладки «Материалы» КП (cost_view.load): системы в порядке КП, все строки, транспорт.</summary>
public sealed record CostMaterialsData(
    IReadOnlyList<(long Id, string Name, bool IsSub, long? ParentId)> Systems,
    IReadOnlyList<CostMaterialsPosition> Positions,
    decimal TransportAmount, decimal? TransportCost, bool Locked)
{
    public string TransportNoteJson { get; init; } = "";
}

public static partial class SqlServerCostSummary
{
    /// <summary>
    /// Загрузка «Материалов» (cost_view.load + B.preload_contract): системы с подразделами,
    /// все верхние строки и комплектующие (вне КП пропущены), закуп (факт/план), поставщик,
    /// цвет строки, пометки, транспорт договора. Один метод — три запроса.
    /// </summary>
    public static CostMaterialsData MaterialsData(string connectionString, int contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        return MaterialsData(conn, contractId);
    }

    /// <summary>Read on the caller's guarded connection without another login.
    /// The caller fences the complete snapshot with the SQL revision.</summary>
    public static CostMaterialsData MaterialsData(SqlConnection conn, int contractId,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var systems = new List<(long Id, string Name, bool IsSub, long? ParentId)>();
        using (var read = new SqlCommand(
            "SELECT id, name, parent_id FROM dbo.systems WHERE contract_id=@id ORDER BY [order], id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                systems.Add((
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    !r.IsDBNull(2),
                    r.IsDBNull(2) ? null : Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture)));
            }
        }

        var positions = new List<CostMaterialsPosition>();
        using (var read = new SqlCommand(
            "SELECT p.id, p.system_id, p.parent_id, p.name, p.unit, p.qty, p.price, p.purchase_price, " +
            "pr.cost_price, sup.name, p.cost_color, p.cost_note " +
            "FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " +
            "LEFT JOIN dbo.suppliers sup ON sup.id=pr.supplier_id " +
            "WHERE sy.contract_id=@id AND ISNULL(p.out_of_kp,0)=0 " +
            "ORDER BY sy.[order], sy.id, p.[order], p.id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            string Text(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            decimal? Dec(int i) => r.IsDBNull(i)
                ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var name = Text(3);
                positions.Add(new CostMaterialsPosition(
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                    r.IsDBNull(2) ? null : Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
                    name, Text(4),
                    r.IsDBNull(5) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture)),
                    r.IsDBNull(6) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(6), CultureInfo.InvariantCulture)),
                    Dec(7), Dec(8), Text(9), Text(10), Text(11),
                    EstimateStatistics.IsWorkName(name)));
            }
        }

        decimal transportAmount = 0m;
        decimal? transportCost = null;
        bool locked = false;
        string noteJson = "";
        using (var read = new SqlCommand(
            "SELECT transport_amount, transport_cost, transport_note, locked FROM dbo.contracts WHERE id=@id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            if (r.Read())
            {
                transportAmount = r.IsDBNull(0)
                    ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
                transportCost = r.IsDBNull(1)
                    ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
                noteJson = r.IsDBNull(2) ? "" : r.GetString(2);
                locked = !r.IsDBNull(3) && Convert.ToBoolean(r.GetValue(3), CultureInfo.InvariantCulture);
            }
        }
        token.ThrowIfCancellationRequested();
        return new CostMaterialsData(systems, positions, transportAmount, transportCost, locked)
        {
            TransportNoteJson = noteJson,
        };
    }
}
