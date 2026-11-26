using System.Collections.Concurrent;
using System.Data;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

internal static class Program
{
    private static int passed;
    private static void Check(bool okay,string message)
    {
        if(!okay)throw new Exception("PTO SQL FAILURE: "+message);
        passed++;
    }
    private static void Expect<T>(Action a) where T:Exception
    {
        try{a();}
        catch(T){passed++;return;}
        throw new Exception("PTO SQL expected "+typeof(T).Name);
    }
    private static void Create(SqlConnectionStringBuilder master,string database)
    {
        using(var con=new SqlConnection(master.ConnectionString))
        {
            con.Open();using var cmd=new SqlCommand($"CREATE DATABASE [{database}]",con)
            {CommandTimeout=90};cmd.ExecuteNonQuery();
        }
        var scope=new SqlConnectionStringBuilder(master.ConnectionString){InitialCatalog=database};
        using var conn=new SqlConnection(scope.ConnectionString);conn.Open();
        using var schema=new SqlCommand(
            "CREATE TABLE dbo.meta([key] nvarchar(128) PRIMARY KEY,[value] nvarchar(128));"+
            "INSERT dbo.meta([key],[value]) VALUES(N'schema_version',N'28');"+
            "CREATE TABLE dbo.objects(id bigint PRIMARY KEY,name nvarchar(255));"+
            "INSERT dbo.objects(id,name) VALUES(1,N'Объект 1'),(2,N'Объект 2');",conn);
        schema.ExecuteNonQuery();
    }
    private static void Drop(SqlConnectionStringBuilder master,string db)
    {
        if(!db.StartsWith("GmcSmetaPilot_PtoCI_",StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe temporary database name.");
        SqlConnection.ClearAllPools();
        using var c=new SqlConnection(master.ConnectionString);c.Open();
        using var cmd=new SqlCommand(
            $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;DROP DATABASE [{db}]",c)
            {CommandTimeout=90};
        cmd.ExecuteNonQuery();
    }
    private static PtoPackage Act(long obj,string number)
    {
        var p=PtoRules.New(obj,"Тестовый объект "+obj,"Адрес 5");
        p.Fields["number"]=number;
        return p;
    }
    private static int Main()
    {
        var connect=Environment.GetEnvironmentVariable("GMC_PTO_CI_SQL")
            ??throw new InvalidOperationException("Only disposable SQL Server integration is allowed.");
        var master=new SqlConnectionStringBuilder(connect){InitialCatalog="master",ConnectTimeout=5};
        var online=false;
        for(int i=0;i<45;i++)
        {
            try{using var connection=new SqlConnection(master.ConnectionString);
                connection.Open();online=true;break;}
            catch(SqlException){Thread.Sleep(2000);}
        }
        if(!online)throw new TimeoutException("SQL service not ready.");
        var one="GmcSmetaPilot_PtoCI_"+Guid.NewGuid().ToString("N")[..10];
        var two="GmcSmetaPilot_PtoCI_"+Guid.NewGuid().ToString("N")[..10];
        var firstCreated=false;var secondCreated=false;
        try
        {
            Create(master,one);firstCreated=true;
            var connection=new SqlConnectionStringBuilder(master.ConnectionString){InitialCatalog=one}.ConnectionString;
            var sql=new SqlServerPto(connection);
            _=new SqlServerPto(connection);
            Check(sql.Packages().Count==0,"Idempotent schema and empty database");
            var p=Act(1,"ОВ-7");
            sql.Save(p);
            Check(p.Revision==1 && sql.Get(p.Id).Fields["number"]=="ОВ-7","AOSR create/read");
            p.Fields["works"]="Монтаж воздуховодов";sql.Save(p);
            Check(p.Revision==2 && sql.History(p.Id).Count==2,"AOSR update and immutable revisions");
            var collision=Act(1,"ов-7");
            Expect<ArgumentException>(()=>sql.Save(collision));
            Check(sql.Packages().Count==1,"Duplicate act number race guard");
            var other=Act(2,"ОВ-7");sql.Save(other);
            Check(sql.Packages(1).Count==1 && sql.Packages(2).Count==1,
                "Different objects with same act number must stay isolated");
            var copy=sql.Copy(p.Id);
            Check(copy.Revision==1 && copy.Fields["number"]=="ОВ-8" &&
                copy.Fields["works"]=="","Duplicate resets works and increments number");
            var stale=sql.Get(p.Id);
            p.Fields["section"]="ОВ";sql.Save(p);
            stale.Fields["section"]="ХВ";
            Expect<DBConcurrencyException>(()=>sql.Save(stale));
            Check(sql.Get(p.Id).Fields["section"]=="ОВ","Optimistic revisions stop stale overwrite");
            var clients=new ConcurrentBag<PtoPackage>();
            Parallel.For(0,10,i=>
            {
                var duplicate=new SqlServerPto(connection).Copy(p.Id);
                clients.Add(duplicate);
            });
            Check(clients.Count==10&&clients.Select(x=>x.Fields["number"]).Distinct().Count()==10,
                "10 concurrent clients allocated duplicate act numbers");
            Check(sql.Packages(1).Count==12,"Concurrent copies missing");

            // A contract-scoped list must not include independent acts, another
            // contract of the same object or the same contract ID in another object.
            var scoped=Act(1,"SCOPE-1");scoped.ContractId=101;sql.Save(scoped);
            var sibling=Act(1,"SCOPE-2");sibling.ContractId=102;sql.Save(sibling);
            var foreign=Act(2,"SCOPE-1");foreign.ContractId=101;sql.Save(foreign);
            Check(sql.Packages(1,101).Single().Id==scoped.Id,"Contract filter crossed scope");
            Check(sql.Packages(1,102).Single().Id==sibling.Id,"Contract filter missed sibling");
            Check(sql.Packages(1,999).Count==0,"Unknown contract must return empty list");
            Check(sql.Packages(1).Count==14 && sql.Packages().Any(x=>x.Id==foreign.Id),
                "Existing object/all scopes must remain unchanged");
            Expect<ArgumentException>(()=>sql.Packages(null,101));
            using(var cancelled=new CancellationTokenSource())
            {
                cancelled.Cancel();
                Expect<OperationCanceledException>(()=>sql.Packages(1,101,cancelled.Token));
            }
            var catalog=new PtoCertificate
            {
                Kind="Декларация",Product="Сертификат трубы",Manufacturer="Завод",
                Number="№002",Issued="01.01.2026",Expires="01.01.2030",
                Sha256=new string('a',64),RelativePath="files/test-certificate.pdf",Pages=2
            };
            sql.SaveCertificate(catalog);
            Check(sql.Certificates("трубы").Count==1 && catalog.Revision==1,
                "Certificate persisted and searchable");
            var item=new PtoAttachment{Title=PtoRules.CertificateTitle(catalog),Kind=catalog.Kind,
                RelativePath=catalog.RelativePath,Sha256=catalog.Sha256,Pages=2};
            var snap=sql.Get(p.Id);
            snap.Attachments.Add(item);
            snap.CertificateSnapshots[item.Id]=catalog;
            sql.Save(snap);
            var edited=sql.Certificates().Single();
            edited.Product="Другая продукция";sql.SaveCertificate(edited);
            Check(sql.Get(p.Id).CertificateSnapshots[item.Id].Product=="Сертификат трубы",
                "Editing catalogue modified already issued AOSR metadata");
            var profileBefore=sql.ReadObjectProfile(1);
            sql.SaveObjectProfile(1,new Dictionary<string,string>{{"project","ОВ-2026"}},profileBefore.Revision);
            bool profileConflict=false;
            try{sql.SaveObjectProfile(1,new Dictionary<string,string>{{"project","Устаревшие данные"}},profileBefore.Revision);}
            catch(System.Data.DBConcurrencyException){profileConflict=true;}
            Check(profileConflict,"Stale PTO profile must be rejected");
            Check(sql.ObjectProfile(1)["project"]=="ОВ-2026","PTO profile retained separately");
            sql.RegisterRelease(new PtoReleaseRecord(Guid.NewGuid(),p.Id,1,
                "exports/"+p.Id.ToString("N")+"/dummy",new string('f',64),DateTime.UtcNow));
            Check(sql.Releases(p.Id).Count==1,"Release registry not persisted");

            // Force an actual SQL failure AFTER package update but before history insert:
            // entire transaction must roll back, including revision and changed text.
            using(var db=new SqlConnection(connection))
            {
                db.Open();using var trigger=new SqlCommand(
                    "CREATE TRIGGER dbo.gmc_pto_history_fail ON dbo.gmc_pto_history AFTER INSERT "+
                    "AS BEGIN THROW 51000,'Simulated SQL failure',1; END;",db);
                trigger.ExecuteNonQuery();
            }
            var before=sql.Get(p.Id);
            before.Fields["section"]="Должно откатиться";
            Expect<SqlException>(()=>sql.Save(before));
            using(var db=new SqlConnection(connection))
            {
                db.Open();using var cmd=new SqlCommand("DROP TRIGGER dbo.gmc_pto_history_fail",db);
                cmd.ExecuteNonQuery();
            }
            Check(sql.Get(p.Id).Fields["section"]=="ОВ" &&
                sql.Get(p.Id).Revision==snap.Revision,"SQL error left partial package update");

            var backup=SqlServerPtoBackup.Export(connection);
            Check(backup.Packages.Count==sql.Packages().Count &&
                backup.History.Count>=backup.Packages.Count,"SQL metadata backup incomplete");
            Create(master,two);secondCreated=true;
            var restored=new SqlConnectionStringBuilder(master.ConnectionString){InitialCatalog=two}.ConnectionString;
            SqlServerPtoBackup.Import(restored,backup);
            var target=new SqlServerPto(restored);
            Check(target.Packages().Count==backup.Packages.Count &&
                  target.History(p.Id).Count==sql.History(p.Id).Count,
                "Metadata restore lost packages or history");
            Check(target.Certificates().Count==1&&target.Releases(p.Id).Count==1 &&
                  target.ObjectProfile(1)["project"]=="ОВ-2026",
                "Metadata restore lost quality library, releases, or profiles");
            Expect<InvalidOperationException>(()=>SqlServerPtoBackup.Import(restored,backup));
            var remove=target.Get(copy.Id);
            target.Delete(remove);
            Check(target.Packages(1).All(x=>x.Id!=copy.Id),
                "Optimistic delete failed after restore");
            Expect<DBConcurrencyException>(()=>target.Delete(remove));
            Console.WriteLine($"PTO REAL SQL CHECKS PASSED: {passed}/{passed}");
            return 0;
        }
        catch(Exception ex)
        {
            Console.Error.WriteLine("PTO REAL SQL FAILURE after "+passed+": "+ex);
            return 1;
        }
        finally
        {
            if(secondCreated)Drop(master,two);
            if(firstCreated)Drop(master,one);
        }
    }
}
