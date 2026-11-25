using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerStages
{
    // Called only after RefreshMoney has checked signed targets and computed rest.
    // Batch reads for the full document and the stage; never touch a different
    // document, locally created systems, or a signed stage.
    private static void RefreshSystemMoney(SqlConnection conn,SqlTransaction tx,long fullId,long stageId)
    {
        var sources=new List<StageSystemMoneyPolicy.Source>();
        var destinations=new List<StageSystemMoneyPolicy.Destination>();
        long Id(SqlDataReader r,int i)=>Convert.ToInt64(r.GetValue(i),CultureInfo.InvariantCulture);
        decimal D(SqlDataReader r,int i)=>r.IsDBNull(i)?0m:
            decimal.Parse(Convert.ToDouble(r.GetValue(i),CultureInfo.InvariantCulture).ToString("R",CultureInfo.InvariantCulture),
                NumberStyles.Float,CultureInfo.InvariantCulture);
        using(var cmd=new SqlCommand("""
            SELECT id,contract_id,stage_sys_src,smr_amount
            FROM dbo.systems WHERE contract_id IN (@full,@stage)
            """,conn,tx))
        {
            cmd.Parameters.Add("@full",SqlDbType.BigInt).Value=fullId;
            cmd.Parameters.Add("@stage",SqlDbType.BigInt).Value=stageId;
            using var reader=cmd.ExecuteReader();
            while(reader.Read())
                if(Id(reader,1)==fullId)sources.Add(new(Id(reader,0),D(reader,3)));
                else destinations.Add(new(Id(reader,0),reader.IsDBNull(2)?null:Id(reader,2)));
        }
        var rows=new List<StageSystemMoneyPolicy.Row>();
        using(var cmd=new SqlCommand("""
            SELECT sy.id,p.qty,p.price,t.rules
            FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id
            JOIN dbo.contracts c ON c.id=sy.contract_id
            LEFT JOIN dbo.tags t ON t.[key]=c.form
            WHERE sy.contract_id IN (@full,@stage) AND p.parent_id IS NULL AND p.out_of_kp=0
            """,conn,tx))
        {
            cmd.Parameters.Add("@full",SqlDbType.BigInt).Value=fullId;
            cmd.Parameters.Add("@stage",SqlDbType.BigInt).Value=stageId;
            using var reader=cmd.ExecuteReader();
            while(reader.Read()) rows.Add(new(Id(reader,0),D(reader,1),D(reader,2),
                KsFinancialPolicy.HasRule(reader.IsDBNull(3)?null:reader.GetString(3),"round_rows")));
        }
        var amounts=StageSystemMoneyPolicy.Calculate(sources,destinations,rows);
        using var update=new SqlCommand("UPDATE dbo.systems SET smr_amount=@amount WHERE id=@id AND contract_id=@stage",conn,tx);
        update.Parameters.Add("@stage",SqlDbType.BigInt).Value=stageId;
        update.Parameters.Add("@id",SqlDbType.BigInt);
        update.Parameters.Add("@amount",SqlDbType.Float);
        foreach(var (id,amount) in amounts)
        {
            update.Parameters["@id"].Value=id;
            update.Parameters["@amount"].Value=(double)amount;
            if(update.ExecuteNonQuery()!=1)throw new DBConcurrencyException("Раздел этапа изменился.");
        }
    }
}
