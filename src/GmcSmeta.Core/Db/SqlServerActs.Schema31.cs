using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerActs
{
    // This narrowly scoped writer is not wired to WPF. The ordinary Open() and
    // pilot write guards reject its dedicated database. Never use production.
    private static SqlConnection OpenSchema31WriteTest(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "GMC-Smeta C# Schema31 write test",
            ConnectTimeout = 5,
        };
        SqlSchemaCompatibility.RequireWriteTestDatabase(builder.InitialCatalog);
        var conn = new SqlConnection(builder.ConnectionString);
        try
        {
            conn.Open();
            SqlSchemaCompatibility.RequireWriteTestDatabase(conn.Database);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SET QUOTED_IDENTIFIER ON;
                SELECT (SELECT CONVERT(nvarchar(32),[value]) FROM dbo.meta WHERE [key]=N'schema_version'),
                    CONVERT(nvarchar(32),DATABASEPROPERTYEX(DB_NAME(),'Updateability')),
                    CASE WHEN COL_LENGTH(N'dbo.contracts',N'discount_percent') IS NOT NULL
                        AND COL_LENGTH(N'dbo.acts',N'discount_percent') IS NOT NULL
                        AND COL_LENGTH(N'dbo.acts',N'discount_override') IS NOT NULL
                        AND COL_LENGTH(N'dbo.acts',N'manual_total_cents') IS NOT NULL
                        AND COL_LENGTH(N'dbo.acts',N'section_amounts_json') IS NOT NULL
                        THEN 1 ELSE 0 END;
                """;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("Не удалось проверить схему 31.");
            SqlSchemaCompatibility.ValidateWriteTest(r.IsDBNull(0) ? null : r.GetString(0),
                conn.Database, r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2) == 1);
            return conn;
        }
        catch { conn.Dispose(); throw; }
    }

    /// <summary>Test-only atomic save of the three v31 discount fields.
    /// Validates the live contract/act, logs the edit, and bumps meta.rev once.</summary>
    public static SqlServerDatabase.SaveResult SaveDiscountSchema31Test(
        string connectionString, long actId, string mode, decimal? value, string expectedRevision)
    {
        using var conn = OpenSchema31WriteTest(connectionString);
        return SaveDiscountCore(conn,actId,mode,value,expectedRevision);
    }

    public static SqlServerDatabase.SaveResult SaveDiscount(string connectionString,long actId,
        string mode,decimal? value,string expectedRevision)
    {
        if(!SqlSchemaCompatibility.OrdinarySchema31Enabled)
            return SqlServerDatabase.SaveResult.Fail("Штатная запись скидок v31 пока не разрешена.",expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn=SqlServerDatabase.Open(connectionString);
        return SaveDiscountCore(conn,actId,mode,value,expectedRevision);
    }

    private static SqlServerDatabase.SaveResult SaveDiscountCore(SqlConnection conn,long actId,
        string mode,decimal? value,string expectedRevision)
    {
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null || !string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены. Обновите КС.", rev ?? expectedRevision);

        long contractId;
        DiscountPolicy.ActDiscount old;
        DiscountPolicy.ActDiscount proposed;
        using (var read = new SqlCommand("""
            SELECT a.contract_id,a.signed,c.locked,c.discount_percent,c.form,o.name,
                t.rules,a.discount_override,a.manual_total_cents,a.discount_percent
            FROM dbo.acts a WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.contracts c ON c.id=a.contract_id
            JOIN dbo.objects o ON o.id=c.object_id
            LEFT JOIN dbo.tags t ON t.[key]=c.form WHERE a.id=@id
            """, conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = actId;
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Акт не найден.", rev);
            contractId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            if (Convert.ToBoolean(r.GetValue(1)) || Convert.ToBoolean(r.GetValue(2)))
                return SqlServerDatabase.SaveResult.Fail("Подписанный акт или заблокированный договор нельзя изменять.", rev);
            var form = r.IsDBNull(4) ? "" : r.GetString(4);
            var name = r.IsDBNull(5) ? "" : r.GetString(5);
            var rules = r.IsDBNull(6) ? "" : r.GetString(6);
            if (mode != "contract" && !DiscountPolicy.SupportsDiscount(
                KsFinancialPolicy.HasRule(rules,"vat_top"), KsFinancialPolicy.HasRule(rules,"ks_dns"), form, name))
                return SqlServerDatabase.SaveResult.Fail("Корректировка не поддерживается этой формой КС.", rev);
            try { proposed = DiscountPolicy.Normalize(mode,value,DiscountPolicy.ReadPercent(r,3)); }
            catch (ArgumentException ex) { return SqlServerDatabase.SaveResult.Fail(ex.Message,rev); }
            old = new DiscountPolicy.ActDiscount(Convert.ToBoolean(r.GetValue(7)),
                Convert.ToInt64(r.GetValue(8)),DiscountPolicy.ReadPercent(r,9));
        }
        if (old == proposed) return new SqlServerDatabase.SaveResult(true,null,rev);
        using (var update = new SqlCommand("""
            UPDATE dbo.acts SET discount_override=@override,manual_total_cents=@cents,discount_percent=@percent
            WHERE id=@id AND contract_id=@cid AND signed=0
            """,conn,tx))
        {
            update.Parameters.Add("@override",SqlDbType.Bit).Value=proposed.Override;
            update.Parameters.Add("@cents",SqlDbType.BigInt).Value=proposed.ManualTotalCents;
            update.Parameters.Add("@percent",SqlDbType.Float).Value=(double)proposed.Percent;
            update.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            update.Parameters.Add("@cid",SqlDbType.BigInt).Value=contractId;
            if (update.ExecuteNonQuery()!=1)
                return SqlServerDatabase.SaveResult.Fail("Акт изменён или удалён.",rev);
        }
        using (var log = new SqlCommand("""
            INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id)
            VALUES (@ts,@user,N'act',@id,N'edit',@details,@cid)
            """,conn,tx))
        {
            log.Parameters.AddWithValue("@ts",DateTime.Now);
            log.Parameters.AddWithValue("@user",ChangeLog.CurrentUser);
            log.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            log.Parameters.Add("@cid",SqlDbType.BigInt).Value=contractId;
            log.Parameters.Add("@details",SqlDbType.NVarChar,-1).Value=
                $"Корректировка КС: {old.Percent}% / {old.ManualTotalCents} коп. → {proposed.Percent}% / {proposed.ManualTotalCents} коп.";
            log.ExecuteNonQuery();
        }
        var updated=SqlServerDatabase.BumpPositionRevision(conn,tx);
        tx.Commit(); // unknown outcome must propagate, never automatically retry
        return new SqlServerDatabase.SaveResult(true,null,updated);
    }
}
