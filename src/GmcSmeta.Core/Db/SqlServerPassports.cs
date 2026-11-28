using System.Data;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed class PassportExportCommitUncertainException(string message, Exception inner)
    : InvalidOperationException(message, inner) { }

public sealed class PassportWriteCommitUncertainException(string message, Exception inner)
    : InvalidOperationException(message, inner) { }

/// <summary>
/// Pilot-only ventilation passports; unrelated to financial tables and SQLite archives.
/// Per-passport optimistic revisions, atomic multi-passport transactions and equipment catalog.
/// </summary>
public sealed class SqlServerPassports
{
    private readonly string _connectionString;

    public SqlServerPassports(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        _connectionString = connectionString;
        EnsureSchema();
    }

    private SqlConnection Open()
    {
        var conn = SqlServerDatabase.Open(_connectionString);
        if (!conn.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
        {
            conn.Dispose();
            throw new InvalidOperationException("Запись паспортов разрешена только в GmcSmetaPilot_*.");
        }
        return conn;
    }

    private static void Lock(SqlConnection conn, SqlTransaction tx, string resource)
    {
        using var cmd = new SqlCommand(
            "DECLARE @result int; EXEC @result=sys.sp_getapplock " +
            "@Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=15000; SELECT @result;",
            conn, tx);
        cmd.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
        if (checked((int)SqlInteger.RequireInt64(cmd.ExecuteScalar())) < 0)
            throw new TimeoutException("Не удалось заблокировать редактирование паспортов.");
    }

    private const string Schema = """
IF OBJECT_ID(N'dbo.gmc_passports',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_passports(
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_gmc_passports PRIMARY KEY,
        object_id BIGINT NOT NULL,
        designation NVARCHAR(160) NOT NULL,
        purpose NVARCHAR(500) NOT NULL,
        payload_json NVARCHAR(MAX) NOT NULL,
        revision BIGINT NOT NULL CONSTRAINT DF_gmc_passports_revision DEFAULT(1),
        created_utc DATETIME2(7) NOT NULL CONSTRAINT DF_gmc_passports_created DEFAULT SYSUTCDATETIME(),
        updated_utc DATETIME2(7) NOT NULL CONSTRAINT DF_gmc_passports_updated DEFAULT SYSUTCDATETIME()
    );
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'IX_gmc_passports_object' AND object_id=OBJECT_ID(N'dbo.gmc_passports'))
    CREATE INDEX IX_gmc_passports_object ON dbo.gmc_passports(object_id,updated_utc DESC);
IF OBJECT_ID(N'dbo.gmc_passport_equipment_catalog',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_passport_equipment_catalog(
        catalog_key NVARCHAR(450) NOT NULL CONSTRAINT PK_gmc_passport_equipment_catalog PRIMARY KEY,
        project_model NVARCHAR(2000) NOT NULL,
        actual_model NVARCHAR(2000) NOT NULL,
        payload_json NVARCHAR(MAX) NOT NULL,
        updated_utc DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF OBJECT_ID(N'dbo.gmc_passport_history',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_passport_history(
        id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        passport_id UNIQUEIDENTIFIER NOT NULL,
        object_id BIGINT NOT NULL,
        revision BIGINT NOT NULL,
        payload_json NVARCHAR(MAX) NOT NULL,
        action NVARCHAR(20) NOT NULL,
        saved_utc DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'IX_gmc_passport_history_object' AND object_id=OBJECT_ID(N'dbo.gmc_passport_history'))
    CREATE INDEX IX_gmc_passport_history_object ON dbo.gmc_passport_history(object_id,passport_id,revision);
IF OBJECT_ID(N'dbo.gmc_passport_exports',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_passport_exports(
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_gmc_passport_exports PRIMARY KEY,
        passport_id UNIQUEIDENTIFIER NOT NULL,
        object_id BIGINT NOT NULL,
        relative_path NVARCHAR(450) NOT NULL CONSTRAINT UX_gmc_passport_exports_path UNIQUE,
        sha256 CHAR(64) NOT NULL,
        draft BIT NOT NULL,
        page_count INT NOT NULL,
        created_utc DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name=N'IX_gmc_passport_exports_object' AND object_id=OBJECT_ID(N'dbo.gmc_passport_exports'))
    CREATE INDEX IX_gmc_passport_exports_object ON dbo.gmc_passport_exports(object_id,passport_id,created_utc DESC);
""";

    public void EnsureSchema()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Lock(conn, tx, "GMC.Passports.Schema");
        using var cmd = new SqlCommand(Schema, conn, tx) { CommandTimeout = 40 };
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private static void CheckObject(SqlConnection conn, SqlTransaction tx, long id)
    {
        using var cmd = new SqlCommand("SELECT COUNT_BIG(1) FROM dbo.objects WITH (HOLDLOCK) WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        if (SqlInteger.RequireInt64(cmd.ExecuteScalar()) != 1)
            throw new InvalidOperationException("Объект не найден в этой тестовой базе.");
    }

    public IReadOnlyList<VentPassport> List(long objectId)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(
            "SELECT payload_json,revision FROM dbo.gmc_passports WHERE object_id=@object ORDER BY created_utc,id",
            conn);
        cmd.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
        var list = new List<VentPassport>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var p = JsonSerializer.Deserialize<VentPassport>(reader.GetString(0), VentPassports.Json)
                ?? throw new InvalidDataException("Повреждён JSON паспорта.");
            p.Revision = SqlInteger.ReadInt64(reader, 1);
            if (p.ObjectId != objectId) throw new InvalidDataException("Паспорт в чужом объекте.");
            list.Add(p);
        }
        return list;
    }

