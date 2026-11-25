using System.Data;
using System.Globalization;
using System.Reflection;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

// This tool NEVER migrates its source. It backs up a named test database,
// restores a new isolated copy, and migrates only that copy.
try
{
    if (args.Length != 4 || args[0] != "--test-only" || args[2] != "--target" ||
        !int.TryParse(args[3], out var target))
        throw new InvalidOperationException("Usage: --test-only GmcSmetaPilot_Test_NAME --target 31");
    var name = args[1];
    SqlMigrationPlan.RequireRehearsalSource(name);
    var raw = Environment.GetEnvironmentVariable("GMC_SMETA_MIGRATION_CONNECTION")
        ?? throw new InvalidOperationException("Set GMC_SMETA_MIGRATION_CONNECTION for the explicitly selected test database.");
    var settings = new SqlConnectionStringBuilder(raw);
    if (!string.Equals(settings.InitialCatalog, name, StringComparison.Ordinal))
        throw new InvalidOperationException("Connection catalog must exactly match --test-only.");
    settings.Pooling = false;
    settings.ApplicationName = "GMC schema rehearsal";
    using var source = Open(name);
    using var admin = Open("master");
    if (Convert.ToInt32(Scalar(admin,"SELECT IS_SRVROLEMEMBER('sysadmin')"),CultureInfo.InvariantCulture) != 1)
        throw new InvalidOperationException("SQL sysadmin is required for this isolated rehearsal.");
    string Identity(SqlConnection c) => Convert.ToString(Scalar(c,"SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName'))"),CultureInfo.InvariantCulture) ?? "";
    if (Identity(source) != Identity(admin) || source.Database != name)
        throw new InvalidOperationException("SQL server/catalog identity mismatch.");
    var version = VersionOf(source);
    var script = SqlMigrationPlan.Script(version,target);
    Verify(source,version);
    if (script is null) { Console.WriteLine("Schema already matches; no backup or migration performed."); return 0; }

    using (var guard = new SqlCommand("DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@name,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @r;",admin))
    {
        guard.Parameters.AddWithValue("@name","GMC-Smeta-schema-release:"+name);
        if (Convert.ToInt32(guard.ExecuteScalar()) < 0) throw new InvalidOperationException("Another migration rehearsal is active.");
    }
    CheckIdle(admin,name,source.ServerProcessId);
    Console.WriteLine("Fingerprint original tables and values…");
    var original = MigrationFingerprint.Describe(source);
    var before = MigrationFingerprint.Read(source,original);
    var finance=MigrationFinanceSnapshot.Read(source,version);
    Console.WriteLine($"Finance baseline: {finance.Contracts.Count} contracts, {finance.Acts.Count} acts, {finance.Groups.Count} KS3 scoped groups.");
    CheckIdle(admin,name,source.ServerProcessId);
    using var paths = new SqlCommand("SELECT CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultBackupPath')),CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultDataPath')),CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultLogPath'))",admin);
    string backupDir,dataDir,logDir;
    using (var r=paths.ExecuteReader())
    {
        if (!r.Read() || r.IsDBNull(1) || r.IsDBNull(2)) throw new InvalidOperationException("SQL Server default paths missing.");
        dataDir=r.GetString(1); logDir=r.GetString(2); backupDir=r.IsDBNull(0)?dataDir:r.GetString(0);
    }
    var physical = new List<(string Name,int Type)>();
    using (var cmd = new SqlCommand("SELECT name,type FROM sys.master_files WHERE database_id=DB_ID(@db) ORDER BY file_id",admin))
    {
        cmd.Parameters.AddWithValue("@db",name);
        using var r=cmd.ExecuteReader();
        while(r.Read()) physical.Add((r.GetString(0),Convert.ToInt32(r.GetValue(1))));
    }
    if (!physical.Select(f=>f.Type).SequenceEqual(new[]{0,1}))
        throw new InvalidOperationException("Expected one data file and one log file.");
    var suffix=DateTime.UtcNow.ToString("yyyyMMdd_HHmmss",CultureInfo.InvariantCulture)+"_"+Guid.NewGuid().ToString("N")[..8];
    var copyName="GmcSmetaPilot_ReleaseCheck_"+suffix;
    string ServerPath(string folder,string file) => folder.TrimEnd('\\','/')+(folder.Contains('\\')?"\\":"/")+file;
    var backup=ServerPath(backupDir,name+"_before_v"+target+"_"+suffix+".bak");
    Console.WriteLine("Native backup: "+backup);
    Exec(admin,$"BACKUP DATABASE {Id(name)} TO DISK={Lit(backup)} WITH COPY_ONLY,CHECKSUM");
    Exec(admin,$"RESTORE VERIFYONLY FROM DISK={Lit(backup)} WITH CHECKSUM");
    if (Scalar(admin,$"SELECT DB_ID({Lit(copyName)})") is not DBNull and not null)
        throw new InvalidOperationException("Restore destination already exists.");
    Console.WriteLine("Restore isolated copy: "+copyName);
    Exec(admin,$"RESTORE DATABASE {Id(copyName)} FROM DISK={Lit(backup)} WITH CHECKSUM, MOVE {Lit(physical[0].Name)} TO {Lit(ServerPath(dataDir,copyName+".mdf"))}, MOVE {Lit(physical[1].Name)} TO {Lit(ServerPath(logDir,copyName+"_log.ldf"))}");
    using var copy=Open(copyName);
    if (copy.Database!=copyName || Identity(copy)!=Identity(source)) throw new InvalidOperationException("Restore identity mismatch.");
    Verify(copy,version);
    MigrationFingerprint.RequireSame(before,MigrationFingerprint.Read(copy,original),"backup restore");
    finance.RequireSame(MigrationFinanceSnapshot.Read(copy,version),"backup restore finance");
    Console.WriteLine("Verified finance: backup restore");
    CheckIdle(admin,copyName,copy.ServerProcessId);
    using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(script)
        ?? throw new InvalidOperationException("Migration resource missing.");
    using var reader=new StreamReader(stream);
    Exec(copy,reader.ReadToEnd());
    Verify(copy,target);
    MigrationFingerprint.RequireSame(before,MigrationFingerprint.Read(copy,original),"migration");
    VerifyNewDefaults(copy,version,target);
    finance.RequireSame(MigrationFinanceSnapshot.Read(copy,target),"migration finance");
    Console.WriteLine("Verified finance: migration (contracts, KS2, KS3)");
    CheckIdle(admin,copyName,copy.ServerProcessId);
    Exec(admin,$"DBCC CHECKDB ({Id(copyName)}) WITH NO_INFOMSGS, ALL_ERRORMSGS");
    CheckIdle(admin,name,source.ServerProcessId);
    Verify(source,version);
    MigrationFingerprint.RequireSame(before,MigrationFingerprint.Read(source,original),"source unchanged");
    finance.RequireSame(MigrationFinanceSnapshot.Read(source,version),"source unchanged finance");
    Console.WriteLine($"TEST ONLY: {version} -> {target} verified. Original table contents, new defaults and C# financial totals match. Copy: {copyName}; backup: {backup}");
    return 0;

    SqlConnection Open(string db)
    {
        var b=new SqlConnectionStringBuilder(settings.ConnectionString){InitialCatalog=db};
        var c=new SqlConnection(b.ConnectionString);
        try { c.Open(); return c; } catch { c.Dispose(); throw; }
    }
}
catch(Exception ex)
{
    // Do not print connection strings or credentials.
    Console.Error.WriteLine(ex is SqlException sql ? $"SQL failure {sql.Number}: {sql.Message}" : ex.Message);
    return 1;
}

