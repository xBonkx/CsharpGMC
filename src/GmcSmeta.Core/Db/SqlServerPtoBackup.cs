using System.Data;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// SQL-only metadata snapshot including history and releases; restore requires empty pilot PTO tables.
/// </summary>
public sealed class PtoSqlSnapshot
{
    public List<PackageRow> Packages {get;set;}=new();
    public List<HistoryRow> History {get;set;}=new();
    public List<CertificateRow> Certificates {get;set;}=new();
    public List<ProfileRow> Profiles {get;set;}=new();
    public List<ReleaseRow> Releases {get;set;}=new();
    public sealed record PackageRow(Guid Id,long ObjectId,long? ContractId,string Number,string Status,
        string Json,long Revision,DateTime UpdatedUtc);
    public sealed record HistoryRow(Guid PackageId,long ObjectId,long Revision,string Action,string Json,DateTime CreatedUtc);
    public sealed record CertificateRow(Guid Id,string Sha256,string Json,long Revision,DateTime UpdatedUtc);
    public sealed record ProfileRow(long ObjectId,string Json,long Revision);
    public sealed record ReleaseRow(Guid Id,Guid PackageId,long ObjectId,
        string RelativePath,string Sha256,DateTime CreatedUtc);
}

public static class SqlServerPtoBackup
{
    private static SqlConnection Open(string connection)
    {
        SqlServerWorkspace.RequirePilotDatabase(connection);
        var conn=SqlServerDatabase.Open(connection);
        if(!conn.Database.StartsWith("GmcSmetaPilot_",StringComparison.OrdinalIgnoreCase))
        {conn.Dispose();throw new InvalidOperationException("PTO restore: только GmcSmetaPilot_* SQL Server.");}
        return conn;
    }
    public static PtoSqlSnapshot Export(string connection)
    {
        _=new SqlServerPto(connection);
        using var conn=Open(connection);
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        var snapshot=new PtoSqlSnapshot();
        using(var cmd=new SqlCommand(
            "SELECT id,object_id,contract_id,act_number_key,status,payload_json,revision,updated_utc "+
            "FROM dbo.gmc_pto_packages ORDER BY object_id,id",conn,tx))
        using(var r=cmd.ExecuteReader())
            while(r.Read())snapshot.Packages.Add(new(
                r.GetGuid(0),SqlInteger.ReadInt64(r, 1),r.IsDBNull(2)?null:SqlInteger.ReadInt64(r, 2),
                r.GetString(3),r.GetString(4),r.GetString(5),SqlInteger.ReadInt64(r, 6),r.GetDateTime(7)));
        using(var cmd=new SqlCommand(
            "SELECT package_id,object_id,revision,action,payload_json,created_utc "+
            "FROM dbo.gmc_pto_history ORDER BY id",conn,tx))
        using(var r=cmd.ExecuteReader())
            while(r.Read())snapshot.History.Add(new(
                r.GetGuid(0),SqlInteger.ReadInt64(r, 1),SqlInteger.ReadInt64(r, 2),r.GetString(3),r.GetString(4),r.GetDateTime(5)));
        using(var cmd=new SqlCommand(
            "SELECT id,sha256,payload_json,revision,updated_utc FROM dbo.gmc_pto_certificates ORDER BY id",conn,tx))
        using(var r=cmd.ExecuteReader())
            while(r.Read())snapshot.Certificates.Add(new(
                r.GetGuid(0),r.GetString(1).TrimEnd(),r.GetString(2),SqlInteger.ReadInt64(r, 3),r.GetDateTime(4)));
        using(var cmd=new SqlCommand(
            "SELECT object_id,payload_json,revision FROM dbo.gmc_pto_object_profiles ORDER BY object_id",conn,tx))
        using(var r=cmd.ExecuteReader())
            while(r.Read())snapshot.Profiles.Add(new(SqlInteger.ReadInt64(r, 0),r.GetString(1),SqlInteger.ReadInt64(r, 2)));
        using(var cmd=new SqlCommand(
            "SELECT id,package_id,object_id,relative_path,manifest_sha256,created_utc "+
            "FROM dbo.gmc_pto_releases ORDER BY created_utc,id",conn,tx))
        using(var r=cmd.ExecuteReader())
            while(r.Read())snapshot.Releases.Add(new(r.GetGuid(0),r.GetGuid(1),SqlInteger.ReadInt64(r, 2),
                r.GetString(3),r.GetString(4).TrimEnd(),r.GetDateTime(5)));
        tx.Commit();
        return snapshot;
    }

