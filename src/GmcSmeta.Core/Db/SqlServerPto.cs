using System.Data;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed class PtoCommitUncertainException(string message, Exception inner)
    : InvalidOperationException(message, inner) { }

/// <summary>
/// Isolated pilot-only SQL Server PTO store. Never writes the estimate or contract tables.
/// Each package has its own revision; an entire change and its history is one transaction.
/// </summary>
public sealed class SqlServerPto
{
    private readonly string _connectionString;
    public SqlServerPto(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        _connectionString=connectionString;
        EnsureSchema();
    }
    private SqlConnection Open(CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var conn=token.CanBeCanceled
            ?SqlServerDatabase.OpenAsync(_connectionString,token).GetAwaiter().GetResult()
            :SqlServerDatabase.Open(_connectionString);
        if(!conn.Database.StartsWith("GmcSmetaPilot_",StringComparison.OrdinalIgnoreCase))
        {
            conn.Dispose();
            throw new InvalidOperationException("ПТО: только фактическая тестовая SQL-база GmcSmetaPilot_*.");
        }
        return conn;
    }
    private static void Lock(SqlConnection conn,SqlTransaction tx,string resource)
    {
        using var cmd=new SqlCommand(
            "DECLARE @v int; EXEC @v=sys.sp_getapplock @Resource=@res,@LockMode='Exclusive',"+
            "@LockOwner='Transaction',@LockTimeout=15000; SELECT @v;",conn,tx);
        cmd.Parameters.Add("@res",SqlDbType.NVarChar,255).Value=resource;
        if(checked((int)SqlInteger.RequireInt64(cmd.ExecuteScalar()))<0)
            throw new TimeoutException("ПТО: не удалось получить блокировку SQL Server.");
    }

    private const string Schema = """
IF OBJECT_ID(N'dbo.gmc_pto_packages',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_pto_packages(
        id uniqueidentifier NOT NULL CONSTRAINT PK_gmc_pto_packages PRIMARY KEY,
        object_id bigint NOT NULL,
        contract_id bigint NULL,
        act_number_key nvarchar(256) NOT NULL DEFAULT N'',
        status nvarchar(32) NOT NULL,
        payload_json nvarchar(max) NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        updated_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.gmc_pto_packages') AND name='UX_gmc_pto_number')
    CREATE UNIQUE INDEX UX_gmc_pto_number ON dbo.gmc_pto_packages(object_id,act_number_key)
    WHERE act_number_key <> N'';
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.gmc_pto_packages') AND name='IX_gmc_pto_object')
    CREATE INDEX IX_gmc_pto_object ON dbo.gmc_pto_packages(object_id,updated_utc DESC);
IF OBJECT_ID(N'dbo.gmc_pto_history',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_pto_history(
        id bigint IDENTITY(1,1) NOT NULL PRIMARY KEY,
        package_id uniqueidentifier NOT NULL,
        object_id bigint NOT NULL,
        revision bigint NOT NULL,
        action nvarchar(32) NOT NULL,
        payload_json nvarchar(max) NOT NULL,
        created_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.gmc_pto_history') AND name='IX_gmc_pto_history_package')
    CREATE INDEX IX_gmc_pto_history_package ON dbo.gmc_pto_history(package_id,revision DESC);
IF OBJECT_ID(N'dbo.gmc_pto_certificates',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_pto_certificates(
        id uniqueidentifier NOT NULL PRIMARY KEY,
        sha256 char(64) NOT NULL UNIQUE,
        payload_json nvarchar(max) NOT NULL,
        revision bigint NOT NULL DEFAULT 1,
        updated_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF OBJECT_ID(N'dbo.gmc_pto_object_profiles',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_pto_object_profiles(
        object_id bigint NOT NULL PRIMARY KEY,
        payload_json nvarchar(max) NOT NULL,
        revision bigint NOT NULL DEFAULT 1
    );
END;
IF OBJECT_ID(N'dbo.gmc_pto_releases',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_pto_releases(
        id uniqueidentifier NOT NULL PRIMARY KEY,
        package_id uniqueidentifier NOT NULL,
        object_id bigint NOT NULL,
        relative_path nvarchar(600) NOT NULL UNIQUE,
        manifest_sha256 char(64) NOT NULL,
        created_utc datetime2 NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.gmc_pto_releases') AND name='IX_gmc_pto_release_package')
    CREATE INDEX IX_gmc_pto_release_package ON dbo.gmc_pto_releases(package_id,created_utc DESC);
""";

