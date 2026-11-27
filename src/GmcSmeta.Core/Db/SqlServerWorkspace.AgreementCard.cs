using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerWorkspace
{
    public sealed record AgreementCardSnapshot(ObjectProperties Properties,
        SqlServerKsRequisites.Values Print, AgreementPresentation.Requisites? Agreement,
        string Contractor, IReadOnlyList<ObjectTag> Tags, string Revision);

    public static AgreementCardSnapshot ReadAgreementCard(string connectionString, int objectId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var revision = SqlServerDatabase.Revision(conn);
            var properties = ReadObject(conn, objectId);
            var print = SqlServerKsRequisites.ReadShared(conn, objectId);
            var agreement = AgreementPresentation.Resolve(Workspace.ReadObjectDocumentMetadata(conn, objectId));
            var company = agreement is null ? "" : Companies(conn).FirstOrDefault(c => c.Id == agreement.CompanyId)?.Name ?? "";
            var tags = ObjectTags(conn, properties.Form);
            if (!string.IsNullOrWhiteSpace(revision) && revision == SqlServerDatabase.Revision(conn))
                return new(properties, print, agreement, company, tags, revision);
        }
        throw new InvalidOperationException("Реквизиты изменились во время чтения. Откройте страницу заново.");
    }

    // One transaction for ordinary card properties and all shared print fields.
    // No contract rows, local KS site parts, acts, or historical files are changed.
    public static void SaveAgreementCard(string connectionString, int objectId,
        ObjectProperties properties, SqlServerKsRequisites.Values print, string expectedRevision)
    {
        RequirePilotDatabase(connectionString);
        var clean = ValidateObject(properties);
        ValidateSharedPrint(objectId, print);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);
        using (var check = new SqlCommand("SELECT archived FROM dbo.objects WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
            var archived = check.ExecuteScalar();
            if (archived is null or DBNull || Convert.ToBoolean(archived, CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Карточка удалена или находится в архиве.");
        }
        using var update = new SqlCommand(
            "UPDATE dbo.objects SET name=@name,customer=@customer,address=@address,folder=@folder,note=@note,form=@form," +
            "customer_full=@customer_full,site_full=@site_full,cust_org=@cust_org,cust_post=@cust_post,cust_name=@cust_name," +
            "second_role=@second_role,second_org=@second_org,second_post=@second_post,second_name=@second_name," +
            "work_title=@work_title,works_start=@works_start WHERE id=@id", conn, tx);
        AddObjectFields(update, clean);
        update.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
        void Set(string key, string value) => update.Parameters.Add(key, SqlDbType.NVarChar, -1).Value = value.Trim();
        Set("@customer_full", print.CustomerFull);
        Set("@site_full", print.SiteFull);
        Set("@cust_org", print.CustomerOrganization);
        Set("@cust_post", print.CustomerPosition);
        Set("@cust_name", print.CustomerName);
        Set("@second_role", print.SecondRole);
        Set("@second_org", print.SecondOrganization);
        Set("@second_post", print.SecondPosition);
        Set("@second_name", print.SecondName);
        Set("@work_title", print.WorkTitle);
        Set("@works_start", print.WorksStart);
        if (update.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Карточка больше не существует.");
        Log(conn, tx, "object", objectId, "ks_requisites", "Обновлены общие реквизиты карточки договора", null, objectId);
        BumpRevision(conn, tx);
        tx.Commit();
    }

    public static void ValidateSharedPrint(long objectId, SqlServerKsRequisites.Values print)
    {
        if (print.ObjectId != objectId || !string.IsNullOrEmpty(print.SitePart))
            throw new ArgumentException("Ожидались общие реквизиты именно этой карточки, без части объекта приложения.");
        foreach (var value in new[] { print.CustomerFull, print.SiteFull, print.CustomerOrganization,
            print.CustomerPosition, print.CustomerName, print.SecondRole, print.SecondOrganization,
            print.SecondPosition, print.SecondName, print.WorkTitle, print.WorksStart })
            if (value is null || value.Length > 2000)
                throw new ArgumentException("Реквизиты превышают ограничение 2000 символов.");
        if (print.WorksStart.Trim().Length > 0 && !DateTime.TryParseExact(print.WorksStart.Trim(),
            "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("Дата начала работ должна быть в формате дд.мм.гггг.");
    }
}
