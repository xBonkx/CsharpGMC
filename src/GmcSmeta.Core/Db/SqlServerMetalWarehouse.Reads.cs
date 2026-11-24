using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed record MetalStockView(long MaterialId, string Category, string Material,
    long Ours, long OursPieces, long Supplier, long SupplierPieces,
    long RemainingDemand, long Reserved, long Available, long Surplus, long Deficit);

public sealed record MetalPlaceView(long MaterialId, string Place, long Area, long Pieces);
public sealed record MetalObjectView(long MaterialId, long? ObjectId, string ObjectName,
    string Category, string Material, long Demand, long Reserved, long Issued, long Remaining);
public sealed record MetalLotView(long Id, long MaterialId, string Material, string Supplier, string Invoice,
    long Ordered, int OrderedPieces, bool Paid, long Received, int ReceivedPieces, DateTime CreatedUtc)
{
    public long AtSupplier => Paid ? Ordered - Received : 0;
    public int PiecesAtSupplier => Paid ? OrderedPieces - ReceivedPieces : 0;
}
public sealed record MetalSnapshot(string Revision, bool Initialized, IReadOnlyList<MetalStockView> Stock,
    IReadOnlyList<MetalPlaceView> Places, IReadOnlyList<MetalObjectView> Objects,
    IReadOnlyList<MetalLotView> Lots);

public sealed partial class SqlServerMetalWarehouse
{
    /// <summary>
    /// Aggregated server-side dashboard: never downloads the full journal.
    /// A serializable read produces one consistent snapshot for all five grids.
    /// </summary>
    public MetalSnapshot ReadSnapshot()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        string revision;
        using (var rev = new SqlCommand(
            "SELECT [value] FROM dbo.meta WHERE [key]=N'rev'", conn, tx))
        {
            revision = Convert.ToString(rev.ExecuteScalar(), CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException("Нет meta.rev.");
        }
        bool initialized;
        using (var cmd = new SqlCommand(
            "SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.gmc_metal_opening) " +
            "OR EXISTS(SELECT 1 FROM dbo.meta WHERE [key]=N\'metal_warehouse_initialized\') " +
            "THEN CONVERT(BIGINT,1) ELSE CONVERT(BIGINT,0) END", conn, tx))
            initialized = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;

        var materials = new Dictionary<long, (string Category, string Name)>();
        using (var cmd = new SqlCommand(
            "SELECT id,category,material FROM dbo.gmc_metal_materials ORDER BY category,material", conn, tx))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) materials.Add(r.GetInt64(0), (r.GetString(1), r.GetString(2)));

