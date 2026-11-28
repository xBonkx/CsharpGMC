using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed record ExcelImportChoice(ExcelImportBlock Block, string Kind, string Number,
    string Date, string Name, long? BaseContractId = null, long? CompanyId = null)
{
    public long? AgreementId { get; init; }
}

public sealed record ExcelImportSaved(long ObjectId, IReadOnlyList<long> ContractIds, string Revision);

/// <summary>
/// New-document Excel import only. Creates an object (optional), documents, sections,
/// price catalog entries and positions in ONE serializable transaction. No existing
/// document/position is overwritten. Only isolated GmcSmetaPilot_* databases are writable.
/// </summary>
public static class SqlServerExcelImport
{
    public static IReadOnlySet<string> AlreadyLoaded(string connectionString, long objectId)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var command = new SqlCommand(
            "SELECT source_file,source_sheet FROM dbo.contracts WHERE object_id=@obj AND source_file<>N''", conn);
        command.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var file = reader.IsDBNull(0) ? "" : Path.GetFileName(reader.GetString(0).Replace('\\', '/'));
            var sheet = reader.IsDBNull(1) ? "" : reader.GetString(1);
            result.Add(file + "|" + sheet);
        }
        return result;
    }

    public static ExcelImportSaved Import(string connectionString, long? existingObjectId, string newObjectName,
        string fileName, IReadOnlyList<ExcelImportChoice> choices, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (choices is null || choices.Count == 0) throw new ArgumentException("Не выбраны КП.");
        if (existingObjectId is null && string.IsNullOrWhiteSpace(newObjectName))
            throw new ArgumentException("Укажи название нового объекта.");
        if (newObjectName.Length > 255) throw new ArgumentException("Название объекта длиннее 255 символов.");
        if (Path.GetFileName(fileName).Length > 500) throw new ArgumentException("Длинное имя файла.");
        foreach (var c in choices)
        {
            if (c.Block.PositionCount == 0) throw new ArgumentException("Пустой блок нельзя импортировать.");
            SqlServerWorkspace.ValidateContract(
                new SqlServerWorkspace.NewContractData(c.Kind, c.Number, c.Date, c.Name, c.CompanyId, c.BaseContractId));
            ExcelImportAgreementPolicy.Validate(c);
            if (c.Name.Length > 255 || c.Block.Sheet.Length > 255)
                throw new ArgumentException("Название документа или листа слишком длинное.");
            foreach (var section in c.Block.Sections)
            {
                if (string.IsNullOrWhiteSpace(section.Name) || section.Name.Length > 255 ||
                    section.Heading.Length > 255 || section.SourceNumber.Length > 32)
                    throw new ArgumentException("Название/номер раздела слишком длинные.");
                foreach (var pos in section.Positions.SelectMany(p => new[] { p }.Concat(p.Children)))
                {
                    if (string.IsNullOrWhiteSpace(pos.Name) || pos.Name.Length > 2000 ||
                        pos.Unit.Length > 32 || pos.Number.Length > 32 || pos.Note.Length > 10000 ||
                        pos.Quantity < 0 || pos.Price < 0 || pos.PurchasePrice < 0 || pos.WorkPrice < 0 ||
                        Catalog.Key(pos.Name, pos.Unit).Length > 600)
                        throw new ArgumentException($"Недопустимая строка {pos.SourceRow}: {pos.Name}");
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        long objectId;
        if (existingObjectId is long id)
        {
            using var check = new SqlCommand(
                "SELECT archived FROM dbo.objects WITH (UPDLOCK,HOLDLOCK) WHERE id=@id", conn, tx);
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            var state = check.ExecuteScalar();
            if (state is null) throw new ArgumentException("Объект не найден.");
            if (Convert.ToBoolean(state, CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Нельзя добавлять КП в архивный объект.");
            objectId = id;
            using var existing = new SqlCommand(
                "SELECT source_file,source_sheet FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE object_id=@obj AND source_file<>N''", conn, tx);
            existing.Parameters.Add("@obj", SqlDbType.BigInt).Value = id;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = existing.ExecuteReader())
                while (reader.Read())
                    seen.Add(Path.GetFileName(reader.GetString(0).Replace('\\', '/')) + "|" +
                        (reader.IsDBNull(1) ? "" : reader.GetString(1)));
            foreach (var c in choices)
                if (seen.Contains(Path.GetFileName(fileName) + "|" + c.Block.Sheet))
                    throw new InvalidOperationException(
                        $"«{Path.GetFileName(fileName)}», лист «{c.Block.Sheet}» уже загружен в объект. " +
                        "Повторный импорт заблокирован во избежание дублей.");
        }
        else
        {
            using var create = new SqlCommand(
                @"INSERT dbo.objects
                  (name,customer,address,folder,note,created_at,customer_full,site_full,
                   cust_org,cust_post,cust_name,second_role,second_org,second_post,second_name,
                   work_title,archived,form,archived_on,works_start)
                  OUTPUT INSERTED.id
                  VALUES (@name,N'',N'',N'',N'',SYSDATETIME(),N'',N'',
                          N'',N'',N'',N'',N'',N'',N'',N'Монтаж системы вентиляции',0,N'',NULL,N'')", conn, tx);
            create.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = newObjectName.Trim();
            objectId = Convert.ToInt64(create.ExecuteScalar(), CultureInfo.InvariantCulture);
            Log(conn, tx, "object", objectId, "create", newObjectName.Trim(), null, objectId);
        }
        // The legal schema uses explicit identities, never a match by number.
        var legal = ReadLegalImportTargets(conn, tx);
        var targetCards = legal?.Where(x => x.ObjectId == objectId).ToArray();
        if (targetCards is not null)
        {
            var annexes = choices.Where(x => x.Kind == "main").ToArray();
            foreach (var choice in annexes)
                ExcelImportAgreementPolicy.ValidateLegalTarget(choice, targetCards);
            if (targetCards.Length == 0)
                ExcelImportAgreementPolicy.ValidateNewDraftBatch(annexes);
        }
        // Validate every parent and every duplicate before inserting any document.
        foreach (var choice in choices.Where(c => c.Kind == "dop"))
        {
            using var check = new SqlCommand(
                "SELECT company_id FROM dbo.contracts WITH (HOLDLOCK) " +
                "WHERE id=@parent AND object_id=@obj AND kind IN (N'main',N'kp') AND stage_of IS NULL", conn, tx);
            check.Parameters.Add("@parent", SqlDbType.BigInt).Value = choice.BaseContractId!.Value;
            check.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
            var value = check.ExecuteScalar();
            var parentCompany = value is null or DBNull ? (long?)null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            ExcelImportAgreementPolicy.ValidateBaseCompany(choice, parentCompany);
            if (targetCards is not null)
            {
                var parentLegal = SqlServerAgreements.DocumentAgreementId(conn, choice.BaseContractId.Value, tx);
                if (parentLegal is null || !targetCards.Any(a => a.Id == parentLegal && !a.Archived))
                    throw new InvalidOperationException("У основания ДС нет действующего юридического договора. Сначала проверьте его реквизиты.");
            }
        }
        var companies = new Dictionary<long, decimal>();
        long? defaultCompanyId = null;
        using (var co = new SqlCommand(
            "SELECT id,vat_rate FROM dbo.companies WITH (HOLDLOCK) ORDER BY [order],id", conn, tx))
        using (var r = co.ExecuteReader())
            while (r.Read())
            {
                var directoryCompanyId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                defaultCompanyId ??= directoryCompanyId;
                companies.Add(directoryCompanyId, r.IsDBNull(1) ? 22m : Convert.ToDecimal(r.GetValue(1), CultureInfo.InvariantCulture));
            }
        var resolvedCompanies = choices.Select(c =>
            ExcelImportAgreementPolicy.ResolveCompany(c, defaultCompanyId, companies)).ToArray();
        using var temp = new SqlCommand(
            @"CREATE TABLE #excel_rows (
                idx INT NOT NULL PRIMARY KEY, parent_idx INT NULL, system_id BIGINT NOT NULL,
                row_order INT NOT NULL, catalog_key NVARCHAR(600) COLLATE DATABASE_DEFAULT NOT NULL,
                num NVARCHAR(32) NOT NULL, basis NVARCHAR(64) NOT NULL, name NVARCHAR(MAX) NOT NULL,
                unit NVARCHAR(32) NOT NULL, qty FLOAT NOT NULL, price FLOAT NOT NULL,
                purchase_price FLOAT NULL, work_price FLOAT NULL, note NVARCHAR(MAX) NOT NULL,
                extra_json NVARCHAR(MAX) NOT NULL, source_row INT NOT NULL);
              CREATE TABLE #excel_ids (idx INT NOT NULL PRIMARY KEY,id BIGINT NOT NULL);", conn, tx);
        temp.ExecuteNonQuery();
        var data = NewPositionTable();
        var created = new List<long>();
        var seq = 0;
        var nextOrder = 0;
        using (var orderCommand = new SqlCommand(
            "SELECT COALESCE(MAX([order]),0) FROM dbo.contracts WHERE object_id=@obj", conn, tx))
        {
            orderCommand.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
            nextOrder = Convert.ToInt32(orderCommand.ExecuteScalar(), CultureInfo.InvariantCulture) + 1;
        }
        for (var choiceIndex = 0; choiceIndex < choices.Count; choiceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var choice = choices[choiceIndex];
            var (companyId, companyVat) = resolvedCompanies[choiceIndex];
            var b = choice.Block;
            var smrMode = b.HasWork ? "pos" :
                b.Sections.Any(s => s.Smr.HasValue) &&
                (b.Smr is null || Math.Abs(b.Sections.Sum(s => s.Smr ?? 0m) - b.Smr.Value) <= 1m)
                ? "systems" : "sum";
            using var contract = new SqlCommand(
                @"INSERT dbo.contracts
                  (object_id,kind,name,ks_site_part,number,[date],vat_rate,smr_amount,
                   transport_amount,declared_material,declared_total,source_file,source_sheet,
                   [order],created_at,company_id,base_contract_id,smr_mode,smr_percent,note,
                   kp_phase,stage_rest,form,m2_mode,pnr_amount,transport_note,locked,locked_by)
                  OUTPUT INSERTED.id
                  VALUES (@obj,@kind,@name,N'',@number,@date,@vat,@smr,
                          @transport,@declmat,@decltotal,@source,@sheet,@order,SYSDATETIME(),
                          @company,@base,@mode,0,N'',@phase,0,@form,N'',@pnr,N'',0,N'')", conn, tx);
            contract.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
            contract.Parameters.Add("@kind", SqlDbType.NVarChar, 16).Value = choice.Kind;
            contract.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = choice.Name.Trim();
            contract.Parameters.Add("@number", SqlDbType.NVarChar, 64).Value = choice.Number.Trim();
            contract.Parameters.Add("@date", SqlDbType.NVarChar, 32).Value = choice.Date.Trim();
            contract.Parameters.Add("@vat", SqlDbType.Float).Value = (double)(b.Form == "dns" ? b.VatRate : companyVat);
            contract.Parameters.Add("@smr", SqlDbType.Float).Value = (double)(b.Smr ?? 0m);
            contract.Parameters.Add("@transport", SqlDbType.Float).Value = (double)(b.Transport ?? 0m);
            contract.Parameters.Add("@declmat", SqlDbType.Float).Value = b.DeclaredMaterial is decimal mat ? (double)mat : DBNull.Value;
            contract.Parameters.Add("@decltotal", SqlDbType.Float).Value = b.DeclaredTotal is decimal total ? (double)total : DBNull.Value;
            contract.Parameters.Add("@source", SqlDbType.NVarChar, 500).Value = Path.GetFileName(fileName);
            contract.Parameters.Add("@sheet", SqlDbType.NVarChar, 255).Value = b.Sheet;
            contract.Parameters.Add("@order", SqlDbType.Int).Value = nextOrder++;
            contract.Parameters.Add("@company", SqlDbType.BigInt).Value = companyId is long co ? co : DBNull.Value;
            contract.Parameters.Add("@base", SqlDbType.BigInt).Value = choice.BaseContractId is long parent ? parent : DBNull.Value;
            contract.Parameters.Add("@mode", SqlDbType.NVarChar, 8).Value = smrMode;
            contract.Parameters.Add("@phase", SqlDbType.NVarChar, 24).Value = choice.Kind == "kp" ? "volumes" : "";
            contract.Parameters.Add("@form", SqlDbType.NVarChar, 32).Value = b.Form;
            contract.Parameters.Add("@pnr", SqlDbType.Float).Value = (double)(b.Pnr ?? 0m);
            var contractId = Convert.ToInt64(contract.ExecuteScalar(), CultureInfo.InvariantCulture);
            SqlServerAgreements.AttachNewDocument(conn, tx, contractId, choice.AgreementId, validateNewAnnex: true);
            created.Add(contractId);
            var sections = new Dictionary<string, long>(StringComparer.Ordinal);
            for (var sIndex = 0; sIndex < b.Sections.Count; sIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var section = b.Sections[sIndex];
                var parentId = sections.TryGetValue(section.Parent, out var mappedSectionParent) ? (long?)mappedSectionParent : null;
                using var insertSection = new SqlCommand(
                    @"INSERT dbo.systems (contract_id,parent_id,name,[order],smr_amount,heading,source_num,stage_sys_src)
                      OUTPUT INSERTED.id VALUES (@contract,@parent,@name,@order,@smr,@heading,@source,NULL)", conn, tx);
                insertSection.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                insertSection.Parameters.Add("@parent", SqlDbType.BigInt).Value = parentId is long sid ? sid : DBNull.Value;
                insertSection.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = section.Name;
                insertSection.Parameters.Add("@order", SqlDbType.Int).Value = sIndex;
                insertSection.Parameters.Add("@smr", SqlDbType.Float).Value = section.Smr is decimal smr ? (double)smr : DBNull.Value;
                insertSection.Parameters.Add("@heading", SqlDbType.NVarChar, 255).Value = section.Heading;
                insertSection.Parameters.Add("@source", SqlDbType.NVarChar, 32).Value = section.SourceNumber;
                var systemId = Convert.ToInt64(insertSection.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (section.Positions.Count == 0) sections[section.Name] = systemId;
                var rowOrder = 0;
                foreach (var p in section.Positions)
                {
                    var parentIdx = seq;
                    AddPosition(data, seq++, null, systemId, rowOrder++, p);
                    foreach (var child in p.Children)
                        AddPosition(data, seq++, parentIdx, systemId, rowOrder++, child);
                }
            }
            Log(conn, tx, "contract", contractId, "import",
                $"{Path.GetFileName(fileName)} / {b.Sheet} / строки {b.FirstRow}–{b.LastRow}: {choice.Name}", contractId, objectId);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using (var copy = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx))
        {
            copy.DestinationTableName = "#excel_rows";
            copy.BatchSize = 500;
            copy.BulkCopyTimeout = 180;
            foreach (DataColumn col in data.Columns) copy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            copy.WriteToServer(data);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using (var catalog = new SqlCommand(
            @"WITH source AS (
                SELECT catalog_key,MIN(name) AS name,MIN(unit) AS unit
                FROM #excel_rows GROUP BY catalog_key)
              INSERT dbo.catalog_items ([key],name,unit)
              SELECT src.catalog_key,src.name,src.unit FROM source src
              WHERE NOT EXISTS (
                SELECT 1 FROM dbo.catalog_items ci WITH (UPDLOCK,HOLDLOCK) WHERE ci.[key]=src.catalog_key)", conn, tx))
            catalog.ExecuteNonQuery();

        // MERGE is used ONLY for INSERT: unlike INSERT..OUTPUT it can reliably output the
        // source idx, allowing children to reference their actual parent identity.
        using (var insertTop = new SqlCommand(
            @"MERGE dbo.positions AS target
              USING (SELECT r.*,c.id AS catalog_id FROM #excel_rows r
                     JOIN dbo.catalog_items c ON c.[key]=r.catalog_key WHERE r.parent_idx IS NULL) AS src
              ON 1=0
              WHEN NOT MATCHED THEN INSERT
                (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,purchase_price,
                 note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual,
                 work_price,cost_note,cost_color,work_sell,work_cost)
              VALUES (src.system_id,NULL,src.catalog_id,src.num,src.basis,src.name,src.unit,src.qty,
                      src.price,src.purchase_price,src.note,src.extra_json,src.source_row,src.row_order,
                      0,N'',NULL,NULL,src.work_price,N'',N'',NULL,NULL)
              OUTPUT src.idx,INSERTED.id INTO #excel_ids(idx,id);", conn, tx))
        {
            insertTop.CommandTimeout = 180;
            var expected = choices.Sum(c => c.Block.Sections.Sum(s => s.Positions.Count));
            if (insertTop.ExecuteNonQuery() != expected)
                throw new DBConcurrencyException("Не все основные позиции записаны. Транзакция отменена.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        using (var insertChildren = new SqlCommand(
            @"INSERT dbo.positions
                (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,purchase_price,
                 note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual,
                 work_price,cost_note,cost_color,work_sell,work_cost)
              SELECT r.system_id,p.id,c.id,r.num,r.basis,r.name,r.unit,r.qty,r.price,r.purchase_price,
                     r.note,r.extra_json,r.source_row,r.row_order,0,N'',NULL,NULL,r.work_price,N'',N'',NULL,NULL
              FROM #excel_rows r JOIN #excel_ids p ON p.idx=r.parent_idx
              JOIN dbo.catalog_items c ON c.[key]=r.catalog_key WHERE r.parent_idx IS NOT NULL", conn, tx))
        {
            insertChildren.CommandTimeout = 180;
            var children = seq - choices.Sum(c => c.Block.Sections.Sum(s => s.Positions.Count));
            if (insertChildren.ExecuteNonQuery() != children)
                throw new DBConcurrencyException("Не все комплектующие записаны. Транзакция отменена.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
        var revision = SqlServerDatabase.Revision(conn);
        return new ExcelImportSaved(objectId, created, revision);
    }

    // null = legacy schema; empty = legal schema with no cards.
    public static IReadOnlyList<ExcelImportLegalTarget>? ReadLegalImportTargets(SqlConnection conn, SqlTransaction? tx = null)
    {
        if (!SqlServerAgreements.Enabled(conn, tx)) return null;
        using var cmd = new SqlCommand(
            "SELECT a.id,a.object_id,a.number,CONVERT(nvarchar(10),a.agreement_date,104),a.company_id,a.archived " +
            "FROM dbo.agreements a JOIN dbo.objects o ON o.id=a.object_id WHERE o.archived=0 ORDER BY a.object_id,a.id", conn, tx);
        using var r = cmd.ExecuteReader();
        var rows = new List<ExcelImportLegalTarget>();
        while (r.Read())
            rows.Add(new(SqlInteger.ReadInt64(r,0),SqlInteger.ReadInt64(r,1),r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),r.IsDBNull(4) ? null : SqlInteger.ReadInt64(r,4),SqlFlag.Read(r,5)));
        return rows;
    }

    private static DataTable NewPositionTable()
    {
        var t = new DataTable();
        foreach (var (name, type) in new (string, Type)[] {
            ("idx",typeof(int)),("parent_idx",typeof(int)),("system_id",typeof(long)),
            ("row_order",typeof(int)),("catalog_key",typeof(string)),("num",typeof(string)),
            ("basis",typeof(string)),("name",typeof(string)),("unit",typeof(string)),
            ("qty",typeof(double)),("price",typeof(double)),("purchase_price",typeof(double)),
            ("work_price",typeof(double)),("note",typeof(string)),("extra_json",typeof(string)),
            ("source_row",typeof(int)) }) t.Columns.Add(name, type);
        return t;
    }

    private static void AddPosition(DataTable table, int idx, int? parent, long sectionId, int order,
        ExcelImportPosition position)
    {
        var extra = position.Extra.Count == 0 ? "" : JsonSerializer.Serialize(position.Extra);
        var basis = position.Extra.TryGetValue("basis", out var b) ? b[..Math.Min(64,b.Length)] : "";
        table.Rows.Add(idx, parent is int id ? id : DBNull.Value, sectionId, order,
            Catalog.Key(position.Name, position.Unit), position.Number, basis, position.Name,
            position.Unit, (double)position.Quantity, (double)position.Price,
            position.PurchasePrice is decimal purchase ? (double)purchase : DBNull.Value,
            position.WorkPrice is decimal work ? (double)work : DBNull.Value,
            position.Note, extra, position.SourceRow);
    }

    private static void Log(SqlConnection conn, SqlTransaction tx, string entity,
        long id, string action, string details, long? contract, long? obj)
    {
        using var command = new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (SYSDATETIME(),@user,@entity,@id,@action,@details,@contract,@object)", conn, tx);
        command.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName[..Math.Min(64, Environment.UserName.Length)];
        command.Parameters.Add("@entity", SqlDbType.NVarChar, 32).Value = entity;
        command.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        command.Parameters.Add("@action", SqlDbType.NVarChar, 32).Value = action;
        command.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        command.Parameters.Add("@contract", SqlDbType.BigInt).Value = contract is long c ? c : DBNull.Value;
        command.Parameters.Add("@object", SqlDbType.BigInt).Value = obj is long o ? o : DBNull.Value;
        command.ExecuteNonQuery();
    }
}
