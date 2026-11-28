using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Pilot-only SQL Server warehouse. Never writes to the Python/SQLite store or a production catalog.
/// Every stock mutation uses one SERIALIZABLE transaction and a SQL application lock.
/// </summary>
public sealed partial class SqlServerMetalWarehouse
{
    private readonly string _connectionString;

    public SqlServerMetalWarehouse(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        _connectionString = connectionString;
        EnsureSchema();
    }

    private SqlConnection Open()
    {
        SqlServerWorkspace.RequirePilotDatabase(_connectionString);
        var conn = SqlServerDatabase.Open(_connectionString);
        // Validate the actually selected DB, not just a potentially misleading connection string.
        if (!conn.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
        {
            conn.Dispose();
            throw new InvalidOperationException("Склад металла: производственная база SQL Server заблокирована.");
        }
        return conn;
    }

    private const string Schema = """
IF OBJECT_ID(N'dbo.gmc_metal_materials', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.gmc_metal_materials(
  id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_gmc_metal_materials PRIMARY KEY,
  category NVARCHAR(32) NOT NULL,
  material NVARCHAR(80) NOT NULL,
  CONSTRAINT UX_gmc_metal_material UNIQUE(category,material)
 );
END;
IF OBJECT_ID(N'dbo.gmc_metal_lots', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.gmc_metal_lots(
  id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_gmc_metal_lots PRIMARY KEY,
  material_id BIGINT NOT NULL REFERENCES dbo.gmc_metal_materials(id),
  supplier NVARCHAR(255) NOT NULL,
  invoice NVARCHAR(120) NOT NULL,
  area_milli BIGINT NOT NULL CHECK (area_milli>0),
  pieces INT NOT NULL CHECK (pieces>0),
  created_utc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UX_gmc_metal_lot UNIQUE(material_id,supplier,invoice)
 );
END;
IF OBJECT_ID(N'dbo.gmc_metal_movements', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.gmc_metal_movements(
  id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_gmc_metal_movements PRIMARY KEY,
  operation_id UNIQUEIDENTIFIER NOT NULL,
  material_id BIGINT NOT NULL REFERENCES dbo.gmc_metal_materials(id),
  kind NVARCHAR(24) NOT NULL,
  area_milli BIGINT NOT NULL,
  pieces INT NOT NULL DEFAULT 0 CHECK (pieces BETWEEN 0 AND 10000000),
  reservation_milli BIGINT NOT NULL DEFAULT 0,
  source_place NVARCHAR(80) NOT NULL DEFAULT N'',
  target_place NVARCHAR(80) NOT NULL DEFAULT N'',
  object_id BIGINT NULL,
  object_name NVARCHAR(255) NOT NULL DEFAULT N'',
  lot_id BIGINT NULL REFERENCES dbo.gmc_metal_lots(id),
  supplier NVARCHAR(255) NOT NULL DEFAULT N'',
  invoice NVARCHAR(120) NOT NULL DEFAULT N'',
  note NVARCHAR(MAX) NOT NULL DEFAULT N'',
  employee NVARCHAR(64) NOT NULL,
  created_utc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
  CONSTRAINT UX_gmc_metal_movement_operation UNIQUE(operation_id)
 );
END;
IF OBJECT_ID(N'dbo.gmc_metal_opening', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.gmc_metal_opening(
  id TINYINT NOT NULL CONSTRAINT PK_gmc_metal_opening PRIMARY KEY CHECK(id=1),
  source_file NVARCHAR(500) NOT NULL,
  source_sha256 CHAR(64) NOT NULL,
  imported_utc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
  employee NVARCHAR(64) NOT NULL
 );
END;
IF OBJECT_ID(N'dbo.gmc_metal_register', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.gmc_metal_register(
  id TINYINT NOT NULL CONSTRAINT PK_gmc_metal_register PRIMARY KEY CHECK(id=1),
  workbook VARBINARY(MAX) NOT NULL,
  revision BIGINT NOT NULL DEFAULT(1),
  employee NVARCHAR(64) NOT NULL,
  saved_utc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
 );
END;
IF OBJECT_ID(N'dbo.gmc_metal_register_history', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.gmc_metal_register_history(
  id BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_gmc_metal_register_history PRIMARY KEY,
  register_revision BIGINT NOT NULL,
  sha256 CHAR(64) NOT NULL,
  employee NVARCHAR(64) NOT NULL,
  saved_utc DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME()
 );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.gmc_metal_movements') AND name=N'IX_gmc_metal_movements_material')
 CREATE INDEX IX_gmc_metal_movements_material ON dbo.gmc_metal_movements(material_id,id) INCLUDE(kind,area_milli,pieces,reservation_milli,source_place,target_place,object_id,lot_id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.gmc_metal_movements') AND name=N'IX_gmc_metal_movements_object')
 CREATE INDEX IX_gmc_metal_movements_object ON dbo.gmc_metal_movements(object_id,material_id,id) INCLUDE(kind,area_milli,reservation_milli);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.gmc_metal_movements') AND name=N'IX_gmc_metal_movements_recent')
 CREATE INDEX IX_gmc_metal_movements_recent ON dbo.gmc_metal_movements(created_utc DESC,id DESC) INCLUDE(material_id,kind,area_milli);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.gmc_metal_lots') AND name=N'IX_gmc_metal_lots_date')
 CREATE INDEX IX_gmc_metal_lots_date ON dbo.gmc_metal_lots(created_utc DESC);
""";

    public void EnsureSchema()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn, tx, "GMC.Metal.Schema");
        using var command = new SqlCommand(Schema, conn, tx) { CommandTimeout = 60 };
        command.ExecuteNonQuery();
        tx.Commit();
    }

    private static void Lock(SqlConnection conn, SqlTransaction tx, string resource = "GMC.Metal.Stock")
    {
        using var command = new SqlCommand(
            "DECLARE @code INT; EXEC @code=sys.sp_getapplock " +
            "@Resource=@key,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000; SELECT @code", conn, tx);
        command.Parameters.Add("@key", SqlDbType.NVarChar, 255).Value = resource;
        if (SqlInteger.RequireInt32(command.ExecuteScalar()) < 0)
            throw new TimeoutException("Не удалось захватить складской SQL-замок; повторите операцию.");
    }

    private static long FindMaterial(SqlConnection conn, SqlTransaction? tx,
        string category, string material, bool create = false)
    {
        MetalWarehouseRules.CheckMaterial(category, material);
        using var cmd = new SqlCommand(
            "SELECT id FROM dbo.gmc_metal_materials " +
            (create ? "WITH (UPDLOCK,HOLDLOCK) " : "") +
            "WHERE category=@cat AND material=@name", conn, tx);
        cmd.Parameters.Add("@cat", SqlDbType.NVarChar, 32).Value = category;
        cmd.Parameters.Add("@name", SqlDbType.NVarChar, 80).Value = material;
        var found = cmd.ExecuteScalar();
        if (found is not null and not DBNull)
            return Convert.ToInt64(found, CultureInfo.InvariantCulture);
        if (!create) throw new ArgumentException("Материал не найден в складском журнале.");
        using var insert = new SqlCommand(
            "INSERT dbo.gmc_metal_materials(category,material) OUTPUT INSERTED.id VALUES (@cat,@name)", conn, tx);
        insert.Parameters.Add("@cat", SqlDbType.NVarChar, 32).Value = category;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 80).Value = material;
        return SqlInteger.RequireInt64(insert.ExecuteScalar());
    }

    private static string OperatorName()
    {
        var text = Environment.UserName.Trim();
        return text.Length > 64 ? text[..64] : text.Length > 0 ? text : "unknown";
    }

    private static long Append(SqlConnection conn, SqlTransaction tx, long materialId,
        string kind, long area, int pieces = 0, long reservation = 0,
        string source = "", string destination = "", long? objectId = null,
        string objectName = "", string note = "", long? lotId = null,
        string supplier = "", string invoice = "", Guid? operationId = null)
    {
        using var cmd = new SqlCommand("""
INSERT dbo.gmc_metal_movements
 (operation_id,material_id,kind,area_milli,pieces,reservation_milli,source_place,target_place,
 object_id,object_name,lot_id,supplier,invoice,note,employee)
OUTPUT INSERTED.id
VALUES (@operation,@material,@kind,@area,@pieces,@reserve,@from,@to,@obj,@objectName,
 @lot,@supplier,@invoice,@note,@user)
""", conn, tx);
        cmd.Parameters.Add("@operation", SqlDbType.UniqueIdentifier).Value = operationId ?? Guid.NewGuid();
        cmd.Parameters.Add("@material", SqlDbType.BigInt).Value = materialId;
        cmd.Parameters.Add("@kind", SqlDbType.NVarChar, 24).Value = kind;
        cmd.Parameters.Add("@area", SqlDbType.BigInt).Value = area;
        cmd.Parameters.Add("@pieces", SqlDbType.Int).Value = pieces;
        cmd.Parameters.Add("@reserve", SqlDbType.BigInt).Value = reservation;
        cmd.Parameters.Add("@from", SqlDbType.NVarChar, 80).Value = source;
        cmd.Parameters.Add("@to", SqlDbType.NVarChar, 80).Value = destination;
        cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId is long objectKey ? objectKey : DBNull.Value;
        cmd.Parameters.Add("@objectName", SqlDbType.NVarChar, 255).Value = objectName;
        cmd.Parameters.Add("@lot", SqlDbType.BigInt).Value = lotId is long lotKey ? lotKey : DBNull.Value;
        cmd.Parameters.Add("@supplier", SqlDbType.NVarChar, 255).Value = supplier;
        cmd.Parameters.Add("@invoice", SqlDbType.NVarChar, 120).Value = invoice;
        cmd.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = note;
        cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = OperatorName();
        return SqlInteger.RequireInt64(cmd.ExecuteScalar());
    }

    private static void Finish(SqlConnection conn, SqlTransaction tx)
    {
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
    }

    public bool IsInitialized()
    {
        using var conn = Open();
        using var cmd = new SqlCommand("SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.gmc_metal_opening) " +
            "OR EXISTS(SELECT 1 FROM dbo.meta WHERE [key]=N\'metal_warehouse_initialized\') " +
            "THEN CONVERT(BIGINT,1) ELSE CONVERT(BIGINT,0) END", conn);
        return SqlFlag.Require(cmd.ExecuteScalar());
    }

    public string Revision()
    {
        using var conn = Open();
        return SqlServerDatabase.Revision(conn);
    }

    public IReadOnlyList<(long Id, string Name)> Objects()
    {
        using var conn = Open();
        using var cmd = new SqlCommand("SELECT id,name FROM dbo.objects ORDER BY name,id", conn);
        using var reader = cmd.ExecuteReader();
        var items = new List<(long, string)>();
        while (reader.Read()) items.Add((Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), reader.GetString(1)));
        return items;
    }

    public IReadOnlyList<(string Category, string Material)> Materials()
    {
        using var conn = Open();
        using var cmd = new SqlCommand("SELECT category,material FROM dbo.gmc_metal_materials ORDER BY category,material", conn);
        using var reader = cmd.ExecuteReader();
        var items = new List<(string, string)>();
        while (reader.Read()) items.Add((reader.GetString(0), reader.GetString(1)));
        return items;
    }
}
