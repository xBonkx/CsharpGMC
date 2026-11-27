using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

internal static class AgreementBackfill
{
    internal sealed record Source(long Id,long ObjectId,string Kind,string Number,string Date,long? Company,long? Base,long? Stage);
    internal sealed record Entry(SqlServerAgreements.Values Legal,IReadOnlyList<Source> Documents,string Status);

    internal static IReadOnlyList<Entry> Plan(SqlConnection conn)
    {
        var documents=new List<Source>();
        using(var cmd=new SqlCommand("SELECT id,object_id,kind,number,[date],company_id,base_contract_id,stage_of FROM dbo.contracts ORDER BY id",conn))
        using(var r=cmd.ExecuteReader())
            while(r.Read())
            {
                string S(int i)=>r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
                long? N(int i)=>r.IsDBNull(i)?null:Convert.ToInt64(r.GetValue(i));
                documents.Add(new(Convert.ToInt64(r.GetValue(0)),Convert.ToInt64(r.GetValue(1)),S(2),S(3).Trim(),S(4).Trim(),N(5),N(6),N(7)));
            }
        var companies=SqlServerAgreements.Companies(conn).ToDictionary(c=>c.Id);
        var plan=new List<Entry>();
        foreach(var group in documents.Where(d=>d.Kind is "main" or "dop").GroupBy(d=>d.ObjectId))
        {
            var mains=group.Where(d=>d.Kind=="main"&&d.Stage is null).ToArray();
            var numbers=mains.Select(d=>d.Number).Distinct().ToArray();
            var dates=mains.Select(d=>SqlServerAgreements.Date(d.Date)?.ToString("dd.MM.yyyy",CultureInfo.InvariantCulture)??"").Distinct().ToArray();
            var contractorIds=(mains.Length==0?(IEnumerable<Source>)group:mains).Select(d=>d.Company).Distinct().ToArray();
            if(numbers.Length>1||dates.Length>1||contractorIds.Length>1)
                throw new InvalidOperationException($"Object {group.Key}: conflicting main agreement metadata; no automatic merge.");
            foreach(var ds in group.Where(d=>d.Kind=="dop"&&d.Base.HasValue))
            {
                var parent=documents.SingleOrDefault(d=>d.Id==ds.Base);
                if(parent is null||parent.ObjectId!=group.Key||parent.Kind is not ("main" or "kp"))
                    throw new InvalidOperationException($"Document {ds.Id}: invalid existing DS base. Stop without rewriting links.");
            }
            var print=SqlServerKsRequisites.ReadShared(conn,group.Key);
            var number=numbers.SingleOrDefault()??"";
            var date=dates.SingleOrDefault()??"";
            var companyId=contractorIds.SingleOrDefault();
            var co=companyId is long id?companies.GetValueOrDefault(id):null;
            var legal=new SqlServerAgreements.Values(0,group.Key,number,date,companyId,"","","",
                co is null?"":string.IsNullOrWhiteSpace(co.FullName)?co.Name:co.FullName,co?.Position??"",co?.Signer??"",print);
            plan.Add(new(legal,group.ToArray(),number.Length==0||date.Length==0?"DRAFT: owner will complete legal number/date; original DS untouched":"Consistent existing main metadata"));
        }
        return plan;
    }

    internal static void Run(SqlConnection conn,SqlTransaction tx,IReadOnlyList<Entry> plan,string output)
    {
        var json=JsonSerializer.Serialize(plan,new JsonSerializerOptions{WriteIndented=true});
        var digest=SHA256.HashData(Encoding.UTF8.GetBytes(json));
        File.WriteAllText(Path.Combine(output,"mapping-plan.json"),json);
        foreach(var entry in plan)
        {
            var id=SqlServerAgreements.Save(conn,tx,entry.Legal);
            foreach(var source in entry.Documents)
            {
                using var cmd=new SqlCommand("UPDATE dbo.contracts SET agreement_id=@agreement WHERE id=@id AND object_id=@object AND agreement_id IS NULL; IF @@ROWCOUNT<>1 THROW 51240,'Document changed during backfill.',1;"+
                    "INSERT dbo.agreement_migration_links(document_id,agreement_id,plan_sha256,source_sha256,approved_by) VALUES(@id,@agreement,@plan,@source,@who)",conn,tx);
                cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=source.Id;
                cmd.Parameters.Add("@object",SqlDbType.BigInt).Value=source.ObjectId;
                cmd.Parameters.Add("@agreement",SqlDbType.BigInt).Value=id;
                cmd.Parameters.Add("@plan",SqlDbType.Binary,32).Value=digest;
                cmd.Parameters.Add("@source",SqlDbType.Binary,32).Value=SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(source));
                cmd.Parameters.Add("@who",SqlDbType.NVarChar,200).Value=Environment.UserName+" / owner approved 2026-11-27";
                cmd.ExecuteNonQuery();
            }
        }
        using var check=new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.contracts WHERE kind IN(N'main',N'dop') AND agreement_id IS NULL",conn,tx);
        if(Convert.ToInt64(check.ExecuteScalar())!=0)throw new InvalidOperationException("Unmapped legal documents remain.");
        Console.WriteLine($"Backfill: {plan.Count} agreements, {plan.Sum(e=>e.Documents.Count)} original documents; {plan.Count(e=>e.Status.StartsWith("DRAFT"))} drafts with no invented legal dates.");
    }
}
