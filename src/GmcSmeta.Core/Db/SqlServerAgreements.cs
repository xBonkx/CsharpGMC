using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Independent legal agreements. Global revision and all writes stay transactional.</summary>
public static class SqlServerAgreements
{
    public sealed record Values(long Id, long ObjectId, string Number, string Date, long? CompanyId,
        string Subject, string WorksEnd, string CustomerAddress, string ContractorFull,
        string ContractorPosition, string ContractorName, SqlServerKsRequisites.Values Print)
    {
        public string Caption => string.IsNullOrWhiteSpace(Number) ? $"Новый договор / реквизиты не заполнены (ID {Id})" :
            "№ " + Number + (Date.Length == 0 ? "" : " от " + Date);
    }
    public sealed record Company(long Id, string Name, string FullName, string Position, string Signer);

    internal static bool Enabled(SqlConnection conn, SqlTransaction? tx = null)
    {
        using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.agreements',N'U') IS NOT NULL AND COL_LENGTH(N'dbo.contracts',N'agreement_id') IS NOT NULL THEN 1 ELSE 0 END", conn, tx);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public static IReadOnlyList<Values> Read(SqlConnection conn, long objectId, SqlTransaction? tx = null)
    {
        if (!Enabled(conn, tx)) return Array.Empty<Values>();
        using var cmd = new SqlCommand("SELECT a.id,a.number,a.agreement_date,a.company_id,a.subject,a.works_end," +
            "a.customer_address,a.contractor_full,a.contractor_post,a.contractor_name,a.customer_full,o.name,o.site_full," +
            "a.cust_org,a.cust_post,a.cust_name,a.second_role,a.second_org,a.second_post,a.second_name,a.work_title,a.works_start " +
            "FROM dbo.agreements a JOIN dbo.objects o ON o.id=a.object_id WHERE a.object_id=@id AND a.archived=0 ORDER BY a.id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
        using var r = cmd.ExecuteReader();
        var rows = new List<Values>();
        while (r.Read())
        {
            string S(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            string D(int i) => r.IsDBNull(i) ? "" : Convert.ToDateTime(r.GetValue(i), CultureInfo.InvariantCulture).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            var print = new SqlServerKsRequisites.Values(objectId, S(11), S(10), S(12), S(13), S(14), S(15), S(16), S(17), S(18), S(19), S(20), D(21), "");
            rows.Add(new(SqlInteger.ReadInt64(r, 0), objectId, S(1), D(2), r.IsDBNull(3) ? null : SqlInteger.ReadInt64(r, 3), S(4), D(5), S(6), S(7), S(8), S(9), print));
        }
        return rows;
    }

    internal static IReadOnlyList<Company> Companies(SqlConnection conn, SqlTransaction? tx = null)
    {
        using var cmd = new SqlCommand("SELECT id,name,legal_full,signer_post,signer_name FROM dbo.companies ORDER BY [order],id", conn, tx);
        using var r = cmd.ExecuteReader();
        var rows = new List<Company>();
        while (r.Read())
        {
            string S(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            rows.Add(new(SqlInteger.ReadInt64(r, 0), S(1), S(2), S(3), S(4)));
        }
        return rows;
    }

    public static Values Validate(Values value)
    {
        if (value.Id < 0 || value.ObjectId <= 0 || value.CompanyId is <= 0)
            throw new ArgumentException("Неверная принадлежность договора или подрядчик.");
        foreach (var text in new[] { value.Number, value.Date, value.Subject, value.WorksEnd, value.CustomerAddress,
            value.ContractorFull, value.ContractorPosition, value.ContractorName })
            if (text is null || text.Length > 2000) throw new ArgumentException("Реквизиты превышают ограничение 2000 символов.");
        if (value.Number.Length > 200) throw new ArgumentException("Номер договора превышает 200 символов.");
        SqlServerWorkspace.ValidateSharedPrint(value.ObjectId, value.Print);
        var date = Date(value.Date); var start = Date(value.Print.WorksStart); var end = Date(value.WorksEnd);
        if (start.HasValue && end.HasValue && end < start) throw new ArgumentException("Дата окончания работ раньше даты начала.");
        return value with { Number = value.Number.Trim(), Date = Format(date), WorksEnd = Format(end) };
    }

    internal static long Save(SqlConnection conn, SqlTransaction tx, Values raw)
    {
        if (!Enabled(conn, tx)) throw new InvalidOperationException("Нужно согласованное расширение схемы договоров.");
        var v = Validate(raw);
        if (v.CompanyId.HasValue)
        {
            using var company = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.companies WITH(HOLDLOCK) WHERE id=@id", conn, tx);
            company.Parameters.Add("@id", SqlDbType.BigInt).Value = v.CompanyId.Value;
            if (Convert.ToInt64(company.ExecuteScalar()) != 1) throw new ArgumentException("Подрядчик больше не существует.");
        }
        using var cmd = new SqlCommand(v.Id == 0
            ? "INSERT dbo.agreements(object_id,number,agreement_date,company_id,subject,works_start,works_end,customer_address," +
              "contractor_full,contractor_post,contractor_name,customer_full,cust_org,cust_post,cust_name,second_role,second_org,second_post,second_name,work_title) " +
              "OUTPUT INSERTED.id VALUES(@object,@number,@date,@company,@subject,@start,@end,@address,@contractor,@post,@name,@customer,@custorg,@custpost,@custname,@role,@secondorg,@secondpost,@secondname,@work)"
            : "UPDATE dbo.agreements SET number=@number,agreement_date=@date,company_id=@company,subject=@subject,works_start=@start,works_end=@end," +
              "customer_address=@address,contractor_full=@contractor,contractor_post=@post,contractor_name=@name,customer_full=@customer," +
              "cust_org=@custorg,cust_post=@custpost,cust_name=@custname,second_role=@role,second_org=@secondorg,second_post=@secondpost,second_name=@secondname,work_title=@work " +
              "OUTPUT INSERTED.id WHERE id=@id AND object_id=@object AND archived=0", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = v.Id;
        cmd.Parameters.Add("@object", SqlDbType.BigInt).Value = v.ObjectId;
        void Text(string key, string value) => cmd.Parameters.Add(key, SqlDbType.NVarChar, key == "@number" ? 200 : 2000).Value = value;
        void Day(string key, string value) => cmd.Parameters.Add(key, SqlDbType.Date).Value = Date(value) is { } day ? day : DBNull.Value;
        Text("@number", v.Number); Day("@date", v.Date); Day("@start", v.Print.WorksStart); Day("@end", v.WorksEnd);
        cmd.Parameters.Add("@company", SqlDbType.BigInt).Value = v.CompanyId is long companyId ? companyId : DBNull.Value;
        Text("@subject", v.Subject); Text("@address", v.CustomerAddress); Text("@contractor", v.ContractorFull);
        Text("@post", v.ContractorPosition); Text("@name", v.ContractorName); Text("@customer", v.Print.CustomerFull);
        Text("@custorg", v.Print.CustomerOrganization); Text("@custpost", v.Print.CustomerPosition); Text("@custname", v.Print.CustomerName);
        Text("@role", v.Print.SecondRole); Text("@secondorg", v.Print.SecondOrganization); Text("@secondpost", v.Print.SecondPosition);
        Text("@secondname", v.Print.SecondName); Text("@work", v.Print.WorkTitle);
        var result = cmd.ExecuteScalar();
        if (result is null or DBNull) throw new DBConcurrencyException("Договор удалён, архивирован или относится к другому объекту.");
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    internal static DateTime? Date(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!AgreementPresentation.TryDate(value, out var date)) throw new ArgumentException("Дата должна быть в формате дд.мм.гггг.");
        return date;
    }
    private static string Format(DateTime? date) => date?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "";

    public static long? DocumentAgreementId(SqlConnection conn, long documentId, SqlTransaction? tx = null)
    {
        if (!Enabled(conn, tx)) return null;
        using var cmd = new SqlCommand("SELECT agreement_id FROM dbo.contracts WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = documentId;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static string AnnexNumber(SqlConnection conn,long documentId,SqlTransaction? tx=null)
    {
        if(!Enabled(conn,tx))return "";
        using var cmd=new SqlCommand("SELECT annex_number FROM dbo.contracts WHERE id=@id",conn,tx);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=documentId;
        return cmd.ExecuteScalar() as string??"";
    }

    internal static void SetAnnexNumber(SqlConnection conn,SqlTransaction tx,long documentId,string number)
    {
        if(!Enabled(conn,tx))return;
        if(number.Length>200)throw new ArgumentException("Номер приложения превышает 200 символов.");
        using var cmd=new SqlCommand("UPDATE dbo.contracts SET annex_number=@number WHERE id=@id AND kind=N'main'",conn,tx);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=documentId;
        cmd.Parameters.Add("@number",SqlDbType.NVarChar,200).Value=number.Trim();
        cmd.ExecuteNonQuery();
    }

    internal static void AttachNewDocument(SqlConnection conn, SqlTransaction tx, long documentId, long? preferred = null, long? sourceId = null)
    {
        if (!Enabled(conn, tx)) return;
        long objectId; string kind; long? parent;
        using (var cmd = new SqlCommand("SELECT object_id,kind,COALESCE(stage_of,base_contract_id) FROM dbo.contracts WITH(UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = documentId;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("Документ не найден.");
            objectId = SqlInteger.ReadInt64(r, 0); kind = r.GetString(1);
            parent = r.IsDBNull(2) ? sourceId : SqlInteger.ReadInt64(r, 2);
        }
        if (kind == "kp") return;
        var inherited = parent.HasValue ? DocumentAgreementId(conn, parent.Value, tx) : null;
        if (preferred.HasValue && inherited.HasValue && preferred != inherited)
            throw new ArgumentException("Договор ДС или этапа должен совпадать с договором основания.");
        var cards = Read(conn, objectId, tx);
        var id = inherited ?? preferred;
        if (id is null && cards.Count == 0)
        {
            using var source = new SqlCommand("SELECT number,[date],company_id FROM dbo.contracts WHERE id=@id", conn, tx);
            source.Parameters.Add("@id", SqlDbType.BigInt).Value = documentId;
            string number, date; long? companyId;
            using (var r=source.ExecuteReader())
            {
                if (!r.Read()) throw new InvalidOperationException("Документ не найден.");
                number=kind=="main" && !r.IsDBNull(0)?r.GetString(0):"";
                date=kind=="main" && !r.IsDBNull(1)?Convert.ToString(r.GetValue(1),CultureInfo.InvariantCulture)??"":"";
                companyId=r.IsDBNull(2)?null:SqlInteger.ReadInt64(r, 2);
            }
            var company=Companies(conn,tx).FirstOrDefault(c=>c.Id==companyId);
            var print=SqlServerKsRequisites.ReadShared(conn,objectId,tx);
            var draft=new Values(0,objectId,number,date,companyId,"","","",company?.FullName??"",company?.Position??"",company?.Signer??"",print);
            id=Save(conn,tx,draft);
            cards=new[]{draft with{Id=id.Value}};
        }
        if (id is null)
        {
            if (cards.Count != 1) throw new InvalidOperationException("Выберите юридический договор в реквизитах документа. Для нового объекта сначала сохраните реквизиты договора.");
            id = cards[0].Id;
        }
        if (cards.All(a => a.Id != id)) throw new ArgumentException("Выбранный договор относится к другому объекту или архивирован.");
        using var update = new SqlCommand("UPDATE dbo.contracts SET agreement_id=@agreement WHERE id=@id AND object_id=@object", conn, tx);
        update.Parameters.Add("@agreement", SqlDbType.BigInt).Value = id.Value;
        update.Parameters.Add("@id", SqlDbType.BigInt).Value = documentId;
        update.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
        if (update.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Документ изменился.");
    }

    internal static Values? ForDocument(SqlConnection conn, long documentId)
    {
        if (!Enabled(conn)) return null;
        long objectId; long? id;
        using (var cmd = new SqlCommand("SELECT object_id,agreement_id FROM dbo.contracts WHERE id=@id", conn))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = documentId;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("Документ не найден.");
            objectId = SqlInteger.ReadInt64(r, 0); id = r.IsDBNull(1) ? null : SqlInteger.ReadInt64(r, 1);
        }
        return id is long value ? Read(conn, objectId).SingleOrDefault(a => a.Id == value) : null;
    }

    // The existing command moves an annex group, not a legal agreement or
    // its signed files. Reassigning to an existing legal ID requires a
    // separately agreed mapping workflow, even when numbers look identical.
    public static void ValidateTransferDestination(int activeAgreementCount,bool sourceHasFiles)
    {
        if(activeAgreementCount!=0)
            throw new InvalidOperationException("В объекте назначения уже есть юридические договоры. Автоматическая перепривязка приложения запрещена.");
        if(sourceHasFiles)
            throw new InvalidOperationException("К исходному юридическому договору прикреплены файлы. Перенос без согласованного переноса юридического контекста запрещён.");
    }

    internal static long TransferTarget(SqlConnection conn, SqlTransaction tx, Values source, long targetObject)
    {
        // Count archived cards as well: a new clone must not obscure a previous
        // legal context, and no existing agreement may be silently substituted.
        using var target=new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.agreements WITH(UPDLOCK,HOLDLOCK) WHERE object_id=@id",conn,tx);
        target.Parameters.Add("@id",SqlDbType.BigInt).Value=targetObject;
        var count=Convert.ToInt64(target.ExecuteScalar(),CultureInfo.InvariantCulture);
        using var files=new SqlCommand("SELECT TOP(1) id FROM dbo.agreement_files WITH(UPDLOCK,HOLDLOCK) WHERE agreement_id=@id",conn,tx);
        files.Parameters.Add("@id",SqlDbType.BigInt).Value=source.Id;
        ValidateTransferDestination(count==0?0:1,files.ExecuteScalar() is not null);
        var print=source.Print with{ObjectId=targetObject};
        return Save(conn,tx,source with{Id=0,ObjectId=targetObject,Print=print});
    }
}
