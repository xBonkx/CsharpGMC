using System.Globalization;
using System.Reflection;
using System.Text.Json;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

// Owner-approved office migration. Rehearsal and apply are separate explicit modes.
try
{
    if (args.Length != 2 || args[0] is not ("--rehearse-office" or "--apply-office"))
        throw new InvalidOperationException("Usage: --rehearse-office|--apply-office <evidence directory>");
    var apply = args[0] == "--apply-office";
    var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
    var raw = Environment.GetEnvironmentVariable("GMC_AGREEMENT_MIGRATION_CONNECTION")
        ?? throw new InvalidOperationException("Set the migration administrator connection (master).");
    var settings = new SqlConnectionStringBuilder(raw) { Pooling = false, ApplicationName = "GMC approved agreement migration" };
    if (settings.InitialCatalog != "master" || !SqlSchemaCompatibility.IsLocalServer(settings.DataSource))
        throw new InvalidOperationException("Only local master provisioning is allowed.");
    const string office = "GmcSmetaPilot_Cutover_20261125";
    using var admin = Open("master");
    if (Text(admin,"SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName'))") != @"DIMA\SQLEXPRESS")
        throw new InvalidOperationException("The actual SQL Server is not the owner's approved DIMA instance.");
    using var source = Open(office);
    if (Version(source) != 31) throw new InvalidOperationException("The office source must still be schema 31.");
    using(var appProbe=SqlServerDatabase.Open(new SqlConnectionStringBuilder(settings.ConnectionString){InitialCatalog=office}.ConnectionString))
        Console.WriteLine("Verified normal C# office profile before migration.");
    Idle(source);
    var originals = MigrationFingerprint.Describe(source);
    var before = MigrationFingerprint.Read(source, originals);
    var finance = MigrationFinanceSnapshot.ReadApprovedAgreementMigration(source, 31);
    var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..8];
    var backupDir = Text(admin,"SELECT COALESCE(CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultBackupPath')),CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultDataPath')))");
    var dataDir = Text(admin,"SELECT CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultDataPath'))");
    var logDir = Text(admin,"SELECT CONVERT(nvarchar(4000),SERVERPROPERTY('InstanceDefaultLogPath'))");
    if (backupDir.Length == 0 || dataDir.Length == 0 || logDir.Length == 0) throw new InvalidOperationException("Missing native SQL Server paths.");
    var backup = Path.Combine(backupDir, office + "_before_agreements_" + stamp + ".bak");
    var target = apply ? office : "GmcSmetaPilot_Test_Agreement_" + stamp;
    var singleUser = false;
    SqlConnection? copy = null;
    try
    {
        if (apply)
        {
            var rehearsal = Path.Combine(output,"rehearsal.json");
            if (!File.Exists(rehearsal)) throw new InvalidOperationException("A successful rehearsal report is required first.");
            using var report = JsonDocument.Parse(File.ReadAllText(rehearsal));
            var stored = JsonSerializer.Deserialize<Dictionary<string,MigrationFingerprint.Digest>>(report.RootElement.GetProperty("originals").GetRawText())!;
            MigrationFingerprint.RequireSame(stored,before,"office unchanged since rehearsal");
            // NO_WAIT never disconnects other users or discards unsaved work.
            Exec(source,$"ALTER DATABASE {Id(office)} SET SINGLE_USER WITH NO_WAIT");
            singleUser = true;
            Idle(source);
        }
        Exec(source,$"BACKUP DATABASE {Id(office)} TO DISK={Lit(backup)} WITH COPY_ONLY,CHECKSUM");
        Exec(source,$"RESTORE VERIFYONLY FROM DISK={Lit(backup)} WITH CHECKSUM");
        if (!apply)
        {
            var files = new List<(string Name,int Type)>();
            using (var cmd = new SqlCommand("SELECT name,type FROM sys.master_files WHERE database_id=DB_ID(@db) ORDER BY file_id",admin))
            {
                cmd.Parameters.AddWithValue("@db",office); using var r=cmd.ExecuteReader();
                while(r.Read()) files.Add((r.GetString(0),Convert.ToInt32(r.GetValue(1))));
            }
            if (!files.Select(f=>f.Type).SequenceEqual(new[]{0,1})) throw new InvalidOperationException("Unexpected data/log layout; stop before restore.");
            if (Scalar(admin,$"SELECT DB_ID({Lit(target)})") is not DBNull) throw new InvalidOperationException("Restore destination already exists.");
            Exec(admin,$"RESTORE DATABASE {Id(target)} FROM DISK={Lit(backup)} WITH CHECKSUM," +
                $" MOVE {Lit(files[0].Name)} TO {Lit(Path.Combine(dataDir,target+".mdf"))}," +
                $" MOVE {Lit(files[1].Name)} TO {Lit(Path.Combine(logDir,target+"_log.ldf"))}");
            copy=Open(target);
            MigrationFingerprint.RequireSame(before,MigrationFingerprint.Read(copy,originals),"actual backup restore");
            finance.RequireSame(MigrationFinanceSnapshot.ReadApprovedAgreementMigration(copy,31),"actual backup restore");
        }
        var work=copy??source;
        Exec(work,"EXEC sys.sp_set_session_context @key=N'AGR10_APPROVED_DATABASE',@value="+Lit(target));
        Idle(work);
        // Prove an empty downgrade and repeatability BEFORE importing any records.
        if (!apply)
        {
            Exec(work,Script("expand_v32.sql"));
            Exec(work,Script("expand_v32.sql"));
            Exec(work,Script("rollback_empty_v32.sql"));
            MigrationFingerprint.RequireSame(before,MigrationFingerprint.Read(work,originals),"empty schema rollback");
        }
        var plan=AgreementBackfill.Plan(work);
        if(!apply)
        {
            using(var fault=work.BeginTransaction(System.Data.IsolationLevel.Serializable))
            {
                Exec(work,Script("expand_v32.sql"),fault);
                AgreementBackfill.Run(work,fault,plan,output);
                fault.Rollback();
            }
            if(Version(work)!=31)throw new InvalidOperationException("Fault rollback failed to restore schema 31.");
            MigrationFingerprint.RequireSame(before,MigrationFingerprint.Read(work,originals),"forced rollback after full backfill");
        }
        using(var migration=work.BeginTransaction(System.Data.IsolationLevel.Serializable))
        {
            Exec(work,Script("expand_v32.sql"),migration);
            AgreementBackfill.Run(work,migration,plan,output);
            MigrationFingerprint.RequireSame(before,MigrationFingerprint.Read(work,originals,true,migration),"transactional agreement migration");
            migration.Commit();
        }
        MigrationFingerprint.RequireSame(before,MigrationFingerprint.Read(work,originals,true),"agreement migration");
        finance.RequireSame(MigrationFinanceSnapshot.ReadApprovedAgreementMigration(work,32),"agreement migration");
        Exec(work,$"DBCC CHECKDB ({Id(target)}) WITH NO_INFOMSGS,ALL_ERRORMSGS");
        if (!apply)
        {
            MigrationFingerprint.RequireSame(before,MigrationFingerprint.Read(source,originals),"office source untouched");
            finance.RequireSame(MigrationFinanceSnapshot.ReadApprovedAgreementMigration(source,31),"office source untouched");
            AgreementAcceptance.Run(new SqlConnectionStringBuilder(settings.ConnectionString){InitialCatalog=target}.ConnectionString);
        }
        var evidence=new { database=target, backup, originalDatabase=office, schema=32, originals=before, completedUtc=DateTime.UtcNow,
            legalCount=Convert.ToInt64(Scalar(work,"SELECT COUNT_BIG(*) FROM dbo.agreements")) };
        File.WriteAllText(Path.Combine(output,apply?"applied.json":"rehearsal.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"PASS {(apply?"OFFICE":"REHEARSAL")}: {target}; original data and financial reports unchanged; backup={backup}");
    }
    finally
    {
        copy?.Dispose();
        if (singleUser) { source.Close(); Exec(admin,$"ALTER DATABASE {Id(office)} SET MULTI_USER WITH NO_WAIT"); }
    }
    return 0;

    SqlConnection Open(string name)
    {
        var conn=new SqlConnection(new SqlConnectionStringBuilder(settings.ConnectionString){InitialCatalog=name}.ConnectionString);
        try { conn.Open(); return conn; } catch {conn.Dispose();throw;}
    }
}
catch(Exception error)
{
    Console.Error.WriteLine(error is SqlException sql?$"SQL {sql.Number}: {sql.Message}":error.Message);
    return 1;
}

static void Idle(SqlConnection conn)
{
    using var cmd=new SqlCommand("SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process=1 AND database_id=DB_ID() AND session_id<>@@SPID",conn);
    if(Convert.ToInt32(cmd.ExecuteScalar())!=0)throw new InvalidOperationException("Other database sessions exist; no users were disconnected.");
}
static int Version(SqlConnection conn)=>Convert.ToInt32(Scalar(conn,"SELECT [value] FROM dbo.meta WHERE [key]=N'schema_version'"),CultureInfo.InvariantCulture);
static object? Scalar(SqlConnection conn,string sql){using var cmd=new SqlCommand(sql,conn){CommandTimeout=1800};return cmd.ExecuteScalar();}
static string Text(SqlConnection conn,string sql)=>Convert.ToString(Scalar(conn,sql),CultureInfo.InvariantCulture)??"";
static void Exec(SqlConnection conn,string sql,SqlTransaction? tx=null){using var cmd=new SqlCommand(sql,conn,tx){CommandTimeout=1800};cmd.ExecuteNonQuery();}
static string Id(string value)=>"["+value.Replace("]","]]")+"]";
static string Lit(string value)=>"N'"+value.Replace("'","''")+"'";
static string Script(string name){using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(name)??throw new InvalidOperationException("Missing script "+name);using var reader=new StreamReader(stream);return reader.ReadToEnd();}
