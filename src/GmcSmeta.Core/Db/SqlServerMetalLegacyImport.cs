using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed record MetalLegacySqlPreview(
    string Server, string Database, string Revision, bool PythonOpening,
    long ExistingOpening, long ExistingMovements, long ExistingLots, long ExistingMaterials,
    long PriorLegacyImports, long PriorMappedEvents, bool TargetSchemaPresent,
    MetalLegacyPreview History)
{
    public bool Ready => PythonOpening && ExistingOpening == 0 && ExistingMovements == 0 &&
        ExistingLots == 0 && ExistingMaterials == 0 && PriorLegacyImports == 0 &&
        PriorMappedEvents == 0 && TargetSchemaPresent && History.CanImport;
}

public sealed record MetalLegacyImportReceipt(
    string SourceSha256, int EventCount, int OriginalUtcOffsetMinutes, string State);

/// <summary>
/// Opt-in, pilot-only, single-shot importer from dbo.metal_entries into the isolated
/// SQL Server warehouse. Preview and receipt verification never create schema or write.
/// The import does not change legacy rows, signed documents, prices or historical objects.
/// </summary>
public sealed class SqlServerMetalLegacyImport
{
    private readonly string _connectionString;

    public SqlServerMetalLegacyImport(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        _connectionString = connectionString;
    }

    private SqlConnection Open()
    {
        SqlServerWorkspace.RequirePilotDatabase(_connectionString);
        var c = SqlServerDatabase.Open(_connectionString);
        if (!c.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
        {
            c.Dispose();
            throw new InvalidOperationException("Фактический SQL-каталог не является GmcSmetaPilot_*.");
        }
        return c;
    }

    private static object Value(long? id) => id is long number ? number : DBNull.Value;

    private static long Scalar(SqlConnection conn, SqlTransaction? tx, string sql)
    {
        using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 60 };
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool Exists(SqlConnection conn, SqlTransaction? tx, string table) =>
        Scalar(conn, tx, "SELECT CASE WHEN OBJECT_ID(N'dbo." + table +
            "',N'U') IS NULL THEN CONVERT(BIGINT,0) ELSE CONVERT(BIGINT,1) END") == 1;

    private static long Count(SqlConnection conn, SqlTransaction? tx, string table)
    {
        // Only internal constant table names, never user-supplied identifiers.
        if (!Exists(conn, tx, table)) return 0;
        return Scalar(conn, tx, "SELECT COUNT_BIG(*) FROM dbo." + table);
    }

    private static string Revision(SqlConnection conn, SqlTransaction? tx)
    {
        using var cmd = new SqlCommand("SELECT [value] FROM dbo.meta WHERE [key]=N'rev'", conn, tx);
        return cmd.ExecuteScalar() as string ??
            throw new InvalidOperationException("Отсутствует meta.rev; миграция запрещена.");
    }

    private static List<MetalLegacyEntry> ReadHistory(SqlConnection conn, SqlTransaction? tx)
    {
        if (!Exists(conn, tx, "metal_entries"))
            throw new InvalidOperationException("Нет исходной таблицы dbo.metal_entries.");
        using var cmd = new SqlCommand("""
SELECT id,category,material,kind,quantity,pieces,reservation,[source],destination,
       object_id,object_name,lot_id,supplier,invoice,note,[user],created_at
FROM dbo.metal_entries
ORDER BY id
""", conn, tx) { CommandTimeout = 120 };
        using var r = cmd.ExecuteReader();
        var rows = new List<MetalLegacyEntry>();
        string S(int n) => r.IsDBNull(n) ? "" : r.GetString(n);
        long L(int n) => Convert.ToInt64(r.GetValue(n), CultureInfo.InvariantCulture);
        long? N(int n) => r.IsDBNull(n) ? null : L(n);
        while (r.Read())
        {
            if (rows.Count >= 100_000)
                throw new InvalidOperationException("История слишком большая: отдельная приёмка обязательна.");
            rows.Add(new MetalLegacyEntry(
                L(0), S(1), S(2), S(3), L(4), checked((int)L(5)), L(6),
                S(7), S(8), N(9), S(10), N(11), S(12), S(13), S(14),
                S(15), r.GetDateTime(16)));
        }
        return rows;
    }

    public MetalLegacySqlPreview Preview()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = Revision(conn, tx);
        var marker = Scalar(conn, tx,
            "SELECT COUNT_BIG(*) FROM dbo.meta WHERE [key]=N'metal_warehouse_initialized'") != 0;
        var history = MetalLegacyReconciliation.Preview(ReadHistory(conn, tx));
        var result = new MetalLegacySqlPreview(conn.DataSource, conn.Database, rev, marker,
            Count(conn, tx, "gmc_metal_opening"), Count(conn, tx, "gmc_metal_movements"),
            Count(conn, tx, "gmc_metal_lots"), Count(conn, tx, "gmc_metal_materials"),
            Count(conn, tx, "gmc_metal_legacy_import"), Count(conn, tx, "gmc_metal_legacy_events"),
            new[] { "gmc_metal_materials", "gmc_metal_lots", "gmc_metal_movements",
                "gmc_metal_opening", "meta", "objects" }.All(t => Exists(conn, tx, t)),
            history);
        tx.Commit(); // No writes; ends a consistent read-only snapshot.
        return result;
    }

