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
    public sealed record KsChoice(long Id,string Number,string Date,string Revision)
    {
        public override string ToString()=>"КС № "+Number+" от "+Date;
    }

    public IReadOnlyList<ObjectChoice> Objects()
    {
        using var conn=SqlServerDatabase.Open(_connection);
        return ReadObjects(conn);
    }

    private static IReadOnlyList<ObjectChoice> ReadObjects(SqlConnection conn,long? objectId=null)
    {
        using var cmd=new SqlCommand("SELECT id,name,address,customer FROM dbo.objects "+
            "WHERE @id IS NULL OR id=@id ORDER BY name,id",conn);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=objectId.HasValue?objectId.Value:DBNull.Value;
        var result=new List<ObjectChoice>();
        using var r=cmd.ExecuteReader();
        string S(int i)=>r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
        while(r.Read())result.Add(new(SqlInteger.ReadInt64(r, 0),S(1),S(2),S(3)));
        return result;
    }
    public IReadOnlyList<PtoContractContext> Contracts(long? objectId=null)
    {
        using var conn=SqlServerDatabase.Open(_connection);
        for(var attempt=0;attempt<2;attempt++)
        {
            var revision=SqlServerDatabase.Revision(conn);
            var result=SqlServerPtoContractContext.Read(conn,objectId);
            if(!string.IsNullOrWhiteSpace(revision)&&revision==SqlServerDatabase.Revision(conn))
                return result;
        }
        throw new InvalidOperationException("Список приложений изменился. Повторите выбор.");
    }

    public PtoPackage Snapshot(ObjectChoice obj,PtoContractContext? contract=null,
        IReadOnlyDictionary<string,string>? defaults=null)
    {
        if(contract is not null && contract.ObjectId!=obj.Id)
            throw new InvalidOperationException("Приложение относится к другому объекту.");
        using var conn=SqlServerDatabase.Open(_connection);
        for(var attempt=0;attempt<2;attempt++)
        {
            var before=SqlServerDatabase.Revision(conn);
            var current=ReadObjects(conn,obj.Id).SingleOrDefault()
                ??throw new InvalidOperationException("Объект больше не доступен.");
            PtoPackage p;
            if(contract is null)
            {
                p=PtoRules.New(current.Id,current.Name,current.Address,contractLabel:"Документы объекта");
                p.Fields["customer"]=current.Customer;
                PtoContractContext.ApplyDefaults(p,defaults);
            }
            else
            {
                var document=SqlServerPtoContractContext.Read(conn,obj.Id).SingleOrDefault(c=>c.Id==contract.Id)
                    ??throw new InvalidOperationException("Приложение больше не доступно в выбранном объекте.");
                if(document.LegalRequired!=contract.LegalRequired || document.Agreement?.Id!=contract.Agreement?.Id)
                    throw new InvalidOperationException("Юридическая привязка приложения изменилась. Выберите источник объёмов заново.");
                p=document.NewPackage(current.Id,current.Name,current.Address,current.Customer,defaults);
                using var cmd=new SqlCommand(
                    "SELECT sy.name,p.name,p.unit,p.qty FROM dbo.positions p "+
                    "JOIN dbo.systems sy ON sy.id=p.system_id "+
                    "WHERE sy.contract_id=@id AND ISNULL(p.out_of_kp,0)=0 "+
                    "ORDER BY sy.[order],sy.id,p.[order],p.id",conn);
                cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=document.Id;
                using var r=cmd.ExecuteReader();
                string S(int i)=>r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
                while(r.Read())
                    p.Positions.Add(new PtoWork {System=S(0),Name=S(1),Unit=S(2),Qty=S(3)});
            }
            if(!string.IsNullOrWhiteSpace(before)&&before==SqlServerDatabase.Revision(conn))
                return p;
        }
        throw new InvalidOperationException("Реквизиты или объёмы изменились во время создания ПТО. Повторите операцию.");
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
