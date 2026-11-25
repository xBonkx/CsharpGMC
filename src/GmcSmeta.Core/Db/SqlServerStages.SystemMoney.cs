using System.Data;
using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerStages
{
    // The caller already holds the revision lock and owns the price transaction.
    internal static void RefreshLinkedPrices(SqlConnection conn, SqlTransaction tx, long fullId,
        IReadOnlyCollection<long> sourceIds)
    {
        if (sourceIds.Count == 0) return;
        using var sync = new SqlCommand("""
            SET NOCOUNT ON;
            DECLARE @stages TABLE (id BIGINT PRIMARY KEY);
            INSERT @stages (id)
            SELECT stage.id
            FROM dbo.contracts stage
            JOIN dbo.contracts full_kp ON full_kp.id=stage.stage_of AND full_kp.object_id=stage.object_id
            JOIN dbo.objects o ON o.id=full_kp.object_id
            WHERE full_kp.id=@full AND full_kp.kind=N'kp' AND full_kp.stage_of IS NULL
              AND full_kp.locked=0 AND o.archived=0 AND stage.kind=N'kp'
              AND stage.stage_sum IS NULL
              AND (stage.stage_rest=0 AND stage.locked=0 OR stage.stage_rest=1 AND
                   NOT EXISTS (SELECT 1 FROM dbo.acts a WHERE a.contract_id=stage.id));

            IF EXISTS (SELECT 1 FROM @stages)
            BEGIN
                DECLARE @changed TABLE (position_id BIGINT PRIMARY KEY, stage_id BIGINT, price FLOAT);
                UPDATE p SET price=src.price
                OUTPUT inserted.id, sy.contract_id, inserted.price INTO @changed
                FROM dbo.positions p
                JOIN dbo.systems sy ON sy.id=p.system_id
                JOIN @stages stage ON stage.id=sy.contract_id
                JOIN dbo.positions src ON src.id=p.stage_src AND src.stage_src IS NULL
                JOIN dbo.systems src_sy ON src_sy.id=src.system_id AND src_sy.contract_id=@full
                JOIN OPENJSON(@ids) changes ON TRY_CONVERT(BIGINT, changes.[value])=src.id
                WHERE p.price<>src.price;

                UPDATE line SET price=p.price
                FROM dbo.act_lines line
                JOIN @changed p ON p.position_id=line.position_id
                JOIN dbo.acts a ON a.id=line.act_id AND a.contract_id=p.stage_id
                WHERE a.signed=0;
            END;

            SELECT id FROM @stages ORDER BY id;
            """, conn, tx);
        sync.Parameters.Add("@full", SqlDbType.BigInt).Value = fullId;
        sync.Parameters.Add("@ids", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(sourceIds);
        var stages = new List<long>();
        using (var reader = sync.ExecuteReader())
            while (reader.Read()) stages.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        foreach (var stageId in stages)
            RefreshMoney(conn, tx, fullId, stageId);
    }

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