    internal static void CommitPreservingFiles(SqlTransaction tx)
    {
        try { tx.Commit(); }
        catch (Exception ex)
        {
            throw new PtoCommitUncertainException(
                "Исход COMMIT ПТО неизвестен. Файлы сохранены; проверьте SQL и архив перед повтором.", ex);
        }
    }

    public void EnsureSchema()
    {
        using var conn=Open();
        using var tx=conn.BeginTransaction();
        Lock(conn,tx,"GMC.PTO.Schema");
        using var cmd=new SqlCommand(Schema,conn,tx){CommandTimeout=45};
        cmd.ExecuteNonQuery();tx.Commit();
    }

    private static void CheckObject(SqlConnection conn,SqlTransaction tx,long objectId)
    {
        using var cmd=new SqlCommand("SELECT COUNT_BIG(1) FROM dbo.objects WITH (HOLDLOCK) WHERE id=@id",conn,tx);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=objectId;
        if(SqlInteger.RequireInt64(cmd.ExecuteScalar())!=1)
            throw new ArgumentException("Объект удалён или отсутствует в SQL Server.");
    }

    public IReadOnlyList<PtoPackage> Packages(long? objectId=null)=>
        Packages(objectId,null);

    public IReadOnlyList<PtoPackage> Packages(long? objectId,long? contractId,
        CancellationToken token=default)
    {
        if(contractId.HasValue && !objectId.HasValue)
            throw new ArgumentException("Для договора требуется объект.");
        token.ThrowIfCancellationRequested();
        using var conn=Open(token);
        // Only fetch payloads in the requested scope. The object-only and
        // unscoped callers keep their original result sets and ordering.
        using var cmd=new SqlCommand(
            "SELECT payload_json,revision FROM dbo.gmc_pto_packages "+
            (objectId.HasValue?"WHERE object_id=@obj ":"")+
            (contractId.HasValue?"AND contract_id=@contract ":"")+
            "ORDER BY updated_utc DESC,id",conn);
        if(objectId.HasValue)cmd.Parameters.Add("@obj",SqlDbType.BigInt).Value=objectId.Value;
        if(contractId.HasValue)cmd.Parameters.Add("@contract",SqlDbType.BigInt).Value=contractId.Value;
        var result=new List<PtoPackage>();
        using var registration=SqlReadCancellation.Register(cmd,token);
        using var reader=SqlReadCancellation.ExecuteReader(cmd,token);
        while(reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var p=JsonSerializer.Deserialize<PtoPackage>(reader.GetString(0),PtoRules.Json)
                ?? throw new InvalidDataException("Повреждён JSON АОСР.");
            p.Revision=SqlInteger.ReadInt64(reader, 1);
            if(objectId.HasValue && p.ObjectId!=objectId ||
                contractId.HasValue && p.ContractId!=contractId)
                throw new InvalidDataException("Неверная область документа.");
            result.Add(p);
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    public PtoPackage Get(Guid id,long? objectId=null)
    {
        using var conn=Open();
        using var cmd=new SqlCommand(
            "SELECT payload_json,revision,object_id FROM dbo.gmc_pto_packages WHERE id=@id "+
            "AND (@obj IS NULL OR object_id=@obj)",conn);
        cmd.Parameters.Add("@id",SqlDbType.UniqueIdentifier).Value=id;
        cmd.Parameters.Add("@obj",SqlDbType.BigInt).Value=objectId.HasValue?objectId.Value:DBNull.Value;
        using var reader=cmd.ExecuteReader();
        if(!reader.Read())throw new KeyNotFoundException("Комплект ПТО не найден.");
        var p=JsonSerializer.Deserialize<PtoPackage>(reader.GetString(0),PtoRules.Json)!;
        if(p.ObjectId!=SqlInteger.ReadInt64(reader, 2))throw new InvalidDataException("Повреждена привязка к объекту.");
        p.Revision=SqlInteger.ReadInt64(reader, 1);
        return p;
    }

    public void Save(PtoPackage p)
    {
        PtoRules.ValidateShape(p);
        if(p.Status is "Готов" or "Подписан")
        {
            var issues=PtoRules.Check(p);
            if(issues.Errors.Count>0 || !p.ManualReview)
                throw new InvalidOperationException(
                    "Статус «Готов/Подписан» требует заполненных реквизитов и ручного подтверждения.\n"+
                    string.Join("\n",issues.Errors));
        }
        var number=PtoRules.NumberKey(PtoRules.Text(p,"number"));
        if(number.Length>256)throw new ArgumentException("Номер акта слишком длинный.");
        var json=JsonSerializer.Serialize(p,PtoRules.Json);
        using var conn=Open();
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn,tx,"GMC.PTO.Object."+p.ObjectId);
        CheckObject(conn,tx,p.ObjectId);
        long? oldRev;
        using(var check=new SqlCommand(
            "SELECT revision FROM dbo.gmc_pto_packages WITH (UPDLOCK,HOLDLOCK) WHERE id=@id AND object_id=@obj",
            conn,tx))
        {
            check.Parameters.Add("@id",SqlDbType.UniqueIdentifier).Value=p.Id;
            check.Parameters.Add("@obj",SqlDbType.BigInt).Value=p.ObjectId;
            var value=check.ExecuteScalar();oldRev=value is null?null:Convert.ToInt64(value);
        }
        if(oldRev is null)
        {
            if(p.Revision!=0)throw new DBConcurrencyException("Комплект ПТО был удалён.");
            using var collision=new SqlCommand("SELECT COUNT_BIG(1) FROM dbo.gmc_pto_packages WHERE id=@id",conn,tx);
            collision.Parameters.Add("@id",SqlDbType.UniqueIdentifier).Value=p.Id;
            if(SqlInteger.RequireInt64(collision.ExecuteScalar())!=0)
                throw new InvalidOperationException("Комплект с таким ID принадлежит другому объекту.");
        }
        else if(oldRev!=p.Revision)
            throw new DBConcurrencyException("АОСР изменён другим сотрудником. Перечитайте документ.");
        if(number.Length>0)
        {
            using var duplicate=new SqlCommand(
                "SELECT COUNT_BIG(1) FROM dbo.gmc_pto_packages WITH (UPDLOCK,HOLDLOCK) "+
                "WHERE object_id=@obj AND act_number_key=@number AND id<>@id",conn,tx);
            duplicate.Parameters.Add("@obj",SqlDbType.BigInt).Value=p.ObjectId;
            duplicate.Parameters.Add("@number",SqlDbType.NVarChar,256).Value=number;
            duplicate.Parameters.Add("@id",SqlDbType.UniqueIdentifier).Value=p.Id;
            if(SqlInteger.RequireInt64(duplicate.ExecuteScalar())>0)
                throw new ArgumentException("Номер акта уже используется на этом объекте.");
        }
        var rev=(oldRev??0)+1;
        using(var cmd=new SqlCommand(oldRev is null
            ? "INSERT dbo.gmc_pto_packages(id,object_id,contract_id,act_number_key,status,payload_json,revision) "+
              "VALUES(@id,@obj,@contract,@number,@status,@json,@rev)"
            : "UPDATE dbo.gmc_pto_packages SET contract_id=@contract,act_number_key=@number,status=@status,"+
              "payload_json=@json,revision=@rev,updated_utc=SYSUTCDATETIME() "+
              "WHERE id=@id AND object_id=@obj AND revision=@old",conn,tx))
        {
            cmd.Parameters.Add("@id",SqlDbType.UniqueIdentifier).Value=p.Id;
            cmd.Parameters.Add("@obj",SqlDbType.BigInt).Value=p.ObjectId;
            cmd.Parameters.Add("@contract",SqlDbType.BigInt).Value=p.ContractId.HasValue?p.ContractId.Value:DBNull.Value;
            cmd.Parameters.Add("@number",SqlDbType.NVarChar,256).Value=number;
            cmd.Parameters.Add("@status",SqlDbType.NVarChar,32).Value=p.Status;
            cmd.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=json;
            cmd.Parameters.Add("@rev",SqlDbType.BigInt).Value=rev;
            if(oldRev.HasValue)cmd.Parameters.Add("@old",SqlDbType.BigInt).Value=oldRev.Value;
            try
            {
                if(cmd.ExecuteNonQuery()!=1)throw new DBConcurrencyException("ПТО: неуспешная запись ревизии.");
            }
            catch(SqlException e) when(e.Number is 2601 or 2627)
            {throw new ArgumentException("Номер АОСР уже используется на объекте.",e);}
        }
        using(var history=new SqlCommand(
            "INSERT dbo.gmc_pto_history(package_id,object_id,revision,action,payload_json) "+
            "VALUES(@id,@obj,@rev,@action,@json)",conn,tx))
        {
            history.Parameters.Add("@id",SqlDbType.UniqueIdentifier).Value=p.Id;
            history.Parameters.Add("@obj",SqlDbType.BigInt).Value=p.ObjectId;
            history.Parameters.Add("@rev",SqlDbType.BigInt).Value=rev;
            history.Parameters.Add("@action",SqlDbType.NVarChar,32).Value=oldRev is null?"create":"update";
            history.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=json;
            history.ExecuteNonQuery();
        }
        CommitPreservingFiles(tx);p.Revision=rev;
    }

    public PtoPackage Copy(Guid sourceId)
    {
        var existing=Get(sourceId);
        // Optimistic unique constraint protects a racing copy from silent duplicate numbers.
        for(var attempt=0;attempt<128;attempt++)
        {
            var all=Packages(existing.ObjectId);
            var next=PtoRules.NextNumber(all,existing);
            var copy=PtoRules.Duplicate(existing,next);
            try {Save(copy);return copy;}
            catch(ArgumentException e) when(e.Message.Contains("Номер",StringComparison.OrdinalIgnoreCase) && attempt<127)
            { Thread.Sleep(Random.Shared.Next(3,30)); /* number taken by another client */ }
        }
        throw new DBConcurrencyException("Не удалось выделить уникальный номер нового АОСР.");
    }

    public void Delete(PtoPackage p)
    {
        using var conn=Open();using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn,tx,"GMC.PTO.Object."+p.ObjectId);
        CheckObject(conn,tx,p.ObjectId);
        using(var cmd=new SqlCommand(
            "DELETE dbo.gmc_pto_packages WHERE id=@id AND object_id=@obj AND revision=@rev",conn,tx))
        {
            cmd.Parameters.Add("@id",SqlDbType.UniqueIdentifier).Value=p.Id;
            cmd.Parameters.Add("@obj",SqlDbType.BigInt).Value=p.ObjectId;
            cmd.Parameters.Add("@rev",SqlDbType.BigInt).Value=p.Revision;
            if(cmd.ExecuteNonQuery()!=1)throw new DBConcurrencyException("Документ изменён другим пользователем.");
        }
        using(var history=new SqlCommand(
            "INSERT dbo.gmc_pto_history(package_id,object_id,revision,action,payload_json) "+
            "VALUES(@id,@obj,@rev,N'delete',@json)",conn,tx))
        {
            history.Parameters.AddWithValue("@id",p.Id);
            history.Parameters.AddWithValue("@obj",p.ObjectId);
            history.Parameters.AddWithValue("@rev",p.Revision+1);
            history.Parameters.AddWithValue("@json",JsonSerializer.Serialize(p,PtoRules.Json));
            history.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public sealed record Version(long Revision,string Action,DateTime SavedUtc,PtoPackage Snapshot);
    public IReadOnlyList<Version> History(Guid id)
    {
        using var conn=Open();
        using var cmd=new SqlCommand(
            "SELECT revision,action,created_utc,payload_json FROM dbo.gmc_pto_history "+
            "WHERE package_id=@id ORDER BY revision DESC",conn);
        cmd.Parameters.AddWithValue("@id",id);
        using var reader=cmd.ExecuteReader();
        var rows=new List<Version>();
        while(reader.Read())
        {
            var snapshot=JsonSerializer.Deserialize<PtoPackage>(reader.GetString(3),PtoRules.Json)!;
            snapshot.Revision=SqlInteger.ReadInt64(reader, 0);
            rows.Add(new Version(SqlInteger.ReadInt64(reader, 0),reader.GetString(1),reader.GetDateTime(2),snapshot));
        }
        return rows;
    }

    public IReadOnlyList<PtoCertificate> Certificates(string query="",CancellationToken token=default)
    {
        using var conn=Open(token);
        using var cmd=new SqlCommand("SELECT payload_json,revision FROM dbo.gmc_pto_certificates ORDER BY updated_utc DESC,id",conn);
        using var registration=SqlReadCancellation.Register(cmd,token);
        using var reader=SqlReadCancellation.ExecuteReader(cmd,token);
        var result=new List<PtoCertificate>();
        while(reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var c=JsonSerializer.Deserialize<PtoCertificate>(reader.GetString(0),PtoRules.Json)
                ??throw new InvalidDataException("Повреждён JSON сертификата.");
            c.Revision=SqlInteger.ReadInt64(reader, 1);
            if(string.IsNullOrWhiteSpace(query) ||
                JsonSerializer.Serialize(c,PtoRules.Json).Contains(query,StringComparison.OrdinalIgnoreCase))
                result.Add(c);
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    public void SaveCertificate(PtoCertificate c)
    {
        if(!PtoRules.QualityKinds.Contains(c.Kind) || c.Pages is <1 || c.Sha256.Length!=64 ||
           !c.Sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Неверные реквизиты документа о качестве.");
        var issued=PtoRules.Date(c.Issued);
        var expires=PtoRules.Date(c.Expires);
        if(issued.HasValue && expires.HasValue && expires<issued)
            throw new ArgumentException("Срок действия раньше даты выдачи.");
        using var conn=Open();using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn,tx,"GMC.PTO.Certificate."+c.Id);
        long? old;
        using(var check=new SqlCommand(
            "SELECT revision FROM dbo.gmc_pto_certificates WITH (UPDLOCK,HOLDLOCK) WHERE id=@id",conn,tx))
        { check.Parameters.AddWithValue("@id",c.Id);var r=check.ExecuteScalar();old=r is null?null:Convert.ToInt64(r); }
        if(old!= (c.Revision==0?null:c.Revision))
            throw new DBConcurrencyException("Сертификат изменён другим пользователем.");
        var next=(old??0)+1;
        using(var cmd=new SqlCommand(old is null
            ? "INSERT dbo.gmc_pto_certificates(id,sha256,payload_json,revision) VALUES(@id,@hash,@json,@rev)"
            : "UPDATE dbo.gmc_pto_certificates SET sha256=@hash,payload_json=@json,revision=@rev,"+
              "updated_utc=SYSUTCDATETIME() WHERE id=@id AND revision=@old",conn,tx))
        {
            cmd.Parameters.AddWithValue("@id",c.Id);
            cmd.Parameters.Add("@hash",SqlDbType.Char,64).Value=c.Sha256.ToLowerInvariant();
            cmd.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=JsonSerializer.Serialize(c,PtoRules.Json);
            cmd.Parameters.AddWithValue("@rev",next);
            if(old.HasValue)cmd.Parameters.AddWithValue("@old",old.Value);
            try{if(cmd.ExecuteNonQuery()!=1)throw new DBConcurrencyException("Сертификат изменён.");}
            catch(SqlException e) when(e.Number is 2601 or 2627)
            {throw new ArgumentException("PDF с таким SHA-256 уже есть в библиотеке.",e);}
        }
        CommitPreservingFiles(tx);c.Revision=next;
    }

    public Dictionary<string,string> ObjectProfile(long id) => ReadObjectProfile(id).Profile;

    public (Dictionary<string,string> Profile,long Revision) ReadObjectProfile(long id)
    {
        using var conn=Open();
        using var cmd=new SqlCommand(
            "SELECT payload_json,revision FROM dbo.gmc_pto_object_profiles WHERE object_id=@id",conn);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=id;
        using var reader=cmd.ExecuteReader();
        if(!reader.Read())return(new(),0);
        return(JsonSerializer.Deserialize<Dictionary<string,string>>(reader.GetString(0),PtoRules.Json)
            ?? throw new InvalidDataException("Повреждена карточка ПТО."),SqlInteger.ReadInt64(reader, 1));
    }

    public void SaveObjectProfile(long id,Dictionary<string,string> profile,long expectedRevision)
    {
        using var conn=Open();using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn,tx,"GMC.PTO.Object."+id);CheckObject(conn,tx,id);
        long revision;
        using(var read=new SqlCommand(
            "SELECT revision FROM dbo.gmc_pto_object_profiles WITH (UPDLOCK,HOLDLOCK) WHERE object_id=@id",conn,tx))
        {
            read.Parameters.Add("@id",SqlDbType.BigInt).Value=id;
            var value=read.ExecuteScalar();
            revision=value is null?0:Convert.ToInt64(value);
        }
        if(revision!=expectedRevision)
            throw new DBConcurrencyException("Карточка ПТО изменена другим сотрудником. Закройте и откройте её заново.");
        using var cmd=new SqlCommand(revision==0
            ? "INSERT dbo.gmc_pto_object_profiles(object_id,payload_json) VALUES(@id,@json)"
            : "UPDATE dbo.gmc_pto_object_profiles SET payload_json=@json,revision=revision+1 WHERE object_id=@id AND revision=@rev",conn,tx);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=id;
        cmd.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=JsonSerializer.Serialize(profile,PtoRules.Json);
        if(revision!=0)cmd.Parameters.Add("@rev",SqlDbType.BigInt).Value=revision;
        if(cmd.ExecuteNonQuery()!=1)throw new DBConcurrencyException("Карточка ПТО изменена.");
        CommitPreservingFiles(tx);
    }

    public void RegisterRelease(PtoReleaseRecord release, long? expectedRevision = null)
    {
        if(!release.RelativePath.StartsWith("exports/",StringComparison.Ordinal) ||
           release.RelativePath.Contains("..") || release.RelativePath.Contains('\\') ||
           release.RelativePath.Contains(':') || release.ManifestSha256.Length!=64 ||
           !release.ManifestSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Недопустимый путь или хеш выпуска.");
        using var conn=Open();using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        using(var verify=new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.gmc_pto_packages WITH (UPDLOCK,HOLDLOCK) WHERE id=@pid AND object_id=@obj AND (@rev IS NULL OR revision=@rev)",conn,tx))
        {
            verify.Parameters.AddWithValue("@pid",release.PackageId);
            verify.Parameters.AddWithValue("@obj",release.ObjectId);
            verify.Parameters.Add("@rev",SqlDbType.BigInt).Value=expectedRevision is long rev?rev:DBNull.Value;
            if(SqlInteger.RequireInt64(verify.ExecuteScalar())!=1)
                throw new InvalidOperationException("Комплект изменён, удалён или принадлежит другому объекту.");
        }
        using(var cmd=new SqlCommand(
            "INSERT dbo.gmc_pto_releases(id,package_id,object_id,relative_path,manifest_sha256) "+
            "VALUES(@id,@pid,@obj,@path,@sha)",conn,tx))
        {
            cmd.Parameters.AddWithValue("@id",release.Id);
            cmd.Parameters.AddWithValue("@pid",release.PackageId);
            cmd.Parameters.AddWithValue("@obj",release.ObjectId);
            cmd.Parameters.AddWithValue("@path",release.RelativePath);
            cmd.Parameters.Add("@sha",SqlDbType.Char,64).Value=release.ManifestSha256;
            if(cmd.ExecuteNonQuery()!=1)throw new DBConcurrencyException("Выпуск не зарегистрирован.");
        }
        CommitPreservingFiles(tx);
    }

    public IReadOnlyList<PtoReleaseRecord> Releases(Guid packageId)
    {
        using var conn=Open();using var cmd=new SqlCommand(
            "SELECT id,package_id,object_id,relative_path,manifest_sha256,created_utc "+
            "FROM dbo.gmc_pto_releases WHERE package_id=@id ORDER BY created_utc DESC",conn);
        cmd.Parameters.AddWithValue("@id",packageId);
        using var reader=cmd.ExecuteReader();
        var result=new List<PtoReleaseRecord>();
        while(reader.Read())
            result.Add(new(reader.GetGuid(0),reader.GetGuid(1),SqlInteger.ReadInt64(reader, 2),
                reader.GetString(3),reader.GetString(4).TrimEnd(),reader.GetDateTime(5)));
        return result;
    }
}
