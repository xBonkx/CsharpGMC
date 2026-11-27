using System.Collections.Concurrent;
using System.Data;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("PASSPORT SQL: " + message);
        checks++;
    }
    private static void Expect<T>(Action action) where T: Exception
    {
        try { action(); }
        catch(T) { checks++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Setup(SqlConnectionStringBuilder master,string name)
    {
        using(var conn=new SqlConnection(master.ConnectionString))
        {
            conn.Open();
            using var create=new SqlCommand($"CREATE DATABASE [{name}]",conn);
            create.CommandTimeout=60;
            create.ExecuteNonQuery();
        }
        var b=new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog=name };
        using var database=new SqlConnection(b.ConnectionString);
        database.Open();
        using var schema=new SqlCommand(
            "CREATE TABLE dbo.meta([key] NVARCHAR(128) PRIMARY KEY,[value] NVARCHAR(128));"+
            "INSERT dbo.meta([key],[value]) VALUES(N'schema_version',N'28');"+
            "CREATE TABLE dbo.objects(id BIGINT PRIMARY KEY,name NVARCHAR(255));"+
            "INSERT dbo.objects(id,name) VALUES(1,N'Объект 1'),(2,N'Объект 2');",
            database);
        schema.ExecuteNonQuery();
    }

    private static int Main()
    {
        var connection=Environment.GetEnvironmentVariable("GMC_PASSPORTS_CI_SQL");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Disposable SQL Server test connection missing.");
        var master=new SqlConnectionStringBuilder(connection) { InitialCatalog="master",ConnectTimeout=5 };
        bool available=false;
        for(int i=0;i<45;i++)
        {
            try { using var conn=new SqlConnection(master.ConnectionString);conn.Open();available=true;break; }
            catch(SqlException) { Thread.Sleep(2000); }
        }
        if(!available) throw new TimeoutException("SQL Server was not ready");
        var name="GmcSmetaPilot_PassportsCI_"+Guid.NewGuid().ToString("N")[..10];
        var created=false;
        try
        {
            Setup(master,name);created=true;
            var b=new SqlConnectionStringBuilder(master.ConnectionString){InitialCatalog=name};
            var sql=new SqlServerPassports(b.ConnectionString);
            _=new SqlServerPassports(b.ConnectionString);
            Check(sql.List(1).Count==0,"Repeatable migration");
            var bag=new ConcurrentBag<VentPassport>();
            Parallel.For(0,10,i=>
            {
                var p=VentPassports.Create(1,"Объект 1","");
                p.System="П"+(i+1);
                new SqlServerPassports(b.ConnectionString).Save(p);
                bag.Add(p);
            });
            Check(bag.Count==10 && sql.List(1).Count==10,"Concurrent saves");
            Check(sql.List(2).Count==0,"Object isolation");
            var p1=sql.Get(1,bag.First().Id);
            p1.Points.Add(new VentFlowPoint {Section="1",Room="Комната",Project="100",Actual="90"});
            p1.Equipment[0].Project[0]="Fan P";p1.Equipment[0].Actual[0]="Fan A";
            p1.Equipment[0].Project[3]="100";
            sql.Save(p1);
            var p2=sql.Get(1,p1.Id);
            Check(p2.Points.Single().Actual=="90" && p2.Revision==p1.Revision,"Read after write");
            var catalog=sql.Catalogue().Single(x=>x.Key=="Fan P / Fan A");
            Check(catalog.Equipment[0].Project[3]=="" &&
                  sql.Get(1,p1.Id).Equipment[0].Project[3]=="100","Fan template has no flow");
            var copied=VentPassports.Create(2,"Объект 2","");
            VentPassports.ApplyTemplate(copied,catalog);
            copied.System="В1";sql.Save(copied);
            copied.Equipment[0].Project[0]="Changed";sql.Save(copied);
            Check(sql.Get(1,p1.Id).Equipment[0].Project[0]=="Fan P","Catalog snapshot isolation");

            var stale=sql.Get(1,p1.Id);
            p2.System="П99";sql.Save(p2);
            stale.System="П100";
            Expect<DBConcurrencyException>(()=>sql.Save(stale));
            Check(sql.Get(1,p1.Id).System=="П99","Stale edit rejected");
            var second=sql.List(1).First(x=>x.Id!=p1.Id);
            var old=sql.Get(1,second.Id);
            second.System="В20";sql.Save(second);
            p2.System="П100";old.System="В30";
            Expect<DBConcurrencyException>(()=>sql.SaveBatch(1,new[]{p2,old}));
            Check(sql.Get(1,p1.Id).System=="П99" && sql.Get(1,second.Id).System=="В20",
                "Atomic batch rollback");
            var duplicate=VentPassports.Duplicate(sql.Get(1,p1.Id));
            sql.Save(duplicate);
            Check(duplicate.Points[0].Actual=="" && VentPassports.IsDraft(duplicate),"Copy measurement exclusion");
            var bulk=VentPassports.BatchCreate(duplicate,"П2\nВ2\nДП1");
            sql.SaveBatch(1,bulk);
            Check(sql.List(1).Count==14,"Bulk creation");
            Expect<DBConcurrencyException>(()=>sql.Delete(1,p1.Id,-1));
            Expect<KeyNotFoundException>(()=>sql.Get(2,p1.Id));
            Expect<InvalidOperationException>(()=>sql.SaveBatch(2,new[]{sql.Get(1,p1.Id)}));
            var export=new SqlServerPassports.ExportInfo(p1.Id,
                "passports/1/test-"+Guid.NewGuid().ToString("N")+".pdf",new string('a',64),true,3);
            var exportRevision=sql.Get(1,p1.Id).Revision;
            Expect<System.Data.DBConcurrencyException>(()=>sql.RegisterExport(1,export,exportRevision-1));
            Check(sql.Exports(1,p1.Id).Count==0,"Stale PDF export must not be registered");
            Check(!sql.HasAnyExports(),"Empty archive before first registered PDF");
            sql.RegisterExport(1,export,exportRevision);
            var unchanged=sql.Get(1,p1.Id);
            sql.Save(unchanged);
            Check(unchanged.Revision==exportRevision,"Unchanged passport must not bump revision");
            Check(sql.Exports(1,p1.Id).Count==1 && sql.Exports(2,p1.Id).Count==0,"PDF registry object scope");
            sql.Delete(1,duplicate.Id,duplicate.Revision);
            Check(sql.List(1).All(p=>p.Id!=duplicate.Id),"Delete + revision");
            Check(sql.HasAnyExports(),"Global archive guard sees a PDF even from another object");
            sql.Delete(1,p1.Id,exportRevision);
            Check(sql.List(1).All(p=>p.Id!=p1.Id) && sql.HasAnyExports(),
                "Deleted passport retains registered PDF; global archive guard remains active");
            Console.WriteLine($"PASSPORTS REAL SQL CHECKS PASSED: {checks}/{checks}");
            return 0;
        }
        catch(Exception e)
        {
            Console.Error.WriteLine("PASSPORTS REAL SQL FAILURE after "+checks+": "+e);
            return 1;
        }
        finally
        {
            if(created)
            {
                SqlConnection.ClearAllPools();
                using var conn=new SqlConnection(master.ConnectionString);
                conn.Open();
                using var cmd=new SqlCommand(
                    $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]",conn);
                cmd.CommandTimeout=60;
                cmd.ExecuteNonQuery();
            }
        }
    }
}
