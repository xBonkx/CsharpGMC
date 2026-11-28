using System.Data;
using System.Globalization;
using System.IO;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Append parsed Excel sections into an existing, unlocked SQL Server estimate.
/// Never changes/removes an existing section or position; checks revision and imported
/// block fingerprints. All new sections and rows are written in one transaction.
/// </summary>
public static class SqlServerExcelAppend
{
    public static SqlServerDatabase.SaveResult Append(string connectionString, long contractId,
        string sourceFile, IReadOnlyList<ExcelImportBlock> blocks, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (contractId <= 0 || blocks is null || blocks.Count == 0)
            return SqlServerDatabase.SaveResult.Fail("Нет выбранных блоков Excel.", expectedRevision);
        if (blocks.Sum(b => b.PositionCount) > 100_000)
            throw new ArgumentException("Слишком много позиций в одном импорте.");
        foreach (var b in blocks)
        {
            if (b.PositionCount == 0) throw new ArgumentException("Пустой блок Excel.");
            foreach (var s in b.Sections)
            {
                if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 255 ||
                    s.Heading.Length > 255 || s.SourceNumber.Length > 32)
                    throw new ArgumentException("Недопустимый раздел.");
                foreach (var p in s.Positions.SelectMany(p => new[] { p }.Concat(p.Children)))
                    if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 2000 ||
                        p.Unit.Length > 32 || p.Number.Length > 32 || p.Note.Length > 10_000 ||
                        p.Quantity < 0 || p.Price < 0 || p.PurchasePrice < 0 || p.WorkPrice < 0 ||
                        Catalog.Key(p.Name, p.Unit).Length > 600)
                        throw new ArgumentException($"Недопустимые данные строки Excel {p.SourceRow}.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        long objectId;
        using (var document = new SqlCommand(
            @"SELECT c.object_id,c.locked,o.archived FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK)
              JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id WHERE c.id=@contract", conn, tx))
        {
            document.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var reader = document.ExecuteReader();
            if (!reader.Read()) throw new ArgumentException("Документ отсутствует.");
            objectId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Подписанное КП заблокировано для импорта.");
            if (Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Нельзя импортировать в архивный объект.");
        }
        // A source sheet may contain several estimates: provenance includes the Excel row span.
        var keys = blocks.Select(b => Fingerprint(sourceFile, b)).ToList();
        if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count)
            throw new InvalidOperationException("Одни и те же строки Excel выбраны дважды.");
        using (var already = new SqlCommand(
            @"SELECT details FROM dbo.change_log WITH (UPDLOCK,HOLDLOCK)
              WHERE contract_id=@contract AND entity=N'contract' AND action=N'excel_append'", conn, tx))
        {
            already.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = already.ExecuteReader())
                while (reader.Read()) signatures.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            if (keys.Any(signatures.Contains))
                throw new InvalidOperationException("Этот Excel-блок уже был добавлен в данное КП.");
        }

        using var temp = new SqlCommand(
            @"CREATE TABLE #append_excel(
                idx INT NOT NULL PRIMARY KEY, parent_idx INT NULL, system_id BIGINT NOT NULL,
                row_order INT NOT NULL, catalog_key NVARCHAR(600) NOT NULL,
                num NVARCHAR(32) NOT NULL,basis NVARCHAR(64) NOT NULL,name NVARCHAR(MAX) NOT NULL,
                unit NVARCHAR(32) NOT NULL,qty FLOAT NOT NULL,price FLOAT NOT NULL,
                purchase_price FLOAT NULL,work_price FLOAT NULL,note NVARCHAR(MAX) NOT NULL,
                extra_json NVARCHAR(MAX) NOT NULL,source_row INT NOT NULL);
              CREATE TABLE #append_ids(idx INT NOT NULL PRIMARY KEY,id BIGINT NOT NULL);", conn, tx);
        temp.ExecuteNonQuery();

        var table = NewTable();
        var nextId = 0;
        var addedRoots = 0;
        var order = 0;
        using (var orderQuery = new SqlCommand(
            "SELECT COALESCE(MAX([order]),-1)+1 FROM dbo.systems WITH (UPDLOCK,HOLDLOCK) WHERE contract_id=@id", conn, tx))
        {
            orderQuery.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            order = SqlInteger.RequireInt32(orderQuery.ExecuteScalar());
        }
        foreach (var b in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // New Excel sections always have new ids; existing sections are never altered.
            var mapped = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var section in b.Sections)
            {
                var parentId = mapped.TryGetValue(section.Parent, out var mappedParent)
                    ? (long?)mappedParent : null;
                using var addSection = new SqlCommand(
                    @"INSERT dbo.systems(contract_id,parent_id,name,[order],smr_amount,heading,source_num,stage_sys_src)
                      OUTPUT INSERTED.id VALUES(@contract,@parent,@name,@order,@smr,@heading,@source,NULL)", conn, tx);
                addSection.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
                addSection.Parameters.Add("@parent", SqlDbType.BigInt).Value = parentId is long parent ? parent : DBNull.Value;
                addSection.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = section.Name;
                addSection.Parameters.Add("@order", SqlDbType.Int).Value = order++;
                // Do not silently add financial SMR to an existing contract: sections
                // are appended, while contract-level totals/settings remain unchanged.
                addSection.Parameters.Add("@smr", SqlDbType.Float).Value = DBNull.Value;
                addSection.Parameters.Add("@heading", SqlDbType.NVarChar, 255).Value = section.Heading;
                addSection.Parameters.Add("@source", SqlDbType.NVarChar, 32).Value = section.SourceNumber;
                var sectionId = SqlInteger.RequireInt64(addSection.ExecuteScalar());
                if (section.Positions.Count == 0) mapped[section.Name] = sectionId;
                var positionOrder = 0;
                foreach (var p in section.Positions)
                {
                    var rootIndex = nextId;
                    AddRow(table, nextId++, null, sectionId, positionOrder++, p);
                    addedRoots++;
                    foreach (var child in p.Children)
                        AddRow(table, nextId++, rootIndex, sectionId, positionOrder++, child);
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        using (var copy = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx))
        {
            copy.DestinationTableName = "#append_excel";
            copy.BatchSize = 500;
            copy.BulkCopyTimeout = 180;
            foreach (DataColumn c in table.Columns) copy.ColumnMappings.Add(c.ColumnName, c.ColumnName);
            copy.WriteToServer(table);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using (var catalog = new SqlCommand(
            @"WITH source AS (SELECT catalog_key,MIN(name) name,MIN(unit) unit FROM #append_excel GROUP BY catalog_key)
              INSERT dbo.catalog_items([key],name,unit)
              SELECT s.catalog_key,s.name,s.unit FROM source s
              WHERE NOT EXISTS (SELECT 1 FROM dbo.catalog_items c WITH (UPDLOCK,HOLDLOCK)
                                WHERE c.[key]=s.catalog_key)", conn, tx))
            catalog.ExecuteNonQuery();
        using (var top = new SqlCommand(
            @"MERGE dbo.positions AS target
              USING (SELECT r.*,c.id catalog_id FROM #append_excel r
                     JOIN dbo.catalog_items c ON c.[key]=r.catalog_key WHERE r.parent_idx IS NULL) AS src
              ON 1=0
              WHEN NOT MATCHED THEN INSERT
                (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,purchase_price,
                 note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual,
                 work_price,cost_note,cost_color,work_sell,work_cost)
              VALUES (src.system_id,NULL,src.catalog_id,src.num,src.basis,src.name,src.unit,src.qty,
                      src.price,src.purchase_price,src.note,src.extra_json,src.source_row,src.row_order,
                      0,N'',NULL,NULL,src.work_price,N'',N'',NULL,NULL)
              OUTPUT src.idx,INSERTED.id INTO #append_ids(idx,id);", conn, tx))
        {
            top.CommandTimeout = 180;
            if (top.ExecuteNonQuery() != addedRoots)
                throw new DBConcurrencyException("Не все строки Excel вставлены.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        using (var child = new SqlCommand(
            @"INSERT dbo.positions
                (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,purchase_price,
                 note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual,
                 work_price,cost_note,cost_color,work_sell,work_cost)
              SELECT r.system_id,p.id,c.id,r.num,r.basis,r.name,r.unit,r.qty,r.price,r.purchase_price,
                     r.note,r.extra_json,r.source_row,r.row_order,0,N'',NULL,NULL,r.work_price,N'',N'',NULL,NULL
              FROM #append_excel r JOIN #append_ids p ON p.idx=r.parent_idx
              JOIN dbo.catalog_items c ON c.[key]=r.catalog_key WHERE r.parent_idx IS NOT NULL", conn, tx))
        {
            child.CommandTimeout = 180;
            if (child.ExecuteNonQuery() != nextId - addedRoots)
                throw new DBConcurrencyException("Не все комплектующие Excel вставлены.");
        }
        for (var i = 0; i < keys.Count; i++)
        {
            using var log = new SqlCommand(
                @"INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id)
                  VALUES (SYSDATETIME(),@user,N'contract',@contract,N'excel_append',@details,@contract,@obj)", conn, tx);
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value =
                Environment.UserName[..Math.Min(64, Environment.UserName.Length)];
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = keys[i];
            log.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, SqlServerDatabase.Revision(conn));
    }

    private static string Fingerprint(string path, ExcelImportBlock b) =>
        "ExcelAppend|" + Path.GetFileName(path).Replace("|", "_").ToLowerInvariant() +
        "|" + b.Sheet.Replace("|", "_").ToLowerInvariant() +
        "|" + b.FirstRow.ToString(CultureInfo.InvariantCulture) +
        "-" + b.LastRow.ToString(CultureInfo.InvariantCulture);

    private static DataTable NewTable()
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

    private static void AddRow(DataTable table, int idx, int? parent, long systemId, int order,
        ExcelImportPosition p)
    {
        var basis = p.Extra.TryGetValue("basis", out var s) ? s[..Math.Min(64, s.Length)] : "";
        table.Rows.Add(idx, parent is int parentIdx ? parentIdx : DBNull.Value, systemId, order,
            Catalog.Key(p.Name, p.Unit), p.Number, basis, p.Name, p.Unit, (double)p.Quantity,
            (double)p.Price, p.PurchasePrice is decimal purchase ? (double)purchase : DBNull.Value,
            p.WorkPrice is decimal work ? (double)work : DBNull.Value,
            p.Note, p.Extra.Count == 0 ? "" : JsonSerializer.Serialize(p.Extra), p.SourceRow);
    }
}