    private static void Lock(SqlConnection conn, SqlTransaction tx)
    {
        using var cmd = new SqlCommand("""
DECLARE @result INT;
EXEC @result=sys.sp_getapplock @Resource=N'GMC.Metal.Stock',
  @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000;
SELECT @result;
""", conn, tx);
        if (Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) < 0)
            throw new TimeoutException("Не удалось взять блокировку склада металла.");
    }

    private static void RequireFresh(SqlConnection conn, SqlTransaction tx)
    {
        foreach (var table in new[] { "gmc_metal_materials", "gmc_metal_movements", "gmc_metal_lots",
                     "gmc_metal_opening", "gmc_metal_legacy_import", "gmc_metal_legacy_events" })
        {
            if (Count(conn, tx, table) != 0)
                throw new InvalidOperationException("Склад/миграция уже содержит записи (" +
                    table + "). Повторный импорт запрещён.");
        }
        foreach (var table in new[] { "gmc_metal_materials", "gmc_metal_lots",
                     "gmc_metal_movements", "gmc_metal_opening", "meta", "objects" })
            if (!Exists(conn, tx, table))
                throw new InvalidOperationException("Отсутствует необходимая таблица: dbo." + table);
        if (Scalar(conn, tx,
            "SELECT COUNT_BIG(*) FROM dbo.meta WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE [key]=N'metal_warehouse_initialized'") != 1)
            throw new InvalidOperationException("Нет однозначного Python opening-marker; ручная сверка обязательна.");
    }

    private const string ImportSchema = """
IF OBJECT_ID(N'dbo.gmc_metal_legacy_import',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.gmc_metal_legacy_import (
   id TINYINT NOT NULL CONSTRAINT PK_gmc_metal_legacy_import PRIMARY KEY CHECK(id=1),
   source_sha256 CHAR(64) NOT NULL, source_server NVARCHAR(255) NOT NULL,
   source_database NVARCHAR(128) NOT NULL, event_count INT NOT NULL,
   source_utc_offset_minutes SMALLINT NOT NULL, employee NVARCHAR(64) NOT NULL,
   imported_utc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
 );
END;
IF OBJECT_ID(N'dbo.gmc_metal_legacy_events',N'U') IS NULL
BEGIN
 CREATE TABLE dbo.gmc_metal_legacy_events (
   legacy_id BIGINT NOT NULL CONSTRAINT PK_gmc_metal_legacy_events PRIMARY KEY,
   movement_id BIGINT NOT NULL UNIQUE REFERENCES dbo.gmc_metal_movements(id),
   legacy_lot_id BIGINT NULL, legacy_created_at DATETIME2(7) NOT NULL,
   target_lot_id BIGINT NULL REFERENCES dbo.gmc_metal_lots(id),
   event_sha256 CHAR(64) NOT NULL, source_sha256 CHAR(64) NOT NULL
 );
END;
""";

    private static void ValidateObjectReferences(SqlConnection conn, SqlTransaction tx,
        IReadOnlyList<MetalLegacyEntry> rows)
    {
        var ids = new HashSet<long>();
        using (var cmd = new SqlCommand("SELECT id FROM dbo.objects WITH (HOLDLOCK)", conn, tx))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) ids.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        foreach (var row in rows)
            if (row.ObjectId is long id && !ids.Contains(id))
                throw new InvalidOperationException(
                    $"metal_entries.id={row.Id}: отсутствующий объект #{id}; перенос остановлен.");
    }

    private static long InsertMaterial(SqlConnection conn, SqlTransaction tx,
        string category, string material)
    {
        using var cmd = new SqlCommand("""
INSERT dbo.gmc_metal_materials(category,material) OUTPUT INSERTED.id VALUES(@cat,@name)
""", conn, tx);
        cmd.Parameters.Add("@cat", SqlDbType.NVarChar, 32).Value = category;
        cmd.Parameters.Add("@name", SqlDbType.NVarChar, 80).Value = material;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long InsertLot(SqlConnection conn, SqlTransaction tx, long materialId,
        MetalLegacyEntry row, DateTime createdUtc)
    {
        using var cmd = new SqlCommand("""
INSERT dbo.gmc_metal_lots(material_id,supplier,invoice,area_milli,pieces,created_utc)
OUTPUT INSERTED.id VALUES(@mat,@supplier,@invoice,@area,@pieces,@date)
""", conn, tx);
        cmd.Parameters.Add("@mat", SqlDbType.BigInt).Value = materialId;
        cmd.Parameters.Add("@supplier", SqlDbType.NVarChar, 255).Value = row.Supplier;
        cmd.Parameters.Add("@invoice", SqlDbType.NVarChar, 120).Value = row.Invoice;
        cmd.Parameters.Add("@area", SqlDbType.BigInt).Value = row.Area;
        cmd.Parameters.Add("@pieces", SqlDbType.Int).Value = row.Pieces;
        cmd.Parameters.Add("@date", SqlDbType.DateTime2).Value = createdUtc;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long InsertMovement(SqlConnection conn, SqlTransaction tx, long materialId,
        long? lotId, MetalLegacyEntry row, DateTime createdUtc)
    {
        using var cmd = new SqlCommand("""
INSERT dbo.gmc_metal_movements(operation_id,material_id,kind,area_milli,pieces,reservation_milli,
 source_place,target_place,object_id,object_name,lot_id,supplier,invoice,note,employee,created_utc)
OUTPUT INSERTED.id
VALUES(@op,@mat,@kind,@area,@pieces,@reserve,@source,@target,@obj,@objname,@lot,
 @supplier,@invoice,@note,@employee,@created)
""", conn, tx) { CommandTimeout = 60 };
        cmd.Parameters.Add("@op", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
        cmd.Parameters.Add("@mat", SqlDbType.BigInt).Value = materialId;
        cmd.Parameters.Add("@kind", SqlDbType.NVarChar, 24).Value = row.Kind;
        cmd.Parameters.Add("@area", SqlDbType.BigInt).Value = row.Area;
        cmd.Parameters.Add("@pieces", SqlDbType.Int).Value = row.Pieces;
        cmd.Parameters.Add("@reserve", SqlDbType.BigInt).Value = row.Reservation;
        cmd.Parameters.Add("@source", SqlDbType.NVarChar, 80).Value = row.Source;
        cmd.Parameters.Add("@target", SqlDbType.NVarChar, 80).Value = row.Destination;
        cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = Value(row.ObjectId);
        cmd.Parameters.Add("@objname", SqlDbType.NVarChar, 255).Value = row.ObjectName;
        cmd.Parameters.Add("@lot", SqlDbType.BigInt).Value = Value(lotId);
        cmd.Parameters.Add("@supplier", SqlDbType.NVarChar, 255).Value = row.Supplier;
        cmd.Parameters.Add("@invoice", SqlDbType.NVarChar, 120).Value = row.Invoice;
        cmd.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = row.Note;
        cmd.Parameters.Add("@employee", SqlDbType.NVarChar, 64).Value = row.User;
        cmd.Parameters.Add("@created", SqlDbType.DateTime2).Value = createdUtc;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void InsertProvenance(SqlConnection conn, SqlTransaction tx,
        MetalLegacyEntry row, long movementId, long? targetLotId, string hash)
    {
        using var cmd = new SqlCommand("""
INSERT dbo.gmc_metal_legacy_events(legacy_id,movement_id,legacy_lot_id,
 legacy_created_at,target_lot_id,event_sha256,source_sha256)
VALUES(@legacy,@movement,@lot,@created,@targetLot,@event,@source)
""", conn, tx);
        cmd.Parameters.Add("@legacy", SqlDbType.BigInt).Value = row.Id;
        cmd.Parameters.Add("@movement", SqlDbType.BigInt).Value = movementId;
        cmd.Parameters.Add("@lot", SqlDbType.BigInt).Value = Value(row.LotId);
        cmd.Parameters.Add("@created", SqlDbType.DateTime2).Value = row.CreatedAt;
        cmd.Parameters.Add("@targetLot", SqlDbType.BigInt).Value = Value(targetLotId);
        cmd.Parameters.Add("@event", SqlDbType.Char, 64).Value =
            MetalLegacyReconciliation.Hash(new[] { row });
        cmd.Parameters.Add("@source", SqlDbType.Char, 64).Value = hash;
        cmd.ExecuteNonQuery();
    }

    public MetalLegacyImportReceipt Import(string expectedServer, string expectedDatabase,
        string expectedRevision, string expectedSourceSha256, TimeSpan legacyUtcOffset,
        bool confirmedByOperator)
    {
        if (!confirmedByOperator)
            throw new InvalidOperationException("Требуется отдельное явное подтверждение исторического импорта.");
        if (legacyUtcOffset.TotalMinutes is < -840 or > 840 ||
            legacyUtcOffset.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentOutOfRangeException(nameof(legacyUtcOffset));
        if (string.IsNullOrWhiteSpace(expectedSourceSha256) ||
            expectedSourceSha256.Length != 64)
            throw new ArgumentException("Укажите SHA256 из read-only preview.");
        using var conn = Open();
        if (!string.Equals(conn.DataSource, expectedServer, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(conn.Database, expectedDatabase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQL Server/каталог отличается от подтверждённого preview.");
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn, tx);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        RequireFresh(conn, tx);
        var rows = ReadHistory(conn, tx);
        var report = MetalLegacyReconciliation.Preview(rows);
        if (!report.CanImport || !report.SourceSha256.Equals(expectedSourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Источник изменился или имеет несверенные ошибки: abort.");
        ValidateObjectReferences(conn, tx, rows);

        using (var schema = new SqlCommand(ImportSchema, conn, tx) { CommandTimeout = 60 })
            schema.ExecuteNonQuery();
        if (Count(conn, tx, "gmc_metal_legacy_import") != 0 ||
            Count(conn, tx, "gmc_metal_legacy_events") != 0)
            throw new InvalidOperationException("Миграция уже была начата/проведена.");

        var materials = new Dictionary<(string, string), long>();
        var lots = new Dictionary<long, long>();
        foreach (var row in rows)
        {
            var key = (row.Category, row.Material);
            if (!materials.TryGetValue(key, out var materialId))
                materials[key] = materialId = InsertMaterial(conn, tx, row.Category, row.Material);
            var originalLocal = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Unspecified);
            var utc = new DateTimeOffset(originalLocal, legacyUtcOffset).UtcDateTime;
            if (row.Kind == "Партия")
                lots.Add(row.Id, InsertLot(conn, tx, materialId, row, utc));
            long? lotId = row.Kind == "Партия" ? lots[row.Id] :
                row.LotId is long parent ? lots[parent] : null;
            var movementId = InsertMovement(conn, tx, materialId, lotId, row, utc);
            InsertProvenance(conn, tx, row, movementId,
                row.Kind == "Партия" ? lotId : null, report.SourceSha256);
        }
        using (var receipt = new SqlCommand("""
INSERT dbo.gmc_metal_legacy_import(id,source_sha256,source_server,source_database,
 event_count,source_utc_offset_minutes,employee)
VALUES(1,@hash,@server,@db,@count,@offset,@employee)
""", conn, tx))
        {
            receipt.Parameters.Add("@hash", SqlDbType.Char, 64).Value = report.SourceSha256;
            receipt.Parameters.Add("@server", SqlDbType.NVarChar, 255).Value = conn.DataSource;
            receipt.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = conn.Database;
            receipt.Parameters.Add("@count", SqlDbType.Int).Value = rows.Count;
            receipt.Parameters.Add("@offset", SqlDbType.SmallInt).Value =
                checked((short)legacyUtcOffset.TotalMinutes);
            var name = Environment.UserName;
            receipt.Parameters.Add("@employee", SqlDbType.NVarChar, 64).Value =
                string.IsNullOrWhiteSpace(name) ? "unknown" : name[..Math.Min(64,name.Length)];
            receipt.ExecuteNonQuery();
        }
        if (Count(conn, tx, "gmc_metal_legacy_events") != rows.Count ||
            Count(conn, tx, "gmc_metal_movements") != rows.Count)
            throw new DBConcurrencyException("Не совпали количества событий: rollback.");
        // Full transformed-ledger reconciliation BEFORE COMMIT: any imbalance aborts
        // the entire SERIALIZABLE transaction, including schema and provenance.
        var beforeCommit = ReadbackOnConnection(conn, tx);
        if (beforeCommit is null || beforeCommit.SourceSha256 != report.SourceSha256 ||
            beforeCommit.EventCount != rows.Count)
            throw new DBConcurrencyException("SQL ledger/provenance differs before COMMIT: rollback.");
        SqlServerWorkspace.BumpRevision(conn, tx);
        try
        {
            tx.Commit();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            // COMMIT may have reached SQL Server while the acknowledgement was lost.
            // NEVER retry the INSERTs. Close the old connection and verify a new one.
            tx.Dispose();
            conn.Dispose();
            try
            {
                var found = Readback();
                if (found is not null && found.SourceSha256 == report.SourceSha256 &&
                    found.EventCount == rows.Count)
                    return found with { State = "CONFIRMED_AFTER_LOST_ACK" };
            }
            catch (Exception) { /* inconclusive readback is not a reason to retry */ }
            throw new InvalidOperationException(
                "Результат COMMIT неизвестен; повторный импорт запрещён до ручного readback.", ex);
        }
        var verified = Readback();
        if (verified is null || verified.SourceSha256 != report.SourceSha256 ||
            verified.EventCount != rows.Count)
            throw new InvalidOperationException("COMMIT выполнен, но readback не сошёлся. Без повторной записи.");
        return verified with { State = "CONFIRMED" };
    }

    /// <summary>
    /// Independent SQL readback. Returns null when the receipt is absent. Reconstructs the
    /// complete imported legacy stream and SHA; verifies mapping and all lot foreign keys.
    /// </summary>
    public MetalLegacyImportReceipt? Readback()
    {
        using var conn = Open();
        return ReadbackOnConnection(conn, null);
    }

    private static MetalLegacyImportReceipt? ReadbackOnConnection(SqlConnection conn, SqlTransaction? tx)
    {
        if (!Exists(conn, tx, "gmc_metal_legacy_import")) return null;
        string hash;
        int count, offset;
        using (var cmd = new SqlCommand("""
SELECT source_sha256,event_count,source_utc_offset_minutes
FROM dbo.gmc_metal_legacy_import WHERE id=1
""", conn, tx))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) return null;
            hash = reader.GetString(0).Trim();
            count = reader.GetInt32(1);
            offset = reader.GetInt16(2);
        }
        var reconstructed = new List<MetalLegacyEntry>();
        using (var cmd = new SqlCommand("""
SELECT p.legacy_id, m.category,m.material,e.kind,e.area_milli,e.pieces,e.reservation_milli,
 e.source_place,e.target_place,e.object_id,e.object_name,p.legacy_lot_id,
 e.supplier,e.invoice,e.note,e.employee,p.legacy_created_at,
 p.event_sha256,e.lot_id,
 CASE WHEN e.kind=N'Партия' THEN p.target_lot_id ELSE lp.target_lot_id END,
 p.source_sha256
FROM dbo.gmc_metal_legacy_events p
JOIN dbo.gmc_metal_movements e ON e.id=p.movement_id
JOIN dbo.gmc_metal_materials m ON m.id=e.material_id
LEFT JOIN dbo.gmc_metal_legacy_events lp ON lp.legacy_id=p.legacy_lot_id
ORDER BY p.legacy_id
""", conn, tx) { CommandTimeout = 120 })
        using (var r = cmd.ExecuteReader())
        {
            string S(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            long L(int i) => Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
            long? N(int i) => r.IsDBNull(i) ? null : L(i);
            while (r.Read())
            {
                if (N(18) != N(19))
                    throw new InvalidOperationException("Нарушено соответствие lot_id в миграции.");
                if (!string.Equals(S(20).Trim(), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Неверный source SHA в provenance события.");
                var row = new MetalLegacyEntry(L(0), S(1), S(2), S(3), L(4),
                    checked((int)L(5)), L(6), S(7), S(8), N(9), S(10), N(11),
                    S(12), S(13), S(14), S(15), r.GetDateTime(16));
                if (!MetalLegacyReconciliation.Hash(new[] { row }).Equals(S(17), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Изменён проведённый импортированный event: audit mismatch.");
                reconstructed.Add(row);
            }
        }
        // A lot header in the target is a distinct entity, not merely a copied movement.
        // Verify the entity itself, not just the event-to-lot mapping.
        if (Scalar(conn, tx, """
SELECT COUNT_BIG(*)
FROM dbo.gmc_metal_legacy_events p
JOIN dbo.gmc_metal_movements e ON e.id=p.movement_id
LEFT JOIN dbo.gmc_metal_lots l ON l.id=p.target_lot_id
WHERE e.kind=N'Партия' AND
 (l.id IS NULL OR l.material_id<>e.material_id OR l.supplier<>e.supplier OR
  l.invoice<>e.invoice OR l.area_milli<>e.area_milli OR l.pieces<>e.pieces)
""") != 0)
            throw new InvalidOperationException("SQL партия отличается от исторического заголовка.");
        if (reconstructed.Count != count ||
            !MetalLegacyReconciliation.Hash(reconstructed).Equals(hash, StringComparison.OrdinalIgnoreCase) ||
            !MetalLegacyReconciliation.Preview(reconstructed).CanImport)
            throw new InvalidOperationException("Расхождение provenance / SHA256 / balances: readback FAIL.");
        return new MetalLegacyImportReceipt(hash, count, offset, "VERIFIED_READONLY");
    }
}
