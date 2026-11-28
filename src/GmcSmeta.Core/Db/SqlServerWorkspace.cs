using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// SQL Server-only workspace operations. Never accepts the legacy file backend.
/// Writes are deliberately restricted to isolated GmcSmetaPilot_* databases until
/// SQL Server integration tests and an explicit production cutover are complete.
/// </summary>
public static partial class SqlServerWorkspace
{
    public sealed record NewContractData(string Kind, string Number, string Date, string Name,
        long? CompanyId, long? BaseContractId)
    {
        public long? AgreementId { get; init; }
        public string AnnexNumber { get; init; } = "";
    }

    public static void RequirePilotDatabase(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString ?? "");
        if (!SqlSchemaCompatibility.IsWritablePilot(builder.InitialCatalog, builder.DataSource))
            throw new InvalidOperationException(
                "Запись C# разрешена только в отдельную тестовую базу GmcSmetaPilot_*. Рабочая база не изменена.");
    }

    /// <summary>
    /// Подпись подключения для строки состояния: «сервер · база».
    /// Никогда не включает логин, пароль или другие секреты строки подключения.
    /// </summary>
    public static string DescribeConnection(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString ?? "");
        var server = string.IsNullOrWhiteSpace(builder.DataSource) ? "SQL Server" : builder.DataSource;
        return string.IsNullOrWhiteSpace(builder.InitialCatalog)
            ? server
            : server + " · " + builder.InitialCatalog;
    }

    public static ObjectProperties ValidateObject(ObjectProperties values) =>
        WorkspaceInputPolicy.ValidateObject(values);

    public static NewContractData ValidateContract(NewContractData input) =>
        WorkspaceInputPolicy.ValidateContract(input);

    public static ObjectProperties ReadObject(SqlConnection conn, long objectId)
    {
        using var cmd = new SqlCommand(
            "SELECT name,customer,address,folder,note,form FROM dbo.objects WHERE id=@id", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("Объект не найден.");
        string Get(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
        return new ObjectProperties(Get(0), Get(1), Get(2), Get(3), Get(4), Get(5));
    }

    public static IReadOnlyList<ObjectTag> ObjectTags(SqlConnection conn, string current = "")
    {
        using var cmd = new SqlCommand("SELECT [key],name FROM dbo.tags ORDER BY name,[key]", conn);
        using var r = cmd.ExecuteReader();
        var tags = new List<ObjectTag> { new("", "— обычный") };
        while (r.Read())
        {
            var key = Convert.ToString(r.GetValue(0), CultureInfo.InvariantCulture) ?? "";
            var name = r.IsDBNull(1) ? key : r.GetString(1);
            if (key.Length > 0) tags.Add(new ObjectTag(key, name));
        }
        if (!string.IsNullOrWhiteSpace(current) && tags.All(t => t.Key != current))
            tags.Add(new ObjectTag(current, current));
        return tags;
    }

    public static IReadOnlyList<CompanyInfo> Companies(SqlConnection conn, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var cmd = new SqlCommand(
            "SELECT id,name,vat_rate FROM dbo.companies ORDER BY [order],id", conn);
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        var list = new List<CompanyInfo>();
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            list.Add(new CompanyInfo(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0m : Convert.ToDecimal(r.GetValue(2), CultureInfo.InvariantCulture)));
        }
        token.ThrowIfCancellationRequested();
        return list;
    }

    public static long? LastCompanyId(SqlConnection conn, long objectId)
    {
        using var cmd = new SqlCommand(
            "SELECT TOP(1) company_id FROM dbo.contracts " +
            "WHERE object_id=@id AND company_id IS NOT NULL ORDER BY [order] DESC,id DESC", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static long CreateObject(string connectionString, ObjectProperties values, string expectedRevision)
    {
        var v = ValidateObject(values);
        RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);

        // SQLAlchemy defaults are client-side: populate every NOT NULL column explicitly.
        using var insert = new SqlCommand(
            @"INSERT dbo.objects
              (name,customer,address,folder,note,created_at,customer_full,site_full,
               cust_org,cust_post,cust_name,second_role,second_org,second_post,second_name,
               work_title,archived,form,archived_on,works_start)
              OUTPUT INSERTED.id
              VALUES (@name,@customer,@address,@folder,@note,SYSDATETIME(),N'',N'',
                      N'',N'',N'',N'',N'',N'',N'',N'Монтаж системы вентиляции',0,@form,NULL,N'')",
            conn, tx);
        AddObjectFields(insert, v);
        var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        Log(conn, tx, "object", id, "create", v.Name, null, id);
        BumpRevision(conn, tx);
        tx.Commit();
        return id;
    }

    public static bool UpdateObject(string connectionString, long objectId,
        ObjectProperties values, string expectedRevision)
    {
        var v = ValidateObject(values);
        RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);

        using var previous = new SqlCommand(
            "SELECT name,customer,address,folder,note,form,archived FROM dbo.objects " +
            "WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx);
        previous.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
        ObjectProperties old;
        using (var r = previous.ExecuteReader())
        {
            if (!r.Read()) throw new ArgumentException("Объект не найден.");
            string Get(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            if (!r.IsDBNull(6) && Convert.ToBoolean(r.GetValue(6), CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Архивный объект нельзя изменять.");
            old = new ObjectProperties(Get(0), Get(1), Get(2), Get(3), Get(4), Get(5));
        }
        if (old == v) return false;

        using var update = new SqlCommand(
            "UPDATE dbo.objects SET name=@name,customer=@customer,address=@address," +
            "folder=@folder,note=@note,form=@form WHERE id=@id", conn, tx);
        AddObjectFields(update, v);
        update.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
        if (update.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Объект был удалён.");
        Log(conn, tx, "object", objectId, "update", $"«{old.Name}» → «{v.Name}»", null, objectId);
        BumpRevision(conn, tx);
        tx.Commit();
        return true;
    }

    public static long CreateEmptyContract(string connectionString, long objectId,
        NewContractData data, string expectedRevision)
    {
        var v = ValidateContract(data);
        RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);

        string objName, form;
        using (var obj = new SqlCommand(
            "SELECT name,form,archived FROM dbo.objects WITH (UPDLOCK,HOLDLOCK) WHERE id=@id",
            conn, tx))
        {
            obj.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
            using var r = obj.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Объект не найден.");
            objName = r.GetString(0);
            form = r.IsDBNull(1) ? "" : r.GetString(1);
            if (Convert.ToBoolean(r.GetValue(2), CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Архивный объект нельзя изменять.");
        }

        decimal vat = 22m;
        if (v.CompanyId is long company)
        {
            using var c = new SqlCommand(
                "SELECT vat_rate FROM dbo.companies WITH (HOLDLOCK) WHERE id=@id", conn, tx);
            c.Parameters.Add("@id", SqlDbType.BigInt).Value = company;
            var value = c.ExecuteScalar();
            if (value is null or DBNull) throw new ArgumentException("Организация не найдена.");
            vat = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        if (v.BaseContractId is long parentId)
        {
            using var baseDoc = new SqlCommand(
                "SELECT COUNT_BIG(*) FROM dbo.contracts WITH (HOLDLOCK) " +
                "WHERE id=@id AND object_id=@obj AND kind IN (N'main',N'kp') AND stage_of IS NULL",
                conn, tx);
            baseDoc.Parameters.Add("@id", SqlDbType.BigInt).Value = parentId;
            baseDoc.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
            if (Convert.ToInt64(baseDoc.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new ArgumentException("Исходный договор не найден в этом объекте.");
        }

        using var insert = new SqlCommand(
            @"INSERT dbo.contracts
              (object_id,kind,name,ks_site_part,number,[date],vat_rate,smr_amount,
               transport_amount,source_file,source_sheet,[order],created_at,
               company_id,base_contract_id,smr_mode,smr_percent,note,kp_phase,
               stage_rest,form,m2_mode,pnr_amount,transport_note,locked,locked_by)
              OUTPUT INSERTED.id
              VALUES (@obj,@kind,@name,N'',@number,@date,@vat,0,
                      0,N'',N'',(SELECT COALESCE(MAX([order]),0)+1 FROM dbo.contracts WHERE object_id=@obj),
                      SYSDATETIME(),@company,@base,N'sum',0,N'',@phase,0,@form,N'',0,N'',0,N'')",
            conn, tx);
        insert.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
        insert.Parameters.Add("@kind", SqlDbType.NVarChar, 16).Value = v.Kind;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = v.Name;
        insert.Parameters.Add("@number", SqlDbType.NVarChar, 64).Value = v.Number;
        insert.Parameters.Add("@date", SqlDbType.NVarChar, 32).Value = v.Date;
        insert.Parameters.Add("@vat", SqlDbType.Float).Value = (double)vat;
        insert.Parameters.Add("@company", SqlDbType.BigInt).Value = v.CompanyId is null ? DBNull.Value : v.CompanyId.Value;
        insert.Parameters.Add("@base", SqlDbType.BigInt).Value = v.BaseContractId is null ? DBNull.Value : v.BaseContractId.Value;
        insert.Parameters.Add("@phase", SqlDbType.NVarChar, 24).Value = v.Kind == "kp" ? "volumes" : "";
        insert.Parameters.Add("@form", SqlDbType.NVarChar, 32).Value = form;
        var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        SqlServerAgreements.AttachNewDocument(conn, tx, id, v.AgreementId);
        SqlServerAgreements.SetAnnexNumber(conn, tx, id, v.AnnexNumber);
        Log(conn, tx, "contract", id, "create",
            objName + ": " + Documents.ContractTitleText(v.Kind, v.Number, v.Date, v.Name), id, objectId);
        BumpRevision(conn, tx);
        tx.Commit();
        return id;
    }

    /// <summary>
    /// Копия документа (builder.copy_contract): реквизиты, режимы СМР/транспорта/м², ПНР,
    /// участок КС, форма и — если <paramref name="withPositions"/> — разделы с подчинённостью,
    /// позиции со всеми полями и комплектующими. Акты, выдачи, закупки и строки «вне КП»
    /// не копируются. Организация берётся из диалога, НДС — по организации (как в Python UI),
    /// иначе остаются источник. Журнал: «create» от create_contract, затем «copy».
    /// </summary>
    public static long CopyContract(string connectionString, long sourceId,
        NewContractData data, string expectedRevision, bool withPositions = true)
    {
        var v = ValidateContract(data);
        RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);

        var src = ReadSourceContract(conn, tx, sourceId);
        var (objName, archived) = ReadObjectName(conn, tx, src.ObjectId);
        if (archived)
            throw new InvalidOperationException("Архивный объект нельзя изменять.");
        if (v.BaseContractId is long parentId)
            EnsureBaseContract(conn, tx, parentId, src.ObjectId);

        // Организация из диалога, НДС — по организации; без неё остаются реквизиты источника.
        var company = v.CompanyId ?? src.CompanyId;
        var vat = v.CompanyId is long dialogCompany && CompanyVat(conn, tx, dialogCompany) is double rate
            ? rate : src.VatRate;

        using (var insert = new SqlCommand(
            @"INSERT dbo.contracts
              (object_id,kind,name,ks_site_part,number,[date],vat_rate,smr_amount,
               transport_amount,source_file,source_sheet,[order],created_at,
               company_id,base_contract_id,smr_mode,smr_percent,note,kp_phase,
               stage_rest,form,m2_mode,pnr_amount,transport_note,transport_cost,locked,locked_by)
              OUTPUT INSERTED.id
              VALUES (@obj,@kind,@name,@ksPart,@number,@date,@vat,@smrAmount,
                      @transportAmount,N'',N'',
                      (SELECT COALESCE(MAX([order]),0)+1 FROM dbo.contracts WHERE object_id=@obj),
                      SYSDATETIME(),@company,@base,@smrMode,@smrPercent,N'',@phase,
                      0,@form,@m2Mode,@pnrAmount,@transportNote,@transportCost,0,N'')", conn, tx))
        {
            insert.Parameters.Add("@obj", SqlDbType.BigInt).Value = src.ObjectId;
            insert.Parameters.Add("@kind", SqlDbType.NVarChar, 16).Value = v.Kind;
            insert.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = v.Name;
            insert.Parameters.Add("@ksPart", SqlDbType.NVarChar, 255).Value = src.KsSitePart;
            insert.Parameters.Add("@number", SqlDbType.NVarChar, 64).Value = v.Number;
            insert.Parameters.Add("@date", SqlDbType.NVarChar, 32).Value = v.Date;
            insert.Parameters.Add("@vat", SqlDbType.Float).Value = vat;
            insert.Parameters.Add("@smrAmount", SqlDbType.Float).Value = src.SmrAmount;
            insert.Parameters.Add("@transportAmount", SqlDbType.Float).Value = src.TransportAmount;
            insert.Parameters.Add("@company", SqlDbType.BigInt).Value = company is null ? DBNull.Value : company.Value;
            insert.Parameters.Add("@base", SqlDbType.BigInt).Value = v.BaseContractId is null ? DBNull.Value : v.BaseContractId.Value;
            insert.Parameters.Add("@smrMode", SqlDbType.NVarChar, 8).Value = src.SmrMode;
            insert.Parameters.Add("@smrPercent", SqlDbType.Float).Value = src.SmrPercent;
            insert.Parameters.Add("@phase", SqlDbType.NVarChar, 24).Value = v.Kind == "kp" ? "volumes" : "";
            insert.Parameters.Add("@form", SqlDbType.NVarChar, 32).Value = src.Form;
            insert.Parameters.Add("@m2Mode", SqlDbType.NVarChar, 8).Value = src.M2Mode;
            insert.Parameters.Add("@pnrAmount", SqlDbType.Float).Value = src.PnrAmount;
            insert.Parameters.Add("@transportNote", SqlDbType.NVarChar, -1).Value = src.TransportNote;
            insert.Parameters.Add("@transportCost", SqlDbType.Float).Value =
                src.TransportCost is double cost ? cost : DBNull.Value;
            var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);

            SqlServerAgreements.AttachNewDocument(conn, tx, id, v.AgreementId, sourceId);
            SqlServerAgreements.SetAnnexNumber(conn, tx, id, v.AnnexNumber);
            Log(conn, tx, "contract", id, "create",
                objName + ": " + Documents.ContractTitleText(v.Kind, v.Number, v.Date, v.Name), id, src.ObjectId);
            Log(conn, tx, "contract", id, "copy", "из «" + src.Title + "»", id, src.ObjectId);

            if (SqlSchemaCompatibility.HasPreviewColumns(conn))
            {
                using var discount = new SqlCommand("""
                    UPDATE dst SET discount_percent=src.discount_percent
                    FROM dbo.contracts dst CROSS JOIN dbo.contracts src
                    WHERE dst.id=@dst AND src.id=@src
                    """, conn, tx);
                discount.Parameters.Add("@dst", SqlDbType.BigInt).Value=id;
                discount.Parameters.Add("@src", SqlDbType.BigInt).Value=sourceId;
                if(discount.ExecuteNonQuery()!=1)
                    throw new DBConcurrencyException("Не удалось скопировать итоговую скидку договора.");
            }
            if (withPositions)
                CopyStructure(conn, tx, sourceId, id);
            BumpRevision(conn, tx);
            tx.Commit();
            return id;
        }
    }

    // ---------------------------------------------------------------- внутреннее копирование

    private sealed record SourceContract(long ObjectId, long? CompanyId, double VatRate,
        string Kind, string Number, string Date, string Name, string Title,
        string SmrMode, double SmrPercent, double SmrAmount, double TransportAmount,
        string M2Mode, double PnrAmount, double? TransportCost, string TransportNote,
        string KsSitePart, string Form);

    private static SourceContract ReadSourceContract(SqlConnection conn, SqlTransaction tx, long id)
    {
        using var cmd = new SqlCommand(
            "SELECT object_id,company_id,vat_rate,kind,number,[date],name,smr_mode,smr_percent," +
            "smr_amount,transport_amount,m2_mode,pnr_amount,transport_cost,transport_note," +
            "ks_site_part,form FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("Документ не найден.");
        string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
        double Dbl(int i) => Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        var kind = Text(3);
        var number = Text(4);
        var date = Text(5);
        var name = Text(6);
        return new SourceContract(
            Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
            r.IsDBNull(1) ? null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
            Dbl(2), kind, number, date, name,
            Documents.ContractTitleText(kind, number, date, name),
            Text(7), Dbl(8), Dbl(9), Dbl(10), Text(11), Dbl(12),
            r.IsDBNull(13) ? null : Dbl(13), Text(14), Text(15), Text(16));
    }

    private static (string Name, bool Archived) ReadObjectName(SqlConnection conn, SqlTransaction tx, long objectId)
    {
        using var cmd = new SqlCommand(
            "SELECT name,archived FROM dbo.objects WITH (HOLDLOCK) WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("Объект не найден.");
        return (r.GetString(0), Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture));
    }

    private static void EnsureBaseContract(SqlConnection conn, SqlTransaction tx, long baseId, long objectId)
    {
        using var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.contracts WITH (HOLDLOCK) " +
            "WHERE id=@id AND object_id=@obj AND kind IN (N'main',N'kp') AND stage_of IS NULL", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = baseId;
        cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
        if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new ArgumentException("Исходный договор не найден в этом объекте.");
    }

    private static double? CompanyVat(SqlConnection conn, SqlTransaction tx, long companyId)
    {
        using var cmd = new SqlCommand(
            "SELECT vat_rate FROM dbo.companies WITH (HOLDLOCK) WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = companyId;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private sealed record SystemCopy(long Id, long? ParentId, long Order, string Name,
        string Heading, double? SmrAmount, string SourceNum);

    private sealed record PositionCopy(long Id, long? ParentId, long? CatalogItemId,
        string Num, string Basis, string Name, string Unit, double Qty, double Price,
        double? PurchasePrice, string Note, string ExtraJson, long? SourceRow, long Order,
        bool OutOfKp, string Formulas, double? AreaManual, double? WorkPrice,
        string CostNote, string CostColor, double? WorkSell, double? WorkCost);

    /// <summary>Разделы источника в порядке показа; подразделы остаются за своим разделом.</summary>
    private static List<SystemCopy> LoadSystemCopies(SqlConnection conn, SqlTransaction tx, long contractId)
    {
        var list = new List<SystemCopy>();
        using var cmd = new SqlCommand(
            "SELECT id,parent_id,[order],name,heading,smr_amount,source_num " +
            "FROM dbo.systems WITH (HOLDLOCK) WHERE contract_id=@id ORDER BY [order],id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SystemCopy(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                r.IsDBNull(1) ? null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
                r.GetString(3),
                r.IsDBNull(4) ? "" : r.GetString(4),
                r.IsDBNull(5) ? null : Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture),
                r.IsDBNull(6) ? "" : r.GetString(6)));
        return list;
    }

    private static List<PositionCopy> LoadPositionCopies(SqlConnection conn, SqlTransaction tx, long systemId)
    {
        var list = new List<PositionCopy>();
        using var cmd = new SqlCommand(
            "SELECT id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,purchase_price," +
            "note,extra_json,source_row,[order],out_of_kp,formulas,area_manual,work_price," +
            "cost_note,cost_color,work_sell,work_cost " +
            "FROM dbo.positions WITH (HOLDLOCK) WHERE system_id=@id ORDER BY [order],id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = systemId;
        using var r = cmd.ExecuteReader();
        string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
        long? Id(int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
        double Dbl(int i) => Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        double? DblN(int i) => r.IsDBNull(i) ? null : Dbl(i);
        while (r.Read())
            list.Add(new PositionCopy(
                Id(0)!.Value, Id(1), Id(2), Text(3), Text(4), Text(5), Text(6),
                Dbl(7), Dbl(8), DblN(9), Text(10), Text(11), Id(12),
                Convert.ToInt64(r.GetValue(13), CultureInfo.InvariantCulture),
                Convert.ToBoolean(r.GetValue(14), CultureInfo.InvariantCulture),
                Text(15), DblN(16), DblN(17), Text(18), Text(19), DblN(20), DblN(21)));
        return list;
    }

    private static void CopyStructure(SqlConnection conn, SqlTransaction tx, long sourceId, long targetId)
    {
        var made = new Dictionary<long, long>();
        foreach (var sy in LoadSystemCopies(conn, tx, sourceId))
        {
            var parent = sy.ParentId is long parentId && made.TryGetValue(parentId, out var mapped)
                ? mapped : (long?)null;
            using var insert = new SqlCommand(
                @"INSERT dbo.systems (contract_id,parent_id,name,[order],smr_amount,heading,source_num)
                  OUTPUT INSERTED.id
                  VALUES (@contract,@parent,@order,@name,@smrAmount,@heading,@sourceNum)", conn, tx);
            insert.Parameters.Add("@contract", SqlDbType.BigInt).Value = targetId;
            insert.Parameters.Add("@parent", SqlDbType.BigInt).Value = parent is null ? DBNull.Value : parent.Value;
            insert.Parameters.Add("@order", SqlDbType.BigInt).Value = sy.Order;
            insert.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = sy.Name;
            insert.Parameters.Add("@smrAmount", SqlDbType.Float).Value =
                sy.SmrAmount is double amount ? amount : DBNull.Value;
            insert.Parameters.Add("@heading", SqlDbType.NVarChar, 255).Value = sy.Heading;
            insert.Parameters.Add("@sourceNum", SqlDbType.NVarChar, 32).Value = sy.SourceNum;
            var newSystem = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            made[sy.Id] = newSystem;
            CopyPositions(conn, tx, sy.Id, newSystem);
        }
    }

    /// <summary>
    /// Позиции раздела: порядок показа (верхняя строка, сразу её комплектующие), ссылки
    /// parent → новые id; строки «вне КП» и их комплектующие пропускаются, как в Python.
    /// </summary>
    private static void CopyPositions(SqlConnection conn, SqlTransaction tx, long sourceSystemId, long targetSystemId)
    {
        var rows = LoadPositionCopies(conn, tx, sourceSystemId);
        var byId = rows.ToDictionary(p => p.Id);

        var children = new Dictionary<long, List<PositionCopy>>();
        foreach (var p in rows.Where(p => p.ParentId is not null))
        {
            if (!children.TryGetValue(p.ParentId!.Value, out var list))
                children[p.ParentId.Value] = list = new List<PositionCopy>();
            list.Add(p);
        }

        var ordered = new List<PositionCopy>();
        foreach (var top in rows.Where(p => p.ParentId is null).OrderBy(p => p.Order))
        {
            ordered.Add(top);
            if (children.TryGetValue(top.Id, out var kids)) ordered.AddRange(kids.OrderBy(p => p.Order));
        }

        var made = new Dictionary<long, long>();
        foreach (var p in ordered)
        {
            if (p.OutOfKp) continue;
            if (p.ParentId is long parentId && byId.TryGetValue(parentId, out var parent) && parent.OutOfKp)
                continue;

            using var insert = new SqlCommand(
                @"INSERT dbo.positions
                  (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,purchase_price,
                   note,extra_json,source_row,[order],out_of_kp,formulas,area_manual,work_price,
                   cost_note,cost_color,work_sell,work_cost)
                  VALUES (@system,@parent,@catalog,@num,@basis,@name,@unit,@qty,@price,@purchase,
                          @note,@extra,@sourceRow,@order,0,@formulas,@areaManual,@workPrice,
                          @costNote,@costColor,@workSell,@workCost)", conn, tx);
            insert.Parameters.Add("@system", SqlDbType.BigInt).Value = targetSystemId;
            insert.Parameters.Add("@parent", SqlDbType.BigInt).Value =
                p.ParentId is long pid && made.TryGetValue(pid, out var mappedParent) ? mappedParent : DBNull.Value;
            insert.Parameters.Add("@catalog", SqlDbType.BigInt).Value =
                p.CatalogItemId is long catalog ? catalog : DBNull.Value;
            insert.Parameters.Add("@num", SqlDbType.NVarChar, 32).Value = p.Num;
            insert.Parameters.Add("@basis", SqlDbType.NVarChar, 64).Value = p.Basis;
            insert.Parameters.Add("@name", SqlDbType.NVarChar, -1).Value = p.Name;
            insert.Parameters.Add("@unit", SqlDbType.NVarChar, 32).Value = p.Unit;
            insert.Parameters.Add("@qty", SqlDbType.Float).Value = p.Qty;
            insert.Parameters.Add("@price", SqlDbType.Float).Value = p.Price;
            insert.Parameters.Add("@purchase", SqlDbType.Float).Value =
                p.PurchasePrice is double purchase ? purchase : DBNull.Value;
            insert.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = p.Note;
            insert.Parameters.Add("@extra", SqlDbType.NVarChar, -1).Value = p.ExtraJson;
            insert.Parameters.Add("@sourceRow", SqlDbType.BigInt).Value =
                p.SourceRow is long sourceRow ? sourceRow : DBNull.Value;
            insert.Parameters.Add("@order", SqlDbType.BigInt).Value = p.Order;
            insert.Parameters.Add("@formulas", SqlDbType.NVarChar, -1).Value = p.Formulas;
            insert.Parameters.Add("@areaManual", SqlDbType.Float).Value =
                p.AreaManual is double area ? area : DBNull.Value;
            insert.Parameters.Add("@workPrice", SqlDbType.Float).Value =
                p.WorkPrice is double workPrice ? workPrice : DBNull.Value;
            insert.Parameters.Add("@costNote", SqlDbType.NVarChar, -1).Value = p.CostNote;
            insert.Parameters.Add("@costColor", SqlDbType.NVarChar, 16).Value = p.CostColor;
            insert.Parameters.Add("@workSell", SqlDbType.Float).Value =
                p.WorkSell is double workSell ? workSell : DBNull.Value;
            insert.Parameters.Add("@workCost", SqlDbType.Float).Value =
                p.WorkCost is double workCost ? workCost : DBNull.Value;
            if (insert.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Не удалось вставить позицию копии.");
            made[p.Id] = SqlServerIdentity(conn, tx);
        }
    }

    /// <summary>id последней вставленной записи в этом соединении (SCOPE_IDENTITY).</summary>
    private static long SqlServerIdentity(SqlConnection conn, SqlTransaction tx)
    {
        using var cmd = new SqlCommand("SELECT CONVERT(bigint, SCOPE_IDENTITY())", conn, tx);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Реквизиты документа для диалога свойств (та же запись, что и в SQLite-линии).</summary>
    public static ContractInfo ReadContract(SqlConnection conn, long id)
    {
        using var cmd = new SqlCommand(
            "SELECT id,object_id,kind,number,[date],name,company_id,base_contract_id,vat_rate,note,form," +
            "locked,stage_of,smr_mode,smr_percent,smr_amount,transport_amount,m2_mode,pnr_amount," +
            "transport_cost,transport_note,ks_site_part FROM dbo.contracts WHERE id=@id", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new ArgumentException("Документ не найден.");
        string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
        double Dbl(int i) => Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
        long? Id(int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
        return new ContractInfo(
            Id(0)!.Value, Id(1)!.Value, Text(2), Text(3), Text(4), Text(5),
            Id(6), Id(7), Dbl(8), Text(9), Text(10),
            Convert.ToBoolean(r.GetValue(11), CultureInfo.InvariantCulture), Id(12),
            Text(13), Dbl(14), Dbl(15), Dbl(16), Text(17), Dbl(18),
            r.IsDBNull(19) ? null : Dbl(19), Text(20), Text(21));
    }

    /// <summary>
    /// Реквизиты документа (services.update_contract / UpdateContract SQLite-линии): меняются
    /// только реально изменившиеся поля, журнал одной записью с перечислением; подписанные
    /// документы и этапы не меняются. При смене организации без явного НДС он берётся
    /// по организации. Возвращает true, если что-то изменилось.
    /// </summary>
    public static bool UpdateContract(string connectionString, long id,
        ContractProperties properties, string expectedRevision)
    {
        if (properties is null) throw new ArgumentNullException(nameof(properties));
        if (properties.Kind is not ("kp" or "main" or "dop"))
            throw new ArgumentException("Недопустимый тип документа.");
        var value = properties.Normalize();
        if (value.BaseContractId == id || value.BaseContractId is <= 0)
            throw new ArgumentException("Некорректная привязка исходного договора.");
        if (!Documents.IsValidDocumentDate(value.Date) || value.Number.Length > 64 ||
            value.Date.Length > 32 || value.Name.Length > 255 || value.Form.Length > 32 ||
            value.VatRate is < 0m or > 100m)
            throw new ArgumentException("Некорректные реквизиты документа.");
        RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);

        ContractInfo old;
        using (var current = new SqlCommand(
            "SELECT id,object_id,kind,number,[date],name,company_id,base_contract_id,vat_rate,note,form," +
            "locked,stage_of,smr_mode,smr_percent,smr_amount,transport_amount,m2_mode,pnr_amount," +
            "transport_cost,transport_note,ks_site_part " +
            "FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            current.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = current.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Документ не найден.");
            string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            double Dbl(int i) => Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture);
            long? Id(int i) => r.IsDBNull(i) ? null : Convert.ToInt64(r.GetValue(i), CultureInfo.InvariantCulture);
            old = new ContractInfo(
                Id(0)!.Value, Id(1)!.Value, Text(2), Text(3), Text(4), Text(5),
                Id(6), Id(7), Dbl(8), Text(9), Text(10),
                Convert.ToBoolean(r.GetValue(11), CultureInfo.InvariantCulture), Id(12),
                Text(13), Dbl(14), Dbl(15), Dbl(16), Text(17), Dbl(18),
                r.IsDBNull(19) ? null : Dbl(19), Text(20), Text(21));
        }
        if (old.Locked)
            throw new InvalidOperationException("Документ подписан. Изменение реквизитов недоступно.");
        if (old.StageOf is not null)
            throw new InvalidOperationException("Это этап другого документа. Реквизиты меняются у полного КП.");
        if (ReadObjectName(conn, tx, old.ObjectId).Archived)
            throw new InvalidOperationException("Архивный объект нельзя изменять.");

        // Preserve legacy unlinked DS when editing unrelated properties, but
        // never create a new broken link or change an existing link to null.
        var linkChanged = old.Kind != value.Kind || old.BaseContractId != value.BaseContractId;
        if (linkChanged && value.Kind == "dop")
        {
            if (value.BaseContractId is not long parentId)
                throw new ArgumentException("Для допсоглашения выбери исходный договор.");
            EnsureBaseContract(conn, tx, parentId, old.ObjectId);
        }
        if (old.Kind != "dop" && value.Kind == "dop")
        {
            using var children = new SqlCommand(
                "SELECT TOP(1) id FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE base_contract_id=@id OR stage_of=@id", conn, tx);
            children.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (children.ExecuteScalar() is not null)
                throw new InvalidOperationException("У документа есть дочерние документы. Сначала проверь связи.");
        }
        var legalEnabled = SqlServerAgreements.Enabled(conn, tx);
        var previousLegal = legalEnabled ? SqlServerAgreements.DocumentAgreementId(conn, id, tx) : null;
        var requestedLegal = legalEnabled
            ? AgreementEditPolicy.ResolveRequested(value.Kind, old.Kind, previousLegal, value.AgreementId,
                linkChanged, value.BaseContractId is long legalBase
                    ? SqlServerAgreements.DocumentAgreementId(conn, legalBase, tx) : null)
            : null;
        var legalChanged = legalEnabled && previousLegal != requestedLegal;
        if (legalChanged && previousLegal.HasValue)
        {
            using var children = new SqlCommand(
                "SELECT TOP(1) id FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE base_contract_id=@id OR stage_of=@id", conn, tx);
            children.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (children.ExecuteScalar() is not null)
                throw new InvalidOperationException("У документа есть дочерние документы. Перепривязка юридического договора запрещена.");
        }
        if (linkChanged || legalChanged)
        {
            using var signed = new SqlCommand(
                "SELECT TOP(1) id FROM dbo.acts WITH (UPDLOCK,HOLDLOCK) WHERE contract_id=@id AND signed=1", conn, tx);
            signed.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (signed.ExecuteScalar() is not null)
                throw new InvalidOperationException("Документ содержит подписанные КС-2. Изменение типа или привязки запрещено.");
        }

        var changes = new Dictionary<string, object?>();
        var details = new List<string>();
        // Формат деталей — как services.update_contract в Python-эталоне: «поле»: «старое» → «новое».
        void Compare(string field, object? before, object? after)
        {
            var same = before is null ? after is null : before.Equals(after);
            if (same) return;
            changes[field] = after;
            details.Add($"{field}: «{before ?? ""}» → «{after ?? ""}»");
        }

        Compare("kind", old.Kind, value.Kind);
        Compare("number", old.Number, value.Number);
        Compare("date", old.Date, value.Date);
        Compare("name", old.Name, value.Name);
        Compare("company_id", old.CompanyId, value.CompanyId);
        Compare("base_contract_id", old.BaseContractId, value.BaseContractId);

        // НДС: явно заданный — как есть; при смене организации — по организации, как в диалоге Python.
        var vat = (double)(value.VatRate ?? (decimal)old.VatRate);
        if (value.VatRate is null && value.CompanyId != old.CompanyId &&
            value.CompanyId is long newCompany && CompanyVat(conn, tx, newCompany) is double companyVat)
            vat = companyVat;
        Compare("vat_rate", old.VatRate, vat);

        Compare("note", old.Note, value.Note);
        Compare("form", old.Form, value.Form);

        if (legalEnabled)
        {
            if (requestedLegal is long chosen &&
                SqlServerAgreements.Read(conn, old.ObjectId, tx).All(a => a.Id != chosen))
                throw new ArgumentException("Выбранный договор относится к другому объекту или архивирован.");
            Compare("agreement_id", previousLegal, requestedLegal);
            if (value.AnnexNumber is not null && value.Kind == "main")
            {
                if(value.AnnexNumber.Length>200)throw new ArgumentException("Номер приложения превышает 200 символов.");
                Compare("annex_number",SqlServerAgreements.AnnexNumber(conn,id,tx),value.AnnexNumber.Trim());
            }
            else if (old.Kind == "main" && value.Kind != "main")
                Compare("annex_number", SqlServerAgreements.AnnexNumber(conn,id,tx), "");
        }
        if (changes.Count == 0) return false;

        var set = string.Join(", ", changes.Keys.Select(c => $"[{c}]=@{c}"));
        using var update = new SqlCommand($"UPDATE dbo.contracts SET {set} WHERE id=@id", conn, tx);
        update.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        foreach (var (field, after) in changes)
        {
            if (after is null) update.Parameters.AddWithValue($"@{field}", DBNull.Value);
            else if (after is double d) update.Parameters.AddWithValue($"@{field}", d);
            else if (after is long l) update.Parameters.AddWithValue($"@{field}", l);
            else update.Parameters.AddWithValue($"@{field}", after);
        }
        if (update.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Документ был удалён.");
        if (legalEnabled && (linkChanged || legalChanged))
            SqlServerAgreements.AttachNewDocument(conn, tx, id, requestedLegal);
        Log(conn, tx, "contract", id, "update", string.Join("; ", details), id, old.ObjectId);
        BumpRevision(conn, tx);
        tx.Commit();
        return true;
    }

    /// <summary>
    /// Этап подготовки КП (kp_workflow.set_phase эталона): только родительское КП,
    /// без этапов и блокировки; журнал — «Подготовка КП: старый → новый».
    /// </summary>
    public static bool SetKpPhase(string connectionString, long id, string phase, string expectedRevision)
    {
        RequirePilotDatabase(connectionString);
        if (!string.IsNullOrEmpty(phase) &&
            !KpWorkflowPhases.ContainsKey(phase))
            throw new ArgumentException("Неизвестный этап подготовки.");
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);

        string oldPhase = "", kind = "";
        long objectId = 0;
        bool locked = false;
        int? stageOf = null;
        using (var read = new SqlCommand(
            "SELECT kind, ISNULL(kp_phase,''), object_id, locked, stage_of " +
            "FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = read.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Документ не найден.");
            kind = r.IsDBNull(0) ? "" : r.GetString(0);
            oldPhase = r.IsDBNull(1) ? "" : r.GetString(1);
            objectId = Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture);
            locked = !r.IsDBNull(3) && Convert.ToBoolean(r.GetValue(3), CultureInfo.InvariantCulture);
            stageOf = r.IsDBNull(4) ? null : Convert.ToInt32(r.GetValue(4), CultureInfo.InvariantCulture);
        }
        if (kind != "kp" || stageOf is not null)
            throw new InvalidOperationException("Этап подготовки задаётся для родительского КП.");
        if (locked) throw new InvalidOperationException("Документ подписан — этап не меняется.");
        if (ReadObjectName(conn, tx, objectId).Archived)
            throw new InvalidOperationException("Архивный объект нельзя изменять.");
        if ((oldPhase ?? "") == (phase ?? "")) return false;

        using (var update = new SqlCommand(
            "UPDATE dbo.contracts SET kp_phase=@phase WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@phase", SqlDbType.NVarChar, 32).Value = (object?)phase ?? DBNull.Value;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (update.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Документ был удалён.");
        }
        var oldTitle = PhaseTitle(oldPhase);
        var newTitle = PhaseTitle(phase);
        Log(conn, tx, "contract", id, "update", $"Подготовка КП: {oldTitle} → {newTitle}", id, objectId);
        BumpRevision(conn, tx);
        tx.Commit();
        return true;
    }

    /// <summary>kp_workflow.PHASES: ключ → заголовок для журнала и кнопки.</summary>
    public static readonly IReadOnlyDictionary<string, string> KpWorkflowPhases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["volumes"] = "Объёмы · Дима",
            ["prices"] = "Цены · Наташа",
            ["review"] = "СМР и проверка · Андрей Викторович",
            ["ready"] = "Готово к отправке",
            ["sent"] = "Отправлено заказчику",
        };

    public static string PhaseTitle(string? phase) =>
        phase is not null && KpWorkflowPhases.TryGetValue(phase, out var title)
            ? title : "Этап подготовки не указан";

    /// <summary>
    /// Заблокировать / разблокировать смету (toggle_lock эталона): подписанные цены
    /// и строки не меняются случайно. locked_by = текущий пользователь Windows.
    /// </summary>
    public static bool SetContractLocked(string connectionString, long id, bool locked, string expectedRevision)
    {
        RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);

        long objectId = 0;
        string title = "";
        bool wasLocked = false;
        using (var read = new SqlCommand(
            "SELECT object_id, kind, ISNULL(number,''), ISNULL(name,''), ISNULL(locked,0), ISNULL(stage_rest,0) " +
            "FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = read.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Документ не найден.");
            objectId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            title = KindTitle(r.IsDBNull(1) ? "" : r.GetString(1)) +
                    (r.IsDBNull(2) || r.GetString(2).Length == 0 ? "" : $" № {r.GetString(2)}") +
                    (r.IsDBNull(3) || r.GetString(3).Length == 0 ? "" : $" — {r.GetString(3)}");
            wasLocked = !r.IsDBNull(4) && Convert.ToBoolean(r.GetValue(4), CultureInfo.InvariantCulture);
            if (Convert.ToBoolean(r.GetValue(5), CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Расчётный остаток нельзя разблокировать или подписать.");
        }
        if (ReadObjectName(conn, tx, objectId).Archived)
            throw new InvalidOperationException("Архивный объект нельзя изменять.");
        if (wasLocked == locked) return false;
        var user = Environment.UserName;
        using (var update = new SqlCommand(
            "UPDATE dbo.contracts SET locked=@locked, locked_by=@who, locked_at=@at WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@locked", SqlDbType.Bit).Value = locked;
            update.Parameters.Add("@who", SqlDbType.NVarChar, 64).Value = locked ? user : (object)DBNull.Value;
            update.Parameters.Add("@at", SqlDbType.DateTime2).Value = locked ? DateTime.Now : (object)DBNull.Value;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            if (update.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Документ был удалён.");
        }
        Log(conn, tx, "contract", id, "update",
            locked ? $"{title}: смета подписана — заблокирована" : $"{title}: блокировка снята",
            id, objectId);
        BumpRevision(conn, tx);
        tx.Commit();
        return true;
    }

    private static string KindTitle(string kind) => kind switch
    {
        "main" => "Договор",
        "dop" => "Допсоглашение",
        _ => "КП"
    };

    /// <summary>Завершить объект (в архив) или вернуть в работу — finish_object эталона.</summary>
    public static void SetObjectArchived(string connectionString, long objectId, bool archived, string expectedRevision)
    {
        RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);

        string name;
        using (var current = new SqlCommand(
            "SELECT name,archived FROM dbo.objects WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            current.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
            using var reader = current.ExecuteReader();
            if (!reader.Read()) throw new ArgumentException("Объект не найден.");
            name = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "";
            var wasArchived = !reader.IsDBNull(1) && Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (wasArchived == archived) return; // preserve the original archive date/year
        }

        using (var update = new SqlCommand(
            "UPDATE dbo.objects SET archived=@archived, archived_on=@on WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@archived", SqlDbType.Bit).Value = archived;
            update.Parameters.Add("@on", SqlDbType.Date).Value =
                archived ? DateTime.Today.Date : DBNull.Value;
            if (update.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Объект был удалён.");
        }
        Log(conn, tx, "object", objectId, archived ? "archive" : "unarchive", name, null, objectId);
        BumpRevision(conn, tx);
        tx.Commit();
    }

    /// <summary>Изменить год в архиве (change_archive_year эталона). 29 февраля переносится на 28-е.</summary>
    public static void ChangeArchiveYear(string connectionString, long objectId, int year, string expectedRevision)
    {
        RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);

        string name; bool archived; DateTime archivedOn = DateTime.Today;
        using (var current = new SqlCommand(
            "SELECT name,archived,archived_on FROM dbo.objects WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx))
        {
            current.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
            using var r = current.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Объект не найден.");
            name = r.GetString(0);
            archived = Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture);
            if (!r.IsDBNull(2)) archivedOn = Convert.ToDateTime(r.GetValue(2), CultureInfo.InvariantCulture);
        }
        if (!archived) throw new InvalidOperationException("Объект не в архиве.");

        var currentYear = archivedOn.Year;
        DateOnly target;
        try { target = new DateOnly(year, archivedOn.Month, archivedOn.Day); }
        catch (ArgumentOutOfRangeException) { target = new DateOnly(year, 2, 28); }   // 29 февраля

        using (var update = new SqlCommand(
            "UPDATE dbo.objects SET archived_on=@on WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@on", SqlDbType.Date).Value = target.ToDateTime(TimeOnly.MinValue);
            if (update.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Объект был удалён.");
        }
        Log(conn, tx, "object", objectId, "archive_year", $"{name}: {currentYear} → {year}", null, objectId);
        BumpRevision(conn, tx);
        tx.Commit();
    }

    // ---------------------------------------------------------------- удаление и корзина

    /// <summary>
    /// Удалить объект: полная копия в корзину, журнал до удаления и явное удаление зависимых
    /// записей детьми первыми (на ON DELETE CASCADE не полагаемся) — одна транзакция
    /// (services.delete_object). Связи без каскада (metal_entries, price_history) очищаются.
    /// </summary>
    public static void DeleteObject(string connectionString, long objectId, string expectedRevision,
        IReadOnlyList<long>? expectedDocuments = null)
    {
        RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);
        var info = DescribeObjectForDelete(conn, tx, objectId);
        if (expectedDocuments is not null &&
            !ReadDeletionDocumentIds(conn, tx, objectId).SequenceEqual(expectedDocuments.OrderBy(id => id)))
            throw new DBConcurrencyException("Состав документов изменился после подтверждения.");
        SqlServerTrash.KeepObject(conn, tx, objectId);
        // Журнал до удаления: после DELETE владелец уже не определяется.
        Log(conn, tx, "object", objectId, "delete", info.Name, null, objectId);

        // Порядок как в Python delete_sql_document_tree: строки актов раньше позиций
        // (FK NO ACTION), дети раньше родителей.
        Execute(conn, tx, "UPDATE dbo.metal_entries SET object_id=NULL WHERE object_id=@id", objectId);
        Execute(conn, tx, "UPDATE dbo.price_history SET contract_id=NULL WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id)", objectId);
        Execute(conn, tx, "DELETE dbo.act_lines WHERE act_id IN " +
            "(SELECT id FROM dbo.acts WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id))", objectId);
        Execute(conn, tx, "DELETE dbo.shipment_lines WHERE shipment_id IN " +
            "(SELECT id FROM dbo.shipments WHERE object_id=@id)", objectId);
        Execute(conn, tx, "DELETE dbo.shipment_lines WHERE position_id IN " +
            "(SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id)))", objectId);
        Execute(conn, tx, "DELETE dbo.procurement WHERE position_id IN " +
            "(SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id)))", objectId);
        Execute(conn, tx, "DELETE dbo.movements WHERE position_id IN " +
            "(SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id)))", objectId);
        Execute(conn, tx, "DELETE dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id))", objectId);
        Execute(conn, tx, "DELETE dbo.systems WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id)", objectId);
        Execute(conn, tx, "DELETE dbo.acts WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id)", objectId);
        Execute(conn, tx, "DELETE dbo.smr_costs WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id)", objectId);
        Execute(conn, tx, "DELETE dbo.attachments WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id)", objectId);
        Execute(conn, tx, "DELETE dbo.spec_revisions WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id)", objectId);
        Execute(conn, tx, "DELETE dbo.contracts WHERE object_id=@id", objectId);
        Execute(conn, tx, "DELETE dbo.access_lists WHERE object_id=@id", objectId);
        Execute(conn, tx, "DELETE dbo.shipments WHERE object_id=@id", objectId);
        if (SqlServerAgreements.Enabled(conn, tx))
        {
            Execute(conn, tx, "DELETE dbo.agreement_files WHERE agreement_id IN (SELECT id FROM dbo.agreements WHERE object_id=@id)", objectId);
            Execute(conn, tx, "DELETE dbo.agreements WHERE object_id=@id", objectId);
        }
        RequireSingleDeletion(conn, tx, "objects", objectId);
        BumpRevision(conn, tx);
        CommitDeletion(tx);
    }

    /// <summary>Удалить документ: полная копия в корзину, журнал и явное удаление зависимых записей — одна транзакция (services.delete_contract).</summary>
    public static void DeleteContract(string connectionString, long contractId, string expectedRevision,
        long? expectedObjectId = null)
    {
        RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        CheckRevision(conn, tx, expectedRevision);
        var info = DescribeContractForDelete(conn, tx, contractId);
        SqlServerTrash.KeepContract(conn, tx, contractId, expectedObjectId: expectedObjectId);
        Log(conn, tx, "contract", contractId, "delete", $"{info.ObjectName}: {info.Title}", contractId, null);

        // Порядок как в Python delete_sql_document_tree: строки актов раньше позиций
        // (FK NO ACTION), дети раньше родителей.
        Execute(conn, tx, "UPDATE dbo.price_history SET contract_id=NULL WHERE contract_id=@id", contractId);
        Execute(conn, tx, "DELETE dbo.act_lines WHERE act_id IN " +
            "(SELECT id FROM dbo.acts WHERE contract_id=@id)", contractId);
        Execute(conn, tx, "DELETE dbo.shipment_lines WHERE position_id IN " +
            "(SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id))", contractId);
        Execute(conn, tx, "DELETE dbo.procurement WHERE position_id IN " +
            "(SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id))", contractId);
        Execute(conn, tx, "DELETE dbo.movements WHERE position_id IN " +
            "(SELECT id FROM dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id))", contractId);
        Execute(conn, tx, "DELETE dbo.positions WHERE system_id IN " +
            "(SELECT id FROM dbo.systems WHERE contract_id=@id)", contractId);
        Execute(conn, tx, "DELETE dbo.systems WHERE contract_id=@id", contractId);
        Execute(conn, tx, "DELETE dbo.acts WHERE contract_id=@id", contractId);
        Execute(conn, tx, "DELETE dbo.smr_costs WHERE contract_id=@id", contractId);
        Execute(conn, tx, "DELETE dbo.attachments WHERE contract_id=@id", contractId);
        Execute(conn, tx, "DELETE dbo.spec_revisions WHERE contract_id=@id", contractId);
        RequireSingleDeletion(conn, tx, "contracts", contractId);
        BumpRevision(conn, tx);
        CommitDeletion(tx);
    }

    private static void Execute(SqlConnection conn, SqlTransaction tx, string sql, long id)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        cmd.ExecuteNonQuery();
    }

    private static void AddObjectFields(SqlCommand cmd, ObjectProperties v)
    {
        cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = v.Name;
        cmd.Parameters.Add("@customer", SqlDbType.NVarChar, 255).Value = v.Customer;
        cmd.Parameters.Add("@address", SqlDbType.NVarChar, 255).Value = v.Address;
        cmd.Parameters.Add("@folder", SqlDbType.NVarChar, 500).Value = v.Folder;
        cmd.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = v.Note;
        cmd.Parameters.Add("@form", SqlDbType.NVarChar, 32).Value = v.Form;
    }

    internal static void CheckRevision(SqlConnection conn, SqlTransaction tx, string expected)
    {
        using var cmd = new SqlCommand(
            "SELECT [value] FROM dbo.meta WITH (UPDLOCK,HOLDLOCK) WHERE [key]=N'rev'", conn, tx);
        var actual = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
            throw new InvalidOperationException("На сервере отсутствует корректная meta.rev или снимок устарел.");
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new DBConcurrencyException("Данные изменены другим пользователем. Обнови список.");
    }

    internal static void BumpRevision(SqlConnection conn, SqlTransaction tx)
    {
        var newRevision = Guid.NewGuid().ToString("N");
        using var cmd = new SqlCommand(
            "UPDATE dbo.meta SET [value]=@new WHERE [key]=N'rev'", conn, tx);
        cmd.Parameters.Add("@new", SqlDbType.NVarChar, -1).Value = newRevision;
        if (cmd.ExecuteNonQuery() != 1)
            throw new DBConcurrencyException("Не удалось обновить meta.rev.");
    }

    private static void Log(SqlConnection conn, SqlTransaction tx,
        string entity, long id, string action, string details, long? contractId, long? objectId)
    {
        using var cmd = new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,@entity,@id,@action,@details,@contract,@obj)", conn, tx);
        cmd.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
        cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName[..Math.Min(64, Environment.UserName.Length)];
        cmd.Parameters.Add("@entity", SqlDbType.NVarChar, 32).Value = entity;
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        cmd.Parameters.Add("@action", SqlDbType.NVarChar, 32).Value = action;
        cmd.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId is null ? DBNull.Value : contractId.Value;
        cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId is null ? DBNull.Value : objectId.Value;
        cmd.ExecuteNonQuery();
    }
}
