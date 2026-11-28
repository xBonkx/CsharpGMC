using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerWorkspace
{
    public sealed record AgreementCardSnapshot(ObjectProperties Properties,
        SqlServerKsRequisites.Values Print, AgreementPresentation.Requisites? Agreement,
        string Contractor, IReadOnlyList<ObjectTag> Tags, string Revision)
    {
        public string SignedLegalPdf { get; init; } = "";
        public bool LegalEnabled { get; init; }
        public IReadOnlyList<SqlServerAgreements.Values> LegalCards { get; init; } = Array.Empty<SqlServerAgreements.Values>();
        public SqlServerAgreements.Values? SelectedLegal { get; init; }
        public IReadOnlyList<SqlServerAgreements.Company> LegalCompanies { get; init; } = Array.Empty<SqlServerAgreements.Company>();
        public IReadOnlyList<AgreementCardDocument> Documents { get; init; } = Array.Empty<AgreementCardDocument>();
    }

    public sealed record AgreementCardDocument(DocRow Document, string Contractor)
    {
        public string Name => Document.Kind == "main" ? AgreementPresentation.AnnexTitle(Document) : Document.NavigationName;
        public string Number => Document.Number;
        public string Date => Document.Date;
        public bool CanEdit => !Document.Locked && !Document.IsStage;
    }

    public static AgreementCardSnapshot ReadAgreementCard(string connectionString, int objectId, long? legalId = null)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var revision = SqlServerDatabase.Revision(conn);
            var properties = ReadObject(conn, objectId);
            var print = SqlServerKsRequisites.ReadShared(conn, objectId);
            var metadata = Workspace.ReadObjectDocumentMetadata(conn, objectId);
            var enabled = SqlServerAgreements.Enabled(conn);
            var cards = SqlServerAgreements.Read(conn, objectId);
            var selected = legalId == 0 ? null : legalId is long chosen
                ? cards.SingleOrDefault(c => c.Id == chosen) : cards.FirstOrDefault();
            if (legalId is > 0 && selected is null)
                throw new InvalidOperationException("Выбранный договор удалён, архивирован или относится к другому объекту.");
            if (selected is not null) print = selected.Print;
            var agreement = enabled ? (selected is { CompanyId: long companyId } ?
                new AgreementPresentation.Requisites(selected.Number, selected.Date, companyId) : null) : AgreementPresentation.Resolve(metadata);
            var companies = Companies(conn).ToDictionary(c => c.Id, c => c.Name);
            string CompanyName(long? id) => id is long key && companies.TryGetValue(key, out var name) ? name : "";
            var company = CompanyName(agreement?.CompanyId);
            var documents = metadata.Where(d => d.Kind is "main" or "dop")
                .Select(d => new AgreementCardDocument(d, CompanyName(d.CompanyId))).ToArray();
            var tags = ObjectTags(conn, properties.Form);
            var signedPdf = selected is null ? "" : SqlServerAgreementFiles.Latest(conn, selected.Id);
            var legalCompanies = enabled ? SqlServerAgreements.Companies(conn) : Array.Empty<SqlServerAgreements.Company>();
            if (!string.IsNullOrWhiteSpace(revision) && revision == SqlServerDatabase.Revision(conn))
                return new(properties, print, agreement, company, tags, revision) { Documents = documents, SignedLegalPdf = signedPdf, LegalEnabled = enabled, LegalCards = cards, SelectedLegal = selected, LegalCompanies = legalCompanies };
        }
        throw new InvalidOperationException("Реквизиты изменились во время чтения. Откройте страницу заново.");
    }

    // One transaction for ordinary card properties and all shared print fields.
    // No contract rows, local KS site parts, acts, or historical files are changed.
    public static void SaveAgreementCard(string connectionString, int objectId,
        ObjectProperties properties, SqlServerKsRequisites.Values print, string expectedRevision, SqlServerAgreements.Values? legal = null)
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
        if (legal is not null)
        {
            if (legal.ObjectId != objectId) throw new ArgumentException("Договор относится к другому объекту.");
            SqlServerAgreements.Save(conn, tx, legal with { Print = print });
        }
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
