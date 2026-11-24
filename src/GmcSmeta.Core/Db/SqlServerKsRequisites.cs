using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Db;

/// <summary>Shared object signatory details and contract KS site part. SQL Server only.</summary>
public static class SqlServerKsRequisites
{
    public sealed record Values(long ObjectId, string ObjectName, string CustomerFull,
        string SiteFull, string CustomerOrganization, string CustomerPosition,
        string CustomerName, string SecondRole, string SecondOrganization,
        string SecondPosition, string SecondName, string WorkTitle,
        string WorksStart, string SitePart);

    public static Values Read(string connectionString, long contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        return Read(conn, contractId);
    }

    public static (Values Data, string Revision) ReadSnapshot(string connectionString, long contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = SqlServerDatabase.Revision(conn);
            var data = Read(conn, contractId);
            if (!string.IsNullOrWhiteSpace(before) && before == SqlServerDatabase.Revision(conn))
                return (data, before);
        }
        throw new InvalidOperationException("Реквизиты изменились во время чтения. Откройте окно повторно.");
    }

    internal static Values Read(SqlConnection conn, long contractId)
    {
        using var command = new SqlCommand(
            "SELECT c.object_id, o.name, o.customer_full, o.site_full, " +
            "o.cust_org, o.cust_post, o.cust_name, o.second_role, o.second_org, " +
            "o.second_post, o.second_name, o.work_title, o.works_start, c.ks_site_part " +
            "FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id " +
            "WHERE c.id=@id", conn);
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Договор не найден.");
        string S(int i) => reader.IsDBNull(i) ? "" :
            Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
        return new Values(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            S(1), S(2), S(3), S(4), S(5), S(6), S(7), S(8), S(9),
            S(10), S(11), S(12), S(13));
    }

    public static SqlServerDatabase.SaveResult Save(
        string connectionString, long contractId, Values data, string expectedRevision)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.InitialCatalog.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Запись в реквизиты разрешена только в тестовую базу GmcSmetaPilot_*.");
        // Reject oversized text before touching any database rows.
        foreach (var value in new[] { data.CustomerFull, data.SiteFull, data.CustomerOrganization,
            data.CustomerPosition, data.CustomerName, data.SecondRole, data.SecondOrganization,
            data.SecondPosition, data.SecondName, data.WorkTitle, data.WorksStart, data.SitePart })
            if ((value ?? "").Length > 2000)
                return SqlServerDatabase.SaveResult.Fail("Реквизиты превышают ограничение 2000 символов.", expectedRevision);

        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = SqlServerDatabase.LockRevision(conn, tx);
        if (revision is null)
            return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Реквизиты уже изменены другим сотрудником. Обновите окно.", revision);

        long objectId;
        using (var ownership = new SqlCommand(
            "SELECT object_id FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            ownership.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            var id = ownership.ExecuteScalar();
            if (id is null || id is DBNull)
                return SqlServerDatabase.SaveResult.Fail("Договор больше не существует.", revision);
            objectId = Convert.ToInt64(id, CultureInfo.InvariantCulture);
        }
        if (objectId != data.ObjectId)
            return SqlServerDatabase.SaveResult.Fail("Реквизиты относятся к другому объекту.", revision);

        using (var update = new SqlCommand(
            "UPDATE dbo.objects SET customer_full=@customer,site_full=@site, " +
            "cust_org=@cust_org,cust_post=@cust_post,cust_name=@cust_name, " +
            "second_role=@second_role,second_org=@second_org, " +
            "second_post=@second_post,second_name=@second_name, " +
            "work_title=@work_title,works_start=@works_start WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
            void Set(string parameter, string value) =>
                update.Parameters.Add(parameter, SqlDbType.NVarChar, -1).Value = value ?? "";
            Set("@customer", data.CustomerFull);
            Set("@site", data.SiteFull);
            Set("@cust_org", data.CustomerOrganization);
            Set("@cust_post", data.CustomerPosition);
            Set("@cust_name", data.CustomerName);
            Set("@second_role", data.SecondRole);
            Set("@second_org", data.SecondOrganization);
            Set("@second_post", data.SecondPosition);
            Set("@second_name", data.SecondName);
            Set("@work_title", data.WorkTitle);
            Set("@works_start", data.WorksStart);
            if (update.ExecuteNonQuery() != 1)
                return SqlServerDatabase.SaveResult.Fail("Объект больше не существует.", revision);
        }
        using (var update = new SqlCommand(
            "UPDATE dbo.contracts SET ks_site_part=@part WHERE id=@id AND object_id=@obj", conn, tx))
        {
            update.Parameters.Add("@part", SqlDbType.NVarChar, -1).Value = data.SitePart ?? "";
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            update.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
            if (update.ExecuteNonQuery() != 1)
                return SqlServerDatabase.SaveResult.Fail("Договор был изменён.", revision);
        }
        using (var log = new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (SYSDATETIME(),@user,N'object',@obj,N'ks_requisites',@details,@cid)", conn, tx))
        {
            log.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            log.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
            log.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                "Обновлены реквизиты для КС-2 / КС-3";
            log.ExecuteNonQuery();
        }

        var savedRevision = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, savedRevision);
    }
}