        // Credit and debit stock legs are grouped in SQL, not projected from full ledger in UI.
        var places = new List<MetalPlaceView>();
        using (var cmd = new SqlCommand("""
SELECT material_id,place,SUM(delta_area) AS area,SUM(delta_pieces) AS pieces
FROM (
 SELECT material_id,target_place AS place,area_milli AS delta_area,CONVERT(BIGINT,pieces) AS delta_pieces
 FROM dbo.gmc_metal_movements WHERE target_place<>N''
 UNION ALL
 SELECT material_id,source_place AS place,-area_milli,-CONVERT(BIGINT,pieces)
 FROM dbo.gmc_metal_movements
 WHERE source_place<>N'' AND kind NOT IN (N'Резерв',N'Снять резерв')
) movements
GROUP BY material_id,place
""", conn, tx))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                places.Add(new MetalPlaceView(r.GetInt64(0), r.GetString(1),
                    Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture)));

        var objectAggregates = new Dictionary<(long Material, string Key), (long? Id, string Name, long Demand, long Issued, long Reserved)>();
        using (var cmd = new SqlCommand("""
SELECT material_id,object_id,object_name,
 SUM(CASE WHEN kind=N'Потребность' THEN area_milli ELSE CONVERT(BIGINT,0) END),
 SUM(CASE WHEN kind=N'Выдача' THEN area_milli WHEN kind=N'Возврат' THEN -area_milli ELSE CONVERT(BIGINT,0) END),
 SUM(reservation_milli)
FROM dbo.gmc_metal_movements
WHERE object_id IS NOT NULL OR object_name<>N''
GROUP BY material_id,object_id,object_name
""", conn, tx))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var materialId = r.GetInt64(0);
                long? obj = r.IsDBNull(1) ? null : r.GetInt64(1);
                var name = r.GetString(2);
                var key = obj is long id ? "O:" + id.ToString(CultureInfo.InvariantCulture) : "N:" + name.ToUpperInvariant();
                var tuple = (materialId, key);
                objectAggregates.TryGetValue(tuple, out var previous);
                objectAggregates[tuple] = (
                    obj, name.Length > 0 ? name : previous.Name ?? "",
                    previous.Demand + r.GetInt64(3),
                    previous.Issued + r.GetInt64(4),
                    previous.Reserved + r.GetInt64(5));
            }
        }

        var byMaterial = new Dictionary<long, (long Need, long Held)>();
        var objects = new List<MetalObjectView>();
        foreach (var row in objectAggregates)
        {
            var id = row.Key.Material;
            var v = row.Value;
            if (v.Demand == 0 && v.Issued == 0 && v.Reserved == 0) continue;
            var remain = Math.Max(0, v.Demand - v.Issued);
            var prior = byMaterial.GetValueOrDefault(id);
            byMaterial[id] = (prior.Need + remain, prior.Held + v.Reserved);
            var info = materials[id];
            objects.Add(new MetalObjectView(id, v.Id, v.Name, info.Category, info.Name,
                v.Demand, v.Reserved, v.Issued, remain));
        }
        objects.Sort((a,b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.ObjectName, b.ObjectName));

        // Unassigned reservations can exist only in external/imported data; still subtract them.
        var allReserved = new Dictionary<long, long>();
        using (var cmd = new SqlCommand("""
SELECT material_id,SUM(reservation_milli) FROM dbo.gmc_metal_movements
WHERE reservation_milli<>0 GROUP BY material_id
""", conn, tx))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) allReserved[r.GetInt64(0)] = r.GetInt64(1);

        var stock = new List<MetalStockView>();
        foreach (var pair in materials)
        {
            var id = pair.Key;
            var ourArea = places.Where(p => p.MaterialId == id && p.Place != MetalWarehouseRules.SupplierPlace).Sum(p => p.Area);
            var ourPieces = places.Where(p => p.MaterialId == id && p.Place != MetalWarehouseRules.SupplierPlace).Sum(p => p.Pieces);
            var supplier = places.Where(p => p.MaterialId == id && p.Place == MetalWarehouseRules.SupplierPlace).Sum(p => p.Area);
            var supplierPieces = places.Where(p => p.MaterialId == id && p.Place == MetalWarehouseRules.SupplierPlace).Sum(p => p.Pieces);
            var needed = byMaterial.GetValueOrDefault(id).Need;
            var reserved = allReserved.GetValueOrDefault(id);
            var balance = ourArea + supplier - needed;
            stock.Add(new MetalStockView(id, pair.Value.Category, pair.Value.Name,
                ourArea, ourPieces, supplier, supplierPieces, needed, reserved,
                ourArea - reserved, Math.Max(0, balance), Math.Max(0, -balance)));
        }
        stock.Sort((a,b) =>
        {
            var c = StringComparer.CurrentCultureIgnoreCase.Compare(a.Category, b.Category);
            return c != 0 ? c : StringComparer.CurrentCultureIgnoreCase.Compare(a.Material,b.Material);
        });

        var lots = new List<MetalLotView>();
        using (var cmd = new SqlCommand("""
SELECT l.id,l.material_id,m.material,l.supplier,l.invoice,l.area_milli,l.pieces,
  MAX(CASE WHEN e.kind=N'Оплата партии' THEN 1 ELSE 0 END) AS paid,
  COALESCE(SUM(CASE WHEN e.kind=N'Получение партии' THEN e.area_milli ELSE CONVERT(BIGINT,0) END),0),
  COALESCE(SUM(CASE WHEN e.kind=N'Получение партии' THEN CONVERT(BIGINT,e.pieces) ELSE CONVERT(BIGINT,0) END),0),
  l.created_utc
FROM dbo.gmc_metal_lots l
JOIN dbo.gmc_metal_materials m ON m.id=l.material_id
LEFT JOIN dbo.gmc_metal_movements e ON e.lot_id=l.id
GROUP BY l.id,l.material_id,m.material,l.supplier,l.invoice,l.area_milli,l.pieces,l.created_utc
ORDER BY l.created_utc DESC,l.id DESC
""", conn, tx))
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                lots.Add(new MetalLotView(r.GetInt64(0), r.GetInt64(1), r.GetString(2),
                    r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt32(6),
                    r.GetInt32(7) != 0, r.GetInt64(8), checked((int)r.GetInt64(9)), r.GetDateTime(10)));
        tx.Commit();
        return new MetalSnapshot(revision, initialized, stock, places, objects, lots);
    }

    public IReadOnlyList<MetalMovement> ReadJournal(int page = 0, int pageSize = 200,
        string search = "", string category = "")
    {
        if (page < 0 || pageSize is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        using var conn = Open();
        using var cmd = new SqlCommand("""
SELECT e.id,e.material_id,m.category,m.material,e.kind,e.area_milli,e.pieces,
       e.reservation_milli,e.source_place,e.target_place,e.object_id,e.object_name,
       e.employee,e.note,e.lot_id,e.supplier,e.invoice,e.created_utc
FROM dbo.gmc_metal_movements e
JOIN dbo.gmc_metal_materials m ON m.id=e.material_id
WHERE (@cat=N'' OR m.category=@cat)
  AND (@search=N'' OR m.material LIKE @pattern OR e.object_name LIKE @pattern OR
       e.invoice LIKE @pattern OR e.note LIKE @pattern OR e.kind LIKE @pattern)
ORDER BY e.id DESC OFFSET @offset ROWS FETCH NEXT @size ROWS ONLY
""", conn);
        cmd.Parameters.Add("@cat", SqlDbType.NVarChar, 32).Value = category;
        cmd.Parameters.Add("@search", SqlDbType.NVarChar, 100).Value = search.Trim()[..Math.Min(100, search.Trim().Length)];
        cmd.Parameters.Add("@pattern", SqlDbType.NVarChar, 205).Value =
            "%" + search.Trim()[..Math.Min(100, search.Trim().Length)].Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
        cmd.Parameters.Add("@offset", SqlDbType.Int).Value = checked(page * pageSize);
        cmd.Parameters.Add("@size", SqlDbType.Int).Value = pageSize;
        using var r = cmd.ExecuteReader();
        var result = new List<MetalMovement>();
        while (r.Read())
            result.Add(new MetalMovement(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
                r.GetString(4), r.GetInt64(5), r.GetInt32(6), r.GetInt64(7), r.GetString(8),
                r.GetString(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.GetString(11),
                r.GetString(12), r.GetString(13), r.IsDBNull(14) ? null : r.GetInt64(14),
                r.GetString(15), r.GetString(16), r.GetDateTime(17)));
        return result;
    }
}
