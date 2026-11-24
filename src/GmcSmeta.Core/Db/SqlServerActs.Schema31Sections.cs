using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerActs
{
    public sealed record Schema31Sections(
        long ActId,long ContractId,string Revision,string SmrMode,bool Signed,bool Locked,
        bool Supported,decimal Smr,decimal Transport,bool SmrBySystem,string SavedJson,
        IReadOnlyList<KsSectionEditPolicy.SystemBudget> Systems,
        KsSectionEditPolicy.Limits Remaining,KsActAllocation.SectionAmounts Current,
        KsActAllocation.SectionAmounts Suggested);

    // Both preview and save get the complete input set within the same serializable
    // transaction holding meta.rev. The test connection is deliberately inaccessible
    // to normal WPF writers. No data is read from a different contract.
    private static Schema31Sections ReadSchema31Sections(SqlConnection conn,SqlTransaction tx,
        long actId,string rev)
    {
        long contractId;
        string mode,form,name,rules,json;
        bool signed,locked,bySystem;
        decimal smr,transport,budgetSmr,budgetTransport,smrPercent;
        static long Id(SqlDataReader r,int i)=>Convert.ToInt64(r.GetValue(i),CultureInfo.InvariantCulture);
        static string S(SqlDataReader r,int i)=>r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
        // Do not round FLOAT quantities/prices to six digits before computing budgets.
        static decimal D(SqlDataReader r,int i)=>r.IsDBNull(i)?0m:
            decimal.Parse(Convert.ToDouble(r.GetValue(i),CultureInfo.InvariantCulture)
                .ToString("R",CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture);
        static bool B(SqlDataReader r,int i)=>!r.IsDBNull(i)&&Convert.ToBoolean(r.GetValue(i),CultureInfo.InvariantCulture);
        using(var cmd=new SqlCommand("""
            SELECT a.contract_id,a.signed,c.locked,c.smr_mode,c.form,o.name,t.rules,
                a.smr_amount,a.transport_amount,a.smr_by_system,a.section_amounts_json,
                c.smr_amount,c.transport_amount,c.smr_percent
            FROM dbo.acts a WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.contracts c ON c.id=a.contract_id
            JOIN dbo.objects o ON o.id=c.object_id
            LEFT JOIN dbo.tags t ON t.[key]=c.form WHERE a.id=@id
            """,conn,tx))
        {
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            using var r=cmd.ExecuteReader();
            if(!r.Read()) throw new InvalidOperationException("Акт не найден.");
            contractId=Id(r,0); signed=B(r,1); locked=B(r,2);
            mode=S(r,3); form=S(r,4); name=S(r,5); rules=S(r,6);
            smr=D(r,7); transport=D(r,8); bySystem=B(r,9); json=S(r,10);
            budgetSmr=D(r,11); budgetTransport=D(r,12); smrPercent=D(r,13);
        }
        var supported=DiscountPolicy.SupportsDiscount(
            KsFinancialPolicy.HasRule(rules,"vat_top"),KsFinancialPolicy.HasRule(rules,"ks_dns"),form,name);
        var roundRows=KsFinancialPolicy.HasRule(rules,"round_rows");
        var systems=new List<KsSectionEditPolicy.SystemBudget>();
        using(var cmd=new SqlCommand("""
            SELECT id,parent_id,smr_amount FROM dbo.systems WHERE contract_id=@cid ORDER BY [order],id
            """,conn,tx))
        {
            cmd.Parameters.Add("@cid",SqlDbType.BigInt).Value=contractId;
            using var r=cmd.ExecuteReader();
            while(r.Read()) systems.Add(new(Id(r,0),r.IsDBNull(1)?null:Id(r,1),D(r,2)));
        }
        var quantities=new List<KsSectionEditPolicy.Quantity>();
        decimal materials=0m,works=0m;
        using(var cmd=new SqlCommand("""
            SELECT p.system_id,p.qty,p.price,p.work_price,p.out_of_kp,ISNULL(closed.qty,0)
            FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id
            OUTER APPLY (SELECT SUM(al.qty) qty FROM dbo.act_lines al
                WHERE al.act_id=@id AND al.position_id=p.id) closed
            WHERE sy.contract_id=@cid AND p.parent_id IS NULL
            ORDER BY sy.[order],sy.id,p.[order],p.id
            """,conn,tx))
        {
            cmd.Parameters.Add("@cid",SqlDbType.BigInt).Value=contractId;
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            using var r=cmd.ExecuteReader();
            while(r.Read())
            {
                var qty=D(r,1); var price=D(r,2); var outside=B(r,4);
                quantities.Add(new(Id(r,0),qty,D(r,5),price,outside));
                if(outside) continue;
                materials+=roundRows ? Money.Round2(qty*price) : qty*price;
                works+=qty*D(r,3);
            }
        }
        budgetSmr=mode switch
        {
            "systems"=>decimal.Round(systems.Sum(s=>s.Smr),2,MidpointRounding.ToEven),
            "percent"=>decimal.Round(decimal.Round(materials,2,MidpointRounding.ToEven)*smrPercent/100m,2,MidpointRounding.ToEven),
            "pos"=>decimal.Round(works,2,MidpointRounding.ToEven),
            _=>budgetSmr
        };
        var lineMap=new Dictionary<long,List<KsActAllocation.Line>>();
        using(var cmd=new SqlCommand("""
            SELECT al.act_id,p.system_id,al.qty,al.price,al.work_price
            FROM dbo.act_lines al JOIN dbo.acts a ON a.id=al.act_id
            JOIN dbo.positions p ON p.id=al.position_id
            JOIN dbo.systems sy ON sy.id=p.system_id
            WHERE a.contract_id=@cid AND sy.contract_id=@cid
            ORDER BY al.act_id,al.id
            """,conn,tx))
        {
            cmd.Parameters.Add("@cid",SqlDbType.BigInt).Value=contractId;
            using var r=cmd.ExecuteReader();
            while(r.Read())
            {
                var id=Id(r,0);
                if(!lineMap.TryGetValue(id,out var list)) lineMap[id]=list=new();
                list.Add(new(Id(r,1),D(r,2),D(r,3),D(r,4)));
            }
        }
        var previous=new List<KsSectionEditPolicy.PriorAct>();
        using(var cmd=new SqlCommand("""
            SELECT id,smr_amount,transport_amount,smr_by_system,section_amounts_json
            FROM dbo.acts WHERE contract_id=@cid AND id<>@id ORDER BY id
            """,conn,tx))
        {
            cmd.Parameters.Add("@cid",SqlDbType.BigInt).Value=contractId;
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            using var r=cmd.ExecuteReader();
            while(r.Read()) previous.Add(new(D(r,1),D(r,2),B(r,3),S(r,4),
                lineMap.GetValueOrDefault(Id(r,0))??new()));
        }
        var limits=KsSectionEditPolicy.Remaining(systems,previous,mode,budgetSmr,budgetTransport,roundRows);
        var current=KsActAllocation.Resolve(lineMap.GetValueOrDefault(actId)??new(),
            systems.Select(s=>s.Id).ToHashSet(),smr,transport,mode=="pos",bySystem,json,roundRows);
        var suggested=KsSectionEditPolicy.Suggest(systems,quantities,mode,budgetSmr,budgetTransport,
            limits.Smr,limits.Transport,limits.BySystem);
        return new(actId,contractId,rev,mode,signed,locked,supported,smr,transport,bySystem,json,
            systems,limits,current,suggested);
    }

    public static Schema31Sections ReadSectionsSchema31Test(string connectionString,long actId)
    {
        using var conn=OpenSchema31WriteTest(connectionString);
        return ReadSectionsCore(conn,actId);
    }

    public static Schema31Sections ReadSections(string connectionString,long actId)
    {
        if(!SqlSchemaCompatibility.OrdinarySchema31Enabled)
            throw new InvalidOperationException("Штатная запись разбивки v31 пока не разрешена.");
        RequirePilotWrite(connectionString);
        using var conn=SqlServerDatabase.Open(connectionString);
        return ReadSectionsCore(conn,actId);
    }

    private static Schema31Sections ReadSectionsCore(SqlConnection conn,long actId)
    {
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        var rev=SqlServerDatabase.LockRevision(conn,tx)
            ??throw new InvalidOperationException("Нет meta.rev.");
        var snapshot=ReadSchema31Sections(conn,tx,actId,rev);
        tx.Rollback(); // this method is read-only
        return snapshot;
    }

    /// <summary>Atomic test-only write. Uses fresh limits, including all other
    /// draft/signed acts, under meta.rev lock. In pos mode existing work sums
    /// and flags are left untouched. Discount fields are never overwritten.</summary>
    public static SqlServerDatabase.SaveResult SaveSectionsSchema31Test(string connectionString,
        long actId,KsActAllocation.SectionAmounts amounts,string expectedRevision)
    {
        using var conn=OpenSchema31WriteTest(connectionString);
        return SaveSectionsCore(conn,actId,amounts,expectedRevision);
    }

    public static SqlServerDatabase.SaveResult SaveSections(string connectionString,long actId,
        KsActAllocation.SectionAmounts amounts,string expectedRevision)
    {
        if(!SqlSchemaCompatibility.OrdinarySchema31Enabled)
            return SqlServerDatabase.SaveResult.Fail("Штатная запись разбивки v31 пока не разрешена.",expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn=SqlServerDatabase.Open(connectionString);
        return SaveSectionsCore(conn,actId,amounts,expectedRevision);
    }

    private static SqlServerDatabase.SaveResult SaveSectionsCore(SqlConnection conn,long actId,
        KsActAllocation.SectionAmounts amounts,string expectedRevision)
    {
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        var rev=SqlServerDatabase.LockRevision(conn,tx);
        if(rev is null||!string.Equals(rev,expectedRevision,StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены. Обновите КС.",rev??expectedRevision);
        var snapshot=ReadSchema31Sections(conn,tx,actId,rev);
        if(snapshot.Signed||snapshot.Locked)
            return SqlServerDatabase.SaveResult.Fail("Подписанный акт или заблокированный договор нельзя изменять.",rev);
        if(!snapshot.Supported)
            return SqlServerDatabase.SaveResult.Fail("Ручная разбивка не поддерживается этой формой КС.",rev);
        var perPosition=snapshot.SmrMode=="pos";
        KsSectionEditPolicy.Validated validated;
        try
        {
            validated=KsSectionEditPolicy.Validate(amounts,snapshot.Systems.Select(s=>s.Id).ToHashSet(),
                perPosition,snapshot.Remaining.Smr,snapshot.Remaining.Transport,snapshot.Remaining.BySystem);
        }
        catch(ArgumentException ex) {return SqlServerDatabase.SaveResult.Fail(ex.Message,rev);}
        using(var cmd=new SqlCommand("""
            UPDATE dbo.acts SET
                smr_amount=CASE WHEN @pos=1 THEN smr_amount ELSE @smr END,
                smr_by_system=CASE WHEN @pos=1 THEN smr_by_system ELSE CONVERT(bit,1) END,
                transport_amount=@transport,section_amounts_json=@json
            WHERE id=@id AND contract_id=@cid AND signed=0
            """,conn,tx))
        {
            cmd.Parameters.Add("@pos",SqlDbType.Bit).Value=perPosition;
            cmd.Parameters.Add("@smr",SqlDbType.Float).Value=(double)validated.Smr;
            cmd.Parameters.Add("@transport",SqlDbType.Float).Value=(double)validated.Transport;
            cmd.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value=validated.Json;
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            cmd.Parameters.Add("@cid",SqlDbType.BigInt).Value=snapshot.ContractId;
            if(cmd.ExecuteNonQuery()!=1)
                return SqlServerDatabase.SaveResult.Fail("Акт изменён или удалён.",rev);
        }
        using(var log=new SqlCommand("""
            INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id)
            VALUES (@ts,@user,N'act',@id,N'edit',@details,@cid)
            """,conn,tx))
        {
            log.Parameters.AddWithValue("@ts",DateTime.Now);
            log.Parameters.AddWithValue("@user",ChangeLog.CurrentUser);
            log.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            log.Parameters.Add("@cid",SqlDbType.BigInt).Value=snapshot.ContractId;
            log.Parameters.Add("@details",SqlDbType.NVarChar,-1).Value=
                $"Разбивка КС: {snapshot.SavedJson} → {validated.Json}";
            log.ExecuteNonQuery();
        }
        var updated=SqlServerDatabase.BumpPositionRevision(conn,tx);
        tx.Commit(); // do not retry an unknown COMMIT outcome
        return new SqlServerDatabase.SaveResult(true,null,updated);
    }
}
