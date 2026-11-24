using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerActs
{
    /// <summary>Test-only contract discount update. Signed acts, explicit 0% overrides
    /// and manual totals (including zero) are immutable. Never changes position prices.
    /// All affected rows, the audit entry and revision are one transaction.</summary>
    public static SqlServerDatabase.SaveResult SaveContractDiscountSchema31Test(
        string connectionString,long contractId,decimal percent,string expectedRevision)
    {
        using var conn=OpenSchema31WriteTest(connectionString);
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        var rev=SqlServerDatabase.LockRevision(conn,tx);
        if(rev is null||!string.Equals(rev,expectedRevision,StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены. Обновите договор.",rev??expectedRevision);
        if(percent<0m||percent>100m)
            return SqlServerDatabase.SaveResult.Fail("Скидка должна быть от 0 до 100%.",rev);
        decimal old;
        bool supported;
        using(var read=new SqlCommand("""
            SELECT c.locked,c.stage_of,c.discount_percent,c.form,o.name,t.rules
            FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.objects o ON o.id=c.object_id
            LEFT JOIN dbo.tags t ON t.[key]=c.form WHERE c.id=@id
            """,conn,tx))
        {
            read.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            using var r=read.ExecuteReader();
            if(!r.Read()) return SqlServerDatabase.SaveResult.Fail("Договор не найден.",rev);
            if(Convert.ToBoolean(r.GetValue(0),CultureInfo.InvariantCulture)||!r.IsDBNull(1))
                return SqlServerDatabase.SaveResult.Fail("Подписанный договор или отдельный этап нельзя изменять.",rev);
            old=DiscountPolicy.ReadPercent(r,2);
            var form=r.IsDBNull(3)?"":r.GetString(3);
            var name=r.IsDBNull(4)?"":r.GetString(4);
            var rules=r.IsDBNull(5)?"":r.GetString(5);
            supported=DiscountPolicy.SupportsDiscount(
                KsFinancialPolicy.HasRule(rules,"vat_top"),KsFinancialPolicy.HasRule(rules,"ks_dns"),form,name);
        }
        if(!supported)
        {
            using var check=new SqlCommand("""
                SELECT COUNT_BIG(*) FROM dbo.acts WHERE contract_id=@id AND
                (discount_override=1 OR discount_percent<>0 OR manual_total_cents>=0)
                """,conn,tx);
            check.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            if(percent!=0m||Convert.ToInt64(check.ExecuteScalar(),CultureInfo.InvariantCulture)>0)
                return SqlServerDatabase.SaveResult.Fail("Скидка доступна только для обычной формы КС.",rev);
        }
        if(old==percent) return new SqlServerDatabase.SaveResult(true,null,rev);
        using(var update=new SqlCommand("""
            UPDATE dbo.contracts SET discount_percent=@percent WHERE id=@id AND locked=0 AND stage_of IS NULL
            """,conn,tx))
        {
            update.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            update.Parameters.Add("@percent",SqlDbType.Float).Value=(double)percent;
            if(update.ExecuteNonQuery()!=1)
                return SqlServerDatabase.SaveResult.Fail("Договор изменён или удалён.",rev);
        }
        int affected;
        using(var propagate=new SqlCommand("""
            UPDATE dbo.acts SET discount_percent=@percent
            WHERE contract_id=@id AND signed=0 AND discount_override=0 AND manual_total_cents<0
            """,conn,tx))
        {
            propagate.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            propagate.Parameters.Add("@percent",SqlDbType.Float).Value=(double)percent;
            affected=propagate.ExecuteNonQuery();
        }
        using(var log=new SqlCommand("""
            INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id)
            VALUES (@ts,@user,N'contract',@id,N'update',@details,@id)
            """,conn,tx))
        {
            log.Parameters.AddWithValue("@ts",DateTime.Now);
            log.Parameters.AddWithValue("@user",ChangeLog.CurrentUser);
            log.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            log.Parameters.Add("@details",SqlDbType.NVarChar,-1).Value=
                $"Скидка договора: {old}% → {percent}%; обновлено черновиков КС: {affected}";
            log.ExecuteNonQuery();
        }
        var updated=SqlServerDatabase.BumpPositionRevision(conn,tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true,null,updated);
    }
}
