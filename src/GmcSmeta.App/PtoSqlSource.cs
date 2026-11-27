using System.Data;
using System.Globalization;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

/// <summary>Read-only DTO snapshots of objects/contracts/positions/KS; never updates financial data.</summary>
public sealed class PtoSqlSource
{
    private readonly string _connection;
    public PtoSqlSource(string connection) {_connection=connection;}
    public sealed record ObjectChoice(long Id,string Name,string Address,string Customer)
    {
        public override string ToString()=>Name;
    }
    public sealed record ContractChoice(long Id,long ObjectId,string Number,string Date,
        string Name,string Kind,string Contractor)
    {
        public override string ToString()=> (Kind=="dop"?"ДС ":"Договор ")+"№"+Number+
            (string.IsNullOrWhiteSpace(Name)?"":" · "+Name);
    }
    public sealed record KsChoice(long Id,string Number,string Date,string Revision)
    {
        public override string ToString()=>"КС № "+Number+" от "+Date;
    }

    public IReadOnlyList<ObjectChoice> Objects()
    {
        using var conn=SqlServerDatabase.Open(_connection);
        using var cmd=new SqlCommand("SELECT id,name,address,customer FROM dbo.objects ORDER BY name,id",conn);
        var result=new List<ObjectChoice>();
        using var r=cmd.ExecuteReader();
        string S(int i)=>r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
        while(r.Read())result.Add(new(SqlInteger.ReadInt64(r, 0),S(1),S(2),S(3)));
        return result;
    }
    public IReadOnlyList<ContractChoice> Contracts(long? objectId=null)
    {
        using var conn=SqlServerDatabase.Open(_connection);
        using var cmd=new SqlCommand(
            "SELECT c.id,c.object_id,c.number,c.[date],c.name,c.kind,co.legal_full "+
            "FROM dbo.contracts c LEFT JOIN dbo.companies co ON co.id=c.company_id "+
            "WHERE c.kind IN(N'main',N'dop') AND (@obj IS NULL OR c.object_id=@obj) "+
            "ORDER BY c.object_id,c.id",conn);
        cmd.Parameters.Add("@obj",SqlDbType.BigInt).Value=objectId.HasValue?objectId.Value:DBNull.Value;
        var result=new List<ContractChoice>();
        using var r=cmd.ExecuteReader();
        string S(int i)=>r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
        while(r.Read())result.Add(new(SqlInteger.ReadInt64(r, 0),SqlInteger.ReadInt64(r, 1),S(2),S(3),S(4),S(5),S(6)));
        return result;
    }
    public PtoPackage Snapshot(ObjectChoice obj,ContractChoice? contract=null,
        IReadOnlyDictionary<string,string>? defaults=null)
    {
        if(contract is not null && contract.ObjectId!=obj.Id)
            throw new InvalidOperationException("Договор относится к другому объекту.");
        var p=PtoRules.New(obj.Id,obj.Name,obj.Address,
            contract?.Id,contract?.ToString()??"Документы объекта",
            contract?.Number??"",contract?.Date??"");
        p.Fields["customer"]=obj.Customer;
        if(contract is not null)
        {
            p.Fields["contractor"]=contract.Contractor;
            p.Fields["executor"]=contract.Contractor;
            using var conn=SqlServerDatabase.Open(_connection);
            using var cmd=new SqlCommand(
                "SELECT sy.name,p.name,p.unit,p.qty FROM dbo.positions p "+
                "JOIN dbo.systems sy ON sy.id=p.system_id "+
                "WHERE sy.contract_id=@id AND ISNULL(p.out_of_kp,0)=0 "+
                "ORDER BY sy.[order],sy.id,p.[order],p.id",conn);
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=contract.Id;
            using var r=cmd.ExecuteReader();
            string S(int i)=>r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
            while(r.Read())
                p.Positions.Add(new PtoWork {System=S(0),Name=S(1),Unit=S(2),Qty=S(3)});
        }
        if(defaults is not null)
            foreach(var (key,value) in defaults)
                if(PtoRules.Fields.ContainsKey(key) && string.IsNullOrWhiteSpace(PtoRules.Text(p,key)))
                    p.Fields[key]=value;
        return p;
    }
    public IReadOnlyList<KsChoice> Ks(long contractId)
    {
        using var conn=SqlServerDatabase.Open(_connection);
        for(var attempt=0;attempt<2;attempt++)
        {
            var revision=SqlServerDatabase.Revision(conn);
            using var cmd=new SqlCommand(
                "SELECT id,number,CONVERT(nvarchar(80),[date]) FROM dbo.acts "+
                "WHERE contract_id=@id ORDER BY [date],id",conn);
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            var result=new List<KsChoice>();
            using(var r=cmd.ExecuteReader())
                while(r.Read())
                    result.Add(new KsChoice(SqlInteger.ReadInt64(r, 0),r.IsDBNull(1)?"":r.GetString(1),
                        r.IsDBNull(2)?"":r.GetString(2),revision));
            if(!string.IsNullOrWhiteSpace(revision)&&revision==SqlServerDatabase.Revision(conn))
                return result;
        }
        throw new InvalidOperationException("Список КС изменился. Повторите выбор акта.");
    }

