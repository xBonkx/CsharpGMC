using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed partial class SqlServerMetalWarehouse
{
    private static List<MetalMovement> ReadMaterialEntries(SqlConnection conn, SqlTransaction tx, long materialId)
    {
        using var cmd = new SqlCommand("""
SELECT e.id,e.material_id,m.category,m.material,e.kind,e.area_milli,e.pieces,
       e.reservation_milli,e.source_place,e.target_place,e.object_id,e.object_name,
       e.employee,e.note,e.lot_id,e.supplier,e.invoice,e.created_utc
FROM dbo.gmc_metal_movements e WITH (UPDLOCK,HOLDLOCK)
JOIN dbo.gmc_metal_materials m ON m.id=e.material_id
WHERE e.material_id=@id ORDER BY e.id
""", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = materialId;
        using var r = cmd.ExecuteReader();
        var result = new List<MetalMovement>();
        while (r.Read())
        {
            long? obj = r.IsDBNull(10) ? null : SqlInteger.ReadInt64(r, 10);
            long? lot = r.IsDBNull(14) ? null : SqlInteger.ReadInt64(r, 14);
            result.Add(new MetalMovement(
                SqlInteger.ReadInt64(r, 0), SqlInteger.ReadInt64(r, 1), r.GetString(2), r.GetString(3), r.GetString(4),
                SqlInteger.ReadInt64(r, 5), SqlInteger.ReadInt32(r, 6), SqlInteger.ReadInt64(r, 7), r.GetString(8), r.GetString(9),
                obj, r.GetString(11), r.GetString(12), r.GetString(13), lot,
                r.GetString(15), r.GetString(16), r.GetDateTime(17)));
        }
        return result;
    }

    /// <summary>Post with a material-scoped balance check *inside* the serializable transaction.</summary>
    public long Post(MetalOperation operation, string expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.OperationId == Guid.Empty)
            throw new ArgumentException("Пустой идентификатор операции.");
        MetalWarehouseRules.CheckMaterial(operation.Category, operation.Material);
        SqlServerWorkspace.RequirePilotDatabase(_connectionString);
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn, tx);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        var materialId = FindMaterial(conn, tx, operation.Category, operation.Material);
        string objName = "";
        bool objExists = false;
        if (operation.ObjectId is long objectId)
        {
            using var cmd = new SqlCommand(
                "SELECT name FROM dbo.objects WITH (HOLDLOCK) WHERE id=@id", conn, tx);
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
            objName = cmd.ExecuteScalar() as string ?? "";
            objExists = objName.Length > 0;
        }
        var current = ReadMaterialEntries(conn, tx, materialId);
        var prepared = MetalWarehouseRules.Prepare(operation, current, objExists);
        var id = Append(conn, tx, materialId, operation.Kind, prepared.Area, prepared.Pieces,
            prepared.Reservation, prepared.Source, prepared.Destination, operation.ObjectId,
            objName, operation.Note.Trim(), operationId: operation.OperationId);
        Finish(conn, tx);
        return id;
    }

    /// <summary>One-time atomic SQL import. Zero-stock entries preserve available catalog materials.</summary>
    public int ImportOpening(IReadOnlyList<MetalOpening> records, string sourceName, byte[] originalFile,
        string expectedRevision)
    {
        MetalWarehouseRules.ValidateOpening(records);
        if (sourceName.Length > 500 || originalFile.Length == 0)
            throw new ArgumentException("Укажите исходную книгу Excel (имя до 500 символов).");
        var digest = Convert.ToHexString(SHA256.HashData(originalFile));
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn, tx);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        using (var check = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.gmc_metal_opening WITH (UPDLOCK,HOLDLOCK)", conn, tx))
        {
            if (SqlInteger.RequireInt64(check.ExecuteScalar()) != 0)
                throw new InvalidOperationException("Начальные остатки уже перенесены.");
        }
        using (var check = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.gmc_metal_movements WITH (UPDLOCK,HOLDLOCK)", conn, tx))
        {
            if (SqlInteger.RequireInt64(check.ExecuteScalar()) != 0)
                throw new InvalidOperationException("Склад содержит операции; повторный первоначальный импорт запрещён.");
        }
        // An existing Python metal warehouse must never be silently imported for a second time.
        // Its historical ledger requires a separate, audited migration, not another opening snapshot.
        using (var previous = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.meta WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE [key]=N'metal_warehouse_initialized'", conn, tx))
        {
            if (SqlInteger.RequireInt64(previous.ExecuteScalar()) != 0)
                throw new InvalidOperationException(
                    "В Python уже выполнен первоначальный импорт. Требуется перенос истории, а не повторная загрузка Excel.");
        }
        using (var legacy = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.metal_entries WITH (UPDLOCK,HOLDLOCK)", conn, tx))
        {
            if (SqlInteger.RequireInt64(legacy.ExecuteScalar()) != 0)
                throw new InvalidOperationException(
                    "В базе уже есть движения металла Python; повторный импорт начальных остатков запрещён.");
        }
        var names = new Dictionary<string, List<(long Id, string Name)>>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = new SqlCommand("SELECT id,name FROM dbo.objects", conn, tx))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var name = r.GetString(1);
                var key = name.Trim();
                if (!names.TryGetValue(key, out var found)) names[key] = found = [];
                found.Add((Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), name));
            }
        }
        var count = 0;
        foreach (var record in records)
        {
            var materialId = FindMaterial(conn, tx, record.Category, record.Material, create: true);
            foreach (var (place, area) in record.Stock)
            {
                var unitPieces = record.Pieces.FirstOrDefault(p => p.Place == place).Pieces;
                Append(conn, tx, materialId, "Начальный запас", MetalWarehouseRules.Units(area),
                    MetalWarehouseRules.Pieces(unitPieces), destination: place);
                count++;
            }
            foreach (var (objectName, area) in record.Demand)
            {
                names.TryGetValue(objectName.Trim(), out var matches);
                var boundObjectId = matches?.Count == 1 ? matches[0].Id : (long?)null;
                Append(conn, tx, materialId, "Потребность", MetalWarehouseRules.Units(area),
                    objectId: boundObjectId, objectName: objectName.Trim());
                count++;
            }
        }
        using (var cmd = new SqlCommand(
            "INSERT dbo.gmc_metal_opening(id,source_file,source_sha256,employee) " +
            "VALUES (1,@file,@hash,@user)", conn, tx))
        {
            cmd.Parameters.Add("@file", SqlDbType.NVarChar, 500).Value = sourceName;
            cmd.Parameters.Add("@hash", SqlDbType.Char, 64).Value = digest;
            cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = OperatorName();
            cmd.ExecuteNonQuery();
        }
        Finish(conn, tx);
        return count;
    }

    public long AddLot(string category, string material, string supplier, string invoice,
        decimal area, decimal pieces, string expectedRevision)
    {
        MetalWarehouseRules.CheckMaterial(category, material);
        var milli = MetalWarehouseRules.Units(area);
        var count = MetalWarehouseRules.Pieces(pieces);
        MetalLotRules.ValidateOrder(category, area, pieces);
        supplier = supplier.Trim();
        invoice = invoice.Trim();
        if (category != "Бухты" || supplier.Length is 0 or > 255 || invoice.Length is 0 or > 120 ||
            milli <= 0 || count == 0)
            throw new ArgumentException("Выберите бухты и укажите поставщика, счёт, площадь и количество.");
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn, tx);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        var materialId = FindMaterial(conn, tx, category, material);
        using var cmd = new SqlCommand("""
INSERT dbo.gmc_metal_lots(material_id,supplier,invoice,area_milli,pieces)
OUTPUT INSERTED.id VALUES (@material,@supplier,@invoice,@area,@pieces)
""", conn, tx);
        cmd.Parameters.Add("@material", SqlDbType.BigInt).Value = materialId;
        cmd.Parameters.Add("@supplier", SqlDbType.NVarChar, 255).Value = supplier;
        cmd.Parameters.Add("@invoice", SqlDbType.NVarChar, 120).Value = invoice;
        cmd.Parameters.Add("@area", SqlDbType.BigInt).Value = milli;
        cmd.Parameters.Add("@pieces", SqlDbType.Int).Value = count;
        // Unique SQL index prevents a second invoice for the same supplier/material.
        var lotId = SqlInteger.RequireInt64(cmd.ExecuteScalar());
        Append(conn, tx, materialId, "Партия", milli, count, lotId: lotId,
            supplier: supplier, invoice: invoice);
        Finish(conn, tx);
        return lotId;
    }

    public void UpdateLot(long lotId, string expectedRevision,
        decimal? receiveArea = null, decimal? receivePieces = null, string destination = "")
    {
        if (lotId <= 0) throw new ArgumentException("Партия не найдена.");
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn, tx);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        long materialId, orderedArea;
        int orderedPieces;
        string supplier, invoice, materialCategory;
        using (var cmd = new SqlCommand("""
SELECT l.material_id,l.area_milli,l.pieces,l.supplier,l.invoice,m.category
FROM dbo.gmc_metal_lots l WITH (UPDLOCK,HOLDLOCK)
JOIN dbo.gmc_metal_materials m ON m.id=l.material_id WHERE l.id=@id
""", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = lotId;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Партия не найдена.");
            materialId = SqlInteger.ReadInt64(r, 0); orderedArea = SqlInteger.ReadInt64(r, 1); orderedPieces = SqlInteger.ReadInt32(r, 2);
            supplier = r.GetString(3); invoice = r.GetString(4); materialCategory = r.GetString(5);
        }
        if (materialCategory != "Бухты") throw new ArgumentException("Партии доступны только для бухт.");
        long receivedArea = 0;
        int receivedPieces = 0;
        bool paid = false;
        using (var cmd = new SqlCommand(
            "SELECT kind,area_milli,pieces FROM dbo.gmc_metal_movements WITH (UPDLOCK,HOLDLOCK) WHERE lot_id=@id",
            conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = lotId;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var kind = r.GetString(0);
                if (kind == "Оплата партии") paid = true;
                if (kind == "Получение партии")
                {
                    receivedArea += SqlInteger.ReadInt64(r, 1);
                    receivedPieces += SqlInteger.ReadInt32(r, 2);
                }
            }
        }
        if (receiveArea is null)
        {
            MetalLotRules.ValidatePayment(paid);
            Append(conn, tx, materialId, "Оплата партии", orderedArea, orderedPieces,
                destination: MetalWarehouseRules.SupplierPlace, lotId: lotId,
                supplier: supplier, invoice: invoice);
        }
        else
        {
            if (receivePieces is null) throw new ArgumentException("Укажите количество бухт.");
            var milli = MetalWarehouseRules.Units(receiveArea.Value);
            var pieces = MetalWarehouseRules.Pieces(receivePieces.Value);
            MetalLotRules.ValidateReceipt(paid, destination, orderedArea, orderedPieces,
                receivedArea, receivedPieces, milli, pieces);
            Append(conn, tx, materialId, "Получение партии", milli, pieces,
                source: MetalWarehouseRules.SupplierPlace, destination: destination, lotId: lotId,
                supplier: supplier, invoice: invoice);
        }
        Finish(conn, tx);
    }

    public sealed record SharedWorkbook(byte[] Data, long Revision);

    /// <summary>Revisioned workbook exchange; saving stale versions never overwrites a colleague's edits.</summary>
    public SharedWorkbook? ReadRegister()
    {
        using var conn = Open();
        using var cmd = new SqlCommand(
            "SELECT workbook,revision FROM dbo.gmc_metal_register WHERE id=1", conn);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new SharedWorkbook((byte[])r.GetValue(0), SqlInteger.ReadInt64(r, 1)) : null;
    }

    public long SaveRegister(byte[] workbook, long? expectedRevision, string expectedMainRevision)
    {
        if (workbook is null || workbook.Length == 0 || workbook.Length > 5_000_000)
            throw new ArgumentException("Исходная Excel-книга должна быть не больше 5 МБ.");
        var sha = Convert.ToHexString(SHA256.HashData(workbook));
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn, tx, "GMC.Metal.Register");
        SqlServerWorkspace.CheckRevision(conn, tx, expectedMainRevision);
        long newVersion;
        if (expectedRevision is null)
        {
            using var cmd = new SqlCommand("""
IF NOT EXISTS(SELECT 1 FROM dbo.gmc_metal_register WITH (UPDLOCK,HOLDLOCK) WHERE id=1)
BEGIN
 INSERT dbo.gmc_metal_register(id,workbook,revision,employee) VALUES (1,@bytes,1,@user);
 SELECT CONVERT(BIGINT,1);
END
ELSE SELECT CONVERT(BIGINT,0);
""", conn, tx);
            cmd.Parameters.Add("@bytes", SqlDbType.VarBinary, -1).Value = workbook;
            cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = OperatorName();
            newVersion = SqlInteger.RequireInt64(cmd.ExecuteScalar());
        }
        else
        {
            using var cmd = new SqlCommand("""
UPDATE dbo.gmc_metal_register SET workbook=@bytes,revision=revision+1,
 employee=@user,saved_utc=SYSUTCDATETIME()
OUTPUT INSERTED.revision
WHERE id=1 AND revision=@expected
""", conn, tx);
            cmd.Parameters.Add("@bytes", SqlDbType.VarBinary, -1).Value = workbook;
            cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = OperatorName();
            cmd.Parameters.Add("@expected", SqlDbType.BigInt).Value = expectedRevision.Value;
            newVersion = SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? 0L);
        }
        if (newVersion == 0)
            throw new DBConcurrencyException(
                "Коллега уже сохранил изменения. Выгрузите свою книгу в Excel и обновите таблицу.");
        using (var audit = new SqlCommand("""
INSERT dbo.gmc_metal_register_history(register_revision,sha256,employee) VALUES(@rev,@sha,@user)
""", conn, tx))
        {
            audit.Parameters.Add("@rev", SqlDbType.BigInt).Value = newVersion;
            audit.Parameters.Add("@sha", SqlDbType.Char, 64).Value = sha;
            audit.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = OperatorName();
            audit.ExecuteNonQuery();
        }
        Finish(conn, tx);
        return newVersion;
    }
}
