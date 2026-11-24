using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerWorkspace
{
    public sealed record TransferDocument(long Id,long ObjectId,string Kind,long? StageOf);
    public sealed record ContractTransferSnapshot(long SourceId,string SourceName,string Revision,IReadOnlyList<long> DocumentIds);

    private static List<TransferDocument> ReadTransferGroup(SqlConnection conn,SqlTransaction tx,long contractId)
    {
        using var cmd=new SqlCommand(
            "SELECT id,object_id,kind,stage_of FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) "+
            "WHERE id=@id OR base_contract_id=@id ORDER BY CASE WHEN id=@id THEN 0 ELSE 1 END,[order],id",conn,tx);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
        using var r=cmd.ExecuteReader();
        var result=new List<TransferDocument>();
        while(r.Read()) result.Add(new(Convert.ToInt64(r.GetValue(0)),Convert.ToInt64(r.GetValue(1)),
            r.GetString(2),r.IsDBNull(3)?null:Convert.ToInt64(r.GetValue(3))));
        ValidateTransferGroup(contractId,result);
        return result;
    }

    public static void ValidateTransferGroup(long contractId,IReadOnlyList<TransferDocument> group)
    {
        if(group.Count==0||group[0].Id!=contractId||group[0].Kind!="main")
            throw new InvalidOperationException("Договор больше не доступен для переноса.");
        if(group.Select(d=>d.Id).Distinct().Count()!=group.Count||
            group.Skip(1).Any(d=>d.Kind!="dop"||d.ObjectId!=group[0].ObjectId))
            throw new InvalidOperationException("У договора есть некорректные связи с другими документами.");
        var ids=group.Select(d=>d.Id).ToHashSet();
        if(group.Any(d=>d.StageOf is long id&&!ids.Contains(id)))
            throw new InvalidOperationException("Договор связан с этапами другого документа.");
    }

    public static ContractTransferSnapshot DescribeContractTransfer(string cs,long contractId)
    {
        RequirePilotDatabase(cs);
        using var conn=SqlServerDatabase.Open(cs);
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        using var cmd=new SqlCommand("SELECT [value] FROM dbo.meta WITH (UPDLOCK,HOLDLOCK) WHERE [key]=N'rev'",conn,tx);
        var revision=Convert.ToString(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
        if(string.IsNullOrWhiteSpace(revision)) throw new InvalidOperationException("Нет ревизии базы.");
        var group=ReadTransferGroup(conn,tx,contractId);
        var source=ReadObjectName(conn,tx,group[0].ObjectId);
        if(source.Archived) throw new InvalidOperationException("Архивный объект нельзя изменять.");
        tx.Commit();
        return new(group[0].ObjectId,source.Name,revision,group.Select(d=>d.Id).ToArray());
    }

    public static void TransferContract(string cs,long contractId,long sourceId,long targetId,
        IReadOnlyList<long> expectedIds,string expectedRevision)
    {
        RequirePilotDatabase(cs);
        if(sourceId==targetId||targetId<=0) throw new ArgumentException("Выберите другой действующий объект.");
        using var conn=SqlServerDatabase.Open(cs);
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn,tx,expectedRevision);
        var group=ReadTransferGroup(conn,tx,contractId);
        var ids=group.Select(d=>d.Id).ToHashSet();
        if(group[0].ObjectId!=sourceId||!ids.SetEquals(expectedIds))
            throw new DBConcurrencyException("Объект или состав документов изменился. Откройте перенос заново.");
        var source=ReadObjectName(conn,tx,sourceId);
        var target=ReadObjectName(conn,tx,targetId);
        if(source.Archived||target.Archived) throw new InvalidOperationException("Выберите другой действующий объект.");
        // Query each ID rather than creating an unbounded IN parameter list.
        foreach(var id in ids)
        {
            using(var stages=new SqlCommand("SELECT id FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE stage_of=@id",conn,tx))
            {
                stages.Parameters.Add("@id",SqlDbType.BigInt).Value=id;
                using var r=stages.ExecuteReader();
                while(r.Read()) if(!ids.Contains(Convert.ToInt64(r.GetValue(0))))
                    throw new InvalidOperationException("Перенос разорвёт группу этапов.");
            }
            using var shipments=new SqlCommand(
                "SELECT TOP(1) sl.id FROM dbo.shipment_lines sl WITH (UPDLOCK,HOLDLOCK) "+
                "JOIN dbo.positions p ON p.id=sl.position_id JOIN dbo.systems s ON s.id=p.system_id WHERE s.contract_id=@id",conn,tx);
            shipments.Parameters.Add("@id",SqlDbType.BigInt).Value=id;
            if(shipments.ExecuteScalar() is not null)
                throw new InvalidOperationException("Позиции входят в ведомости выдачи. Перенос недоступен.");
        }
        using var max=new SqlCommand("SELECT COALESCE(MAX([order]),0) FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE object_id=@target",conn,tx);
        max.Parameters.Add("@target",SqlDbType.BigInt).Value=targetId;
        var order=Convert.ToInt32(max.ExecuteScalar());
        foreach(var doc in group)
        {
            using var update=new SqlCommand("UPDATE dbo.contracts SET object_id=@target,[order]=@order WHERE id=@id AND object_id=@source",conn,tx);
            update.Parameters.Add("@target",SqlDbType.BigInt).Value=targetId;
            update.Parameters.Add("@source",SqlDbType.BigInt).Value=sourceId;
            update.Parameters.Add("@id",SqlDbType.BigInt).Value=doc.Id;
            update.Parameters.Add("@order",SqlDbType.Int).Value=checked(++order);
            if(update.ExecuteNonQuery()!=1) throw new DBConcurrencyException("Документ изменился при переносе.");
            Log(conn,tx,"contract",doc.Id,"move",$"Из «{source.Name}» в «{target.Name}»",doc.Id,targetId);
        }
        BumpRevision(conn,tx);
        tx.Commit();
    }
}