    /// <summary>Raw positive work quantities and the single-act workbook source
    /// must describe the same revision. Never substitute a KS-3 group for this act.</summary>
    public (IReadOnlyList<PtoWork> Rows,SqlServerKsPrintData.Package Print) ReadKsVor(
        long contractId,KsChoice choice)
    {
        using var conn=SqlServerDatabase.Open(_connection);
        RequireKsRevision(conn,choice.Revision);
        var rows=KsWorks(conn,contractId,choice.Id);
        if(rows.Count==0)
            throw new InvalidOperationException("В КС нет выполненных объёмов.");
        var print=SqlServerKsPrintData.ReadSingle(_connection,contractId,choice.Id,choice.Revision);
        RequireKsRevision(conn,choice.Revision);
        return (rows,print);
    }

    // Recheck after rendering, before adding the generated file to the PTO archive.
    public void RequireKsRevision(string expectedRevision)
    {
        using var conn=SqlServerDatabase.Open(_connection);
        RequireKsRevision(conn,expectedRevision);
    }

    private static void RequireKsRevision(SqlConnection conn,string expectedRevision)
    {
        if(string.IsNullOrWhiteSpace(expectedRevision)||expectedRevision!=SqlServerDatabase.Revision(conn))
            throw new InvalidOperationException("КС изменились во время подготовки ВОР. Повторите выбор акта.");
    }

    private static IReadOnlyList<PtoWork> KsWorks(SqlConnection conn,long contractId,long actId)
    {
        using var check=new SqlCommand(
            "SELECT COUNT_BIG(1) FROM dbo.acts WHERE id=@act AND contract_id=@contract",conn);
        check.Parameters.AddWithValue("@act",actId);
        check.Parameters.AddWithValue("@contract",contractId);
        if(Convert.ToInt64(check.ExecuteScalar())!=1)
            throw new InvalidOperationException("КС не принадлежит выбранному договору.");
        using var cmd=new SqlCommand(
            "SELECT sy.name,p.name,p.unit,al.qty,p.id "+
            "FROM dbo.act_lines al JOIN dbo.positions p ON p.id=al.position_id "+
            "JOIN dbo.systems sy ON sy.id=p.system_id JOIN dbo.acts a ON a.id=al.act_id "+
            "WHERE al.act_id=@act AND a.contract_id=@contract AND sy.contract_id=a.contract_id "+
            "AND al.qty>0 ORDER BY sy.[order],p.[order],p.id",conn);
        cmd.Parameters.AddWithValue("@act",actId);
        cmd.Parameters.AddWithValue("@contract",contractId);
        var result=new List<PtoWork>();
        using var r=cmd.ExecuteReader();
        string S(int i)=>r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
        while(r.Read())
            result.Add(new PtoWork
            {
                System=S(0),Name=S(1),Unit=S(2),Qty=S(3),
                SourceActId=actId,SourcePositionId=SqlInteger.ReadInt64(r, 4)
            });
        return result;
    }
}