static string Id(string s)=>"["+s.Replace("]","]]")+"]";
static string Lit(string s)=>"N'"+s.Replace("'","''")+"'";
static object? Scalar(SqlConnection c,string sql)
{
    using var cmd=new SqlCommand(sql,c){CommandTimeout=60};
    return cmd.ExecuteScalar();
}
static void Exec(SqlConnection c,string sql)
{
    using var cmd=new SqlCommand(sql,c){CommandTimeout=1800};
    cmd.ExecuteNonQuery();
}
static int VersionOf(SqlConnection c)
{
    using var cmd=new SqlCommand("SELECT [value] FROM dbo.meta WHERE [key]=N'schema_version'",c);
    using var r=cmd.ExecuteReader();
    if (!r.Read() || !int.TryParse(Convert.ToString(r.GetValue(0),CultureInfo.InvariantCulture),out var v)||r.Read())
        throw new InvalidOperationException("Invalid schema marker.");
    return v;
}
static void Verify(SqlConnection c,int version)
{
    if(VersionOf(c)!=version) throw new InvalidOperationException("Schema version mismatch.");
    var fields=new List<(string Table,string Column,string Type)>();
    if(version>=29) fields.AddRange(new[]{("contracts","discount_percent","float"),("acts","discount_percent","float")});
    if(version>=30) fields.Add(("acts","section_amounts_json","nvarchar"));
    if(version>=31) fields.AddRange(new[]{("acts","discount_override","bit"),("acts","manual_total_cents","bigint")});
    foreach(var (table,col,type) in fields)
    {
        using var cmd=new SqlCommand("SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(@t) AND name=@c AND TYPE_NAME(system_type_id)=@type AND is_nullable=0",c);
        cmd.Parameters.AddWithValue("@t","dbo."+table);cmd.Parameters.AddWithValue("@c",col);cmd.Parameters.AddWithValue("@type",type);
        if(Convert.ToInt32(cmd.ExecuteScalar())!=1) throw new InvalidOperationException("Invalid column "+table+"."+col);
        if(Convert.ToInt64(Scalar(c,$"SELECT COUNT_BIG(*) FROM dbo.{Id(table)} WHERE {Id(col)} IS NULL"))!=0)
            throw new InvalidOperationException("Unexpected NULL in "+table+"."+col);
    }
}
static void CheckIdle(SqlConnection admin,string database,int ownSpid)
{
    using var cmd=new SqlCommand("""
        SELECT COUNT_BIG(*) FROM sys.dm_exec_sessions
        WHERE is_user_process=1 AND database_id=DB_ID(@db) AND session_id<>@self
        """,admin);
    cmd.Parameters.Add("@db",SqlDbType.NVarChar,128).Value=database;
    cmd.Parameters.Add("@self",SqlDbType.Int).Value=ownSpid;
    if(Convert.ToInt64(cmd.ExecuteScalar())!=0)
        throw new InvalidOperationException("Active sessions in "+database+". Close clients and retry; no sessions were killed.");
}
static void VerifyNewDefaults(SqlConnection c,int from,int to)
{
    void Check(string table,string column,string expected)
    {
        if(Convert.ToInt64(Scalar(c,$"SELECT COUNT_BIG(*) FROM dbo.{Id(table)} WHERE {Id(column)} IS NULL OR {Id(column)}<>{expected}"))!=0)
            throw new InvalidOperationException("Unexpected migrated default: "+table+"."+column);
    }
    if(from<29&&to>=29)
    {
        Check("contracts","discount_percent","0");
        Check("acts","discount_percent","0");
    }
    if(from<30&&to>=30) Check("acts","section_amounts_json","N''");
    if(from<31&&to>=31)
    {
        Check("acts","discount_override","0");
        Check("acts","manual_total_cents","-1");
    }
}