    private static void CheckEmpty(SqlConnection conn,SqlTransaction tx)
    {
        foreach(var table in new[]{"gmc_pto_packages","gmc_pto_history","gmc_pto_certificates",
                     "gmc_pto_object_profiles","gmc_pto_releases"})
        {
            using var cmd=new SqlCommand($"SELECT COUNT_BIG(*) FROM dbo.[{table}] WITH (UPDLOCK,HOLDLOCK)",conn,tx);
            if(Convert.ToInt64(cmd.ExecuteScalar())!=0)
                throw new InvalidOperationException(
                    "SQL Server уже содержит данные ПТО. Восстановление допускается только в пустое пилотное хранилище.");
        }
    }
    public static void Import(string connection,PtoSqlSnapshot snapshot)
    {
        if(snapshot is null)throw new ArgumentNullException(nameof(snapshot));
        // Check inconsistent backups before making any writes.
        if(snapshot.Packages.Select(p=>p.Id).Distinct().Count()!=snapshot.Packages.Count ||
           snapshot.Certificates.Select(c=>c.Id).Distinct().Count()!=snapshot.Certificates.Count ||
           snapshot.Releases.Select(r=>r.Id).Distinct().Count()!=snapshot.Releases.Count)
            throw new InvalidDataException("Резервная копия содержит дубли ID.");
        var packageIds=snapshot.Packages.Select(p=>p.Id).ToHashSet();
        if(snapshot.Releases.Any(r=>!packageIds.Contains(r.PackageId)))
            throw new InvalidDataException("Восстановление: выпуск ссылается на отсутствующий АОСР.");
        foreach(var row in snapshot.Packages)
        {
            var p=System.Text.Json.JsonSerializer.Deserialize<PtoPackage>(row.Json,PtoRules.Json)
                ?? throw new InvalidDataException("Повреждён JSON АОСР.");
            PtoRules.ValidateShape(p);
            if(p.Id!=row.Id || p.ObjectId!=row.ObjectId ||
               PtoRules.NumberKey(PtoRules.Text(p,"number"))!=row.Number)
                throw new InvalidDataException("Несогласованная привязка JSON/SQL АОСР.");
        }
        _=new SqlServerPto(connection);
        using var conn=Open(connection);
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        using(var lockCmd=new SqlCommand(
            "DECLARE @r int; EXEC @r=sys.sp_getapplock "+
            "@Resource=N'GMC.PTO.Restore',@LockMode='Exclusive',@LockOwner='Transaction',"+
            "@LockTimeout=15000; SELECT @r;",conn,tx))
            if(Convert.ToInt32(lockCmd.ExecuteScalar())<0)
                throw new TimeoutException("Не удалось заблокировать восстановление.");
        CheckEmpty(conn,tx);
        var objects=snapshot.Packages.Select(p=>p.ObjectId)
            .Concat(snapshot.Profiles.Select(p=>p.ObjectId)).Distinct().ToArray();
        foreach(var obj in objects)
        {
            using var cmd=new SqlCommand("SELECT COUNT_BIG(1) FROM dbo.objects WHERE id=@id",conn,tx);
            cmd.Parameters.AddWithValue("@id",obj);
            if(Convert.ToInt64(cmd.ExecuteScalar())!=1)
                throw new InvalidOperationException("Пилотная SQL-база не содержит объект ID="+obj+
                    ". Сначала восстановите объекты из проверенной копии SQL Server.");
        }
        foreach(var p in snapshot.Packages)
        {
            using var cmd=new SqlCommand(
                "INSERT dbo.gmc_pto_packages(id,object_id,contract_id,act_number_key,status,payload_json,"+
                "revision,updated_utc) VALUES(@id,@obj,@contract,@number,@status,@json,@rev,@time)",conn,tx);
            cmd.Parameters.AddWithValue("@id",p.Id);
            cmd.Parameters.AddWithValue("@obj",p.ObjectId);
            cmd.Parameters.Add("@contract",SqlDbType.BigInt).Value=p.ContractId.HasValue?p.ContractId.Value:DBNull.Value;
            cmd.Parameters.AddWithValue("@number",p.Number);
            cmd.Parameters.AddWithValue("@status",p.Status);
            cmd.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=p.Json;
            cmd.Parameters.AddWithValue("@rev",p.Revision);
            cmd.Parameters.AddWithValue("@time",p.UpdatedUtc);
            cmd.ExecuteNonQuery();
        }
        foreach(var h in snapshot.History)
        {
            using var cmd=new SqlCommand(
                "INSERT dbo.gmc_pto_history(package_id,object_id,revision,action,payload_json,created_utc) "+
                "VALUES(@id,@obj,@rev,@action,@json,@time)",conn,tx);
            cmd.Parameters.AddWithValue("@id",h.PackageId);
            cmd.Parameters.AddWithValue("@obj",h.ObjectId);
            cmd.Parameters.AddWithValue("@rev",h.Revision);
            cmd.Parameters.AddWithValue("@action",h.Action);
            cmd.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=h.Json;
            cmd.Parameters.AddWithValue("@time",h.CreatedUtc);
            cmd.ExecuteNonQuery();
        }
        foreach(var c in snapshot.Certificates)
        {
            using var cmd=new SqlCommand(
                "INSERT dbo.gmc_pto_certificates(id,sha256,payload_json,revision,updated_utc) "+
                "VALUES(@id,@sha,@json,@rev,@time)",conn,tx);
            cmd.Parameters.AddWithValue("@id",c.Id);
            cmd.Parameters.Add("@sha",SqlDbType.Char,64).Value=c.Sha256;
            cmd.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=c.Json;
            cmd.Parameters.AddWithValue("@rev",c.Revision);
            cmd.Parameters.AddWithValue("@time",c.UpdatedUtc);
            cmd.ExecuteNonQuery();
        }
        foreach(var p in snapshot.Profiles)
        {
            using var cmd=new SqlCommand(
                "INSERT dbo.gmc_pto_object_profiles(object_id,payload_json,revision) VALUES(@id,@json,@rev)",conn,tx);
            cmd.Parameters.AddWithValue("@id",p.ObjectId);
            cmd.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=p.Json;
            cmd.Parameters.AddWithValue("@rev",p.Revision);
            cmd.ExecuteNonQuery();
        }
        foreach(var r in snapshot.Releases)
        {
            using var cmd=new SqlCommand(
                "INSERT dbo.gmc_pto_releases(id,package_id,object_id,relative_path,manifest_sha256,created_utc) "+
                "VALUES(@id,@pid,@obj,@path,@sha,@time)",conn,tx);
            cmd.Parameters.AddWithValue("@id",r.Id);
            cmd.Parameters.AddWithValue("@pid",r.PackageId);
            cmd.Parameters.AddWithValue("@obj",r.ObjectId);
            cmd.Parameters.AddWithValue("@path",r.RelativePath);
            cmd.Parameters.Add("@sha",SqlDbType.Char,64).Value=r.Sha256;
            cmd.Parameters.AddWithValue("@time",r.CreatedUtc);
            cmd.ExecuteNonQuery();
        }
        SqlServerPto.CommitPreservingFiles(tx);
    }
}