    public VentPassport Get(long objectId, Guid passportId)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(
            "SELECT payload_json,revision FROM dbo.gmc_passports WHERE object_id=@obj AND id=@id",
            conn);
        cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = passportId;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Паспорт не найден в этом объекте.");
        var p = JsonSerializer.Deserialize<VentPassport>(reader.GetString(0), VentPassports.Json)!;
        p.Revision = SqlInteger.ReadInt64(reader, 1);
        if (p.ObjectId != objectId) throw new InvalidDataException("Неверная область паспорта.");
        return p;
    }

    public IReadOnlyList<VentEquipmentTemplate> Catalogue()
    {
        using var conn = Open();
        using var cmd = new SqlCommand(
            "SELECT payload_json FROM dbo.gmc_passport_equipment_catalog ORDER BY catalog_key", conn);
        using var reader = cmd.ExecuteReader();
        var result = new List<VentEquipmentTemplate>();
        while (reader.Read())
            result.Add(JsonSerializer.Deserialize<VentEquipmentTemplate>(reader.GetString(0), VentPassports.Json)!);
        return result;
    }

    /// <summary>Atomic set of create/update operations; no partial writes on conflict or SQL failure.</summary>
    public void SaveBatch(long objectId, IReadOnlyList<VentPassport> batch)
    {
        if (objectId <= 0) throw new ArgumentException("Неверный объект.");
        if (batch.Select(p => p.Id).Distinct().Count() != batch.Count)
            throw new ArgumentException("Повторяющиеся ID паспортов.");
        foreach (var p in batch)
        {
            VentPassports.Validate(p);
            if (p.ObjectId != objectId)
                throw new InvalidOperationException("Нельзя сохранять паспорт другого объекта.");
        }
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn, tx, "GMC.Passports.Object." + objectId);
        CheckObject(conn, tx, objectId);
        var updated = new List<(VentPassport Passport, long Revision)>();
        foreach (var p in batch)
        {
            long? revision = null;
            string? previousJson = null;
            using (var check = new SqlCommand(
                "SELECT revision,payload_json FROM dbo.gmc_passports WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE id=@id AND object_id=@obj", conn, tx))
            {
                check.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = p.Id;
                check.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
                using var reader = check.ExecuteReader();
                if (reader.Read())
                {
                    revision = SqlInteger.ReadInt64(reader, 0);
                    previousJson = reader.GetString(1);
                }
            }
            if (revision is null)
            {
                // Explicitly detect ID collisions across object scopes, never re-home them.
                using var collision = new SqlCommand("SELECT COUNT_BIG(1) FROM dbo.gmc_passports WHERE id=@id", conn, tx);
                collision.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = p.Id;
                if (SqlInteger.RequireInt64(collision.ExecuteScalar()) != 0 || p.Revision != 0)
                    throw new DBConcurrencyException("Паспорт удалён либо принадлежит другому объекту.");
            }
            else if (revision.Value != p.Revision)
                throw new DBConcurrencyException(
                    $"Паспорт «{p.System}» изменён другим сотрудником. Перечитайте объект.");

            var json = JsonSerializer.Serialize(p, VentPassports.Json);
            // Revision is excluded from JSON. Unchanged navigation/close
            // must not create a new history row or overwrite the shared catalogue.
            if (revision.HasValue && string.Equals(previousJson, json, StringComparison.Ordinal))
                continue;
            var nextRevision = (revision ?? 0) + 1;
            using (var cmd = new SqlCommand(revision is null
                ? "INSERT dbo.gmc_passports(id,object_id,designation,purpose,payload_json,revision) " +
                  "VALUES(@id,@obj,@name,@purpose,@json,@newrev)"
                : "UPDATE dbo.gmc_passports SET designation=@name,purpose=@purpose," +
                  "payload_json=@json,revision=@newrev,updated_utc=SYSUTCDATETIME() " +
                  "WHERE id=@id AND object_id=@obj AND revision=@prev", conn, tx))
            {
                cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = p.Id;
                cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
                cmd.Parameters.Add("@name", SqlDbType.NVarChar, 160).Value = p.System;
                cmd.Parameters.Add("@purpose", SqlDbType.NVarChar, 500).Value = p.Purpose;
                cmd.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = json;
                cmd.Parameters.Add("@newrev", SqlDbType.BigInt).Value = nextRevision;
                if (revision.HasValue) cmd.Parameters.Add("@prev", SqlDbType.BigInt).Value = revision.Value;
                if (cmd.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Версия паспорта изменилась.");
            }
            using (var history = new SqlCommand(
                "INSERT dbo.gmc_passport_history(passport_id,object_id,revision,payload_json,action) " +
                "VALUES(@id,@obj,@rev,@json,@action)", conn, tx))
            {
                history.Parameters.AddWithValue("@id", p.Id);
                history.Parameters.AddWithValue("@obj", objectId);
                history.Parameters.AddWithValue("@rev", nextRevision);
                history.Parameters.AddWithValue("@json", json);
                history.Parameters.AddWithValue("@action", revision is null ? "create" : "update");
                history.ExecuteNonQuery();
            }
            var key = VentPassports.CatalogueKey(p);
            if (key.Length is > 0 and <= 450)
            {
                var template = VentPassports.MakeTemplate(p);
                using var catalog = new SqlCommand(
                    "UPDATE dbo.gmc_passport_equipment_catalog SET " +
                    "project_model=@project,actual_model=@actual,payload_json=@json,updated_utc=SYSUTCDATETIME() " +
                    "WHERE catalog_key=@key; " +
                    "IF @@ROWCOUNT=0 INSERT dbo.gmc_passport_equipment_catalog" +
                    "(catalog_key,project_model,actual_model,payload_json) " +
                    "VALUES(@key,@project,@actual,@json)", conn, tx);
                catalog.Parameters.Add("@key", SqlDbType.NVarChar, 450).Value = key;
                catalog.Parameters.Add("@project", SqlDbType.NVarChar, 2000).Value = template.ProjectModel;
                catalog.Parameters.Add("@actual", SqlDbType.NVarChar, 2000).Value = template.ActualModel;
                catalog.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value =
                    JsonSerializer.Serialize(template, VentPassports.Json);
                catalog.ExecuteNonQuery();
            }
            updated.Add((p, nextRevision));
        }
        CommitWrite(tx);
        foreach (var (p, newRevision) in updated) p.Revision = newRevision;
    }

    public void Save(VentPassport passport) => SaveBatch(passport.ObjectId, new[] { passport });

    public void Delete(long objectId, Guid passportId, long expectedRevision)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn, tx, "GMC.Passports.Object." + objectId);
        CheckObject(conn, tx, objectId);
        using (var cmd = new SqlCommand(
            "DELETE FROM dbo.gmc_passports WHERE id=@id AND object_id=@obj AND revision=@rev", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = passportId;
            cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
            cmd.Parameters.Add("@rev", SqlDbType.BigInt).Value = expectedRevision;
            if (cmd.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Паспорт удалён или изменён другим сотрудником.");
        }
        using (var history = new SqlCommand(
            "INSERT dbo.gmc_passport_history(passport_id,object_id,revision,payload_json,action) " +
            "VALUES(@id,@obj,@rev,N'{}',N'delete')", conn, tx))
        {
            history.Parameters.AddWithValue("@id", passportId);
            history.Parameters.AddWithValue("@obj", objectId);
            history.Parameters.AddWithValue("@rev", expectedRevision + 1);
            history.ExecuteNonQuery();
        }
        CommitWrite(tx);
    }

    private static void CommitWrite(SqlTransaction tx)
    {
        try { tx.Commit(); }
        catch (Exception ex)
        {
            throw new PassportWriteCommitUncertainException(
                "Исход записи паспорта неизвестен. Проверьте сохранённые данные перед повтором.", ex);
        }
    }

    public sealed record ExportInfo(Guid PassportId, string RelativePath, string Sha256,
        bool Draft, int Pages);

    public void RegisterExport(long objectId, ExportInfo export, long expectedRevision)
    {
        if (!export.RelativePath.StartsWith("passports/", StringComparison.Ordinal) ||
            export.RelativePath.Contains("..") || export.RelativePath.Contains('\\') ||
            export.RelativePath.Contains(':') || export.RelativePath.StartsWith('/'))
            throw new InvalidDataException("Недопустимый путь экспорта.");
        if (export.Sha256.Length != 64 || !export.Sha256.All(Uri.IsHexDigit) || export.Pages <= 0)
            throw new ArgumentException("Некорректные контрольные данные PDF.");
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn, tx, "GMC.Passports.Object." + objectId);
        using (var check = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.gmc_passports WITH (UPDLOCK,HOLDLOCK) WHERE id=@id AND object_id=@obj AND revision=@rev", conn, tx))
        {
            check.Parameters.AddWithValue("@id", export.PassportId);
            check.Parameters.AddWithValue("@obj", objectId);
            check.Parameters.Add("@rev", SqlDbType.BigInt).Value = expectedRevision;
            if (SqlInteger.RequireInt64(check.ExecuteScalar()) != 1)
                throw new DBConcurrencyException("Паспорт изменён или удалён. Обновите данные перед экспортом.");
        }
        using (var cmd = new SqlCommand(
            "INSERT dbo.gmc_passport_exports(id,passport_id,object_id,relative_path,sha256,draft,page_count) " +
            "VALUES(@id,@pid,@obj,@path,@hash,@draft,@pages)", conn, tx))
        {
            cmd.Parameters.AddWithValue("@id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("@pid", export.PassportId);
            cmd.Parameters.AddWithValue("@obj", objectId);
            cmd.Parameters.AddWithValue("@path", export.RelativePath);
            cmd.Parameters.AddWithValue("@hash", export.Sha256);
            cmd.Parameters.AddWithValue("@draft", export.Draft);
            cmd.Parameters.AddWithValue("@pages", export.Pages);
            if (cmd.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("PDF не зарегистрирован.");
        }
        try { tx.Commit(); }
        catch (Exception ex)
        {
            throw new PassportExportCommitUncertainException(
                "Исход регистрации PDF неизвестен. Файл сохранён; проверьте историю перед повтором.", ex);
        }
    }

    /// <summary>The archive root is global; deleted passports retain registered PDFs.</summary>
    public bool HasAnyExports()
    {
        using var conn = Open();
        using var cmd = new SqlCommand(
            "SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.gmc_passport_exports) THEN 1 ELSE 0 END", conn);
        return checked((int)SqlInteger.RequireInt64(cmd.ExecuteScalar())) == 1;
    }

    public IReadOnlyList<ExportInfo> Exports(long objectId, Guid passportId)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(
            "SELECT passport_id,relative_path,sha256,draft,page_count FROM dbo.gmc_passport_exports " +
            "WHERE object_id=@obj AND passport_id=@pid ORDER BY created_utc DESC", conn);
        cmd.Parameters.AddWithValue("@obj", objectId);
        cmd.Parameters.AddWithValue("@pid", passportId);
        var list = new List<ExportInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(new ExportInfo(reader.GetGuid(0), reader.GetString(1),
            reader.GetString(2).TrimEnd(), SqlFlag.Read(reader, 3), SqlInteger.ReadInt32(reader, 4)));
        return list;
    }
}
