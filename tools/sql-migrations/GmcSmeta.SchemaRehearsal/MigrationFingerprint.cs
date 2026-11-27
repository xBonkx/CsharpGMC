using System.Data;
using System.Security.Cryptography;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

/// <summary>Compare the exact stored values of original SQL columns. The schema
/// marker is verified separately; only that one metadata row may change.</summary>
internal static class MigrationFingerprint
{
    internal sealed record Column(string Name,string Type,int MaxLength,int Precision,int Scale,bool Nullable,int KeyOrdinal);
    internal sealed record Table(string Schema,string Name,IReadOnlyList<Column> Columns)
    {
        public string Key=>Id(Schema)+"."+Id(Name);
    }
    internal sealed record Digest(long Rows,string Hash);
    private static string Id(string value)=>"["+value.Replace("]","]]")+"]";

    internal static List<Table> Describe(SqlConnection connection,SqlTransaction? transaction=null)
    {
        using var cmd=new SqlCommand("""
            SELECT SCHEMA_NAME(t.schema_id),t.name,c.name,TYPE_NAME(c.system_type_id),
                c.max_length,c.precision,c.scale,c.is_nullable,COALESCE(ic.key_ordinal,0)
            FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id
            LEFT JOIN sys.indexes pk ON pk.object_id=t.object_id AND pk.is_primary_key=1
            LEFT JOIN sys.index_columns ic ON ic.object_id=pk.object_id AND ic.index_id=pk.index_id
                AND ic.column_id=c.column_id
            WHERE t.is_ms_shipped=0 ORDER BY t.schema_id,t.name,c.column_id
            """,connection,transaction);
        using var r=cmd.ExecuteReader();
        var grouped=new Dictionary<(string Schema,string Name),List<Column>>();
        while(r.Read())
        {
            var key=(r.GetString(0),r.GetString(1));
            if(!grouped.TryGetValue(key,out var list)) grouped[key]=list=new();
            list.Add(new(r.GetString(2),r.GetString(3),Convert.ToInt32(r.GetValue(4)),
                Convert.ToInt32(r.GetValue(5)),Convert.ToInt32(r.GetValue(6)),r.GetBoolean(7),
                Convert.ToInt32(r.GetValue(8))));
        }
        var tables=grouped.Select(x=>new Table(x.Key.Schema,x.Key.Name,x.Value)).ToList();
        if(tables.Count==0||tables.Any(t=>!t.Columns.Any(c=>c.KeyOrdinal>0)))
            throw new InvalidOperationException("All existing tables must have a stable primary key.");
        return tables;
    }

    internal static Dictionary<string,Digest> Read(SqlConnection connection,IReadOnlyList<Table> originals,bool allowAgreementExtension=false,SqlTransaction? transaction=null)
    {
        var actual=Describe(connection,transaction).ToDictionary(t=>t.Key,StringComparer.Ordinal);
        if(!allowAgreementExtension && actual.Count!=originals.Count)
            throw new InvalidOperationException("Existing table set changed.");
        if(allowAgreementExtension && actual.Keys.Except(originals.Select(t=>t.Key)).Any(k=>k is not ("[dbo].[agreements]" or "[dbo].[agreement_files]" or "[dbo].[agreement_migration_links]")))
            throw new InvalidOperationException("Unexpected added tables.");
        var result=new Dictionary<string,Digest>(StringComparer.Ordinal);
        foreach(var table in originals)
        {
            if(!actual.TryGetValue(table.Key,out var now)||table.Columns.Any(c=>!now.Columns.Contains(c))||
                !table.Columns.Where(c=>c.KeyOrdinal>0).SequenceEqual(now.Columns.Where(c=>c.KeyOrdinal>0)))
                throw new InvalidOperationException("Original column schema changed: "+table.Key);
            var projection=string.Join(",",table.Columns.Select(c=>"CONVERT(varbinary(max),"+Id(c.Name)+")"));
            var order=string.Join(",",table.Columns.Where(c=>c.KeyOrdinal>0).OrderBy(c=>c.KeyOrdinal).Select(c=>Id(c.Name)));
            var where=table.Schema=="dbo"&&table.Name=="meta"?allowAgreementExtension?" WHERE [key] NOT IN(N'schema_version',N'agr10_schema_manifest')":" WHERE [key]<>N'schema_version'":"";
            using var cmd=new SqlCommand("SELECT "+projection+" FROM "+table.Key+where+" ORDER BY "+order,connection,transaction)
                {CommandTimeout=1800};
            using var reader=cmd.ExecuteReader(CommandBehavior.SequentialAccess);
            using var hash=new MigrationValueHash();
            long count=0;
            while(reader.Read())
            {
                for(var i=0;i<table.Columns.Count;i++)
                {
                    if(reader.IsDBNull(i)) hash.Append(null);
                    else hash.Append(reader.GetFieldValue<byte[]>(i));
                }
                count++;
            }
            result.Add(table.Key,new(count,hash.Finish()));
        }
        return result;
    }

    internal static void RequireSame(Dictionary<string,Digest> before,Dictionary<string,Digest> after,string stage)
    {
        if(before.Count!=after.Count) throw new InvalidOperationException("Table count differs: "+stage);
        foreach(var (name,digest) in before)
            if(!after.TryGetValue(name,out var current)||digest!=current)
                throw new InvalidOperationException("Existing data changed in "+name+" during "+stage+".");
        Console.WriteLine("Verified original values: "+stage+" ("+before.Count+" tables, "+before.Values.Sum(v=>v.Rows)+" rows, excluding schema marker).");
    }
}
