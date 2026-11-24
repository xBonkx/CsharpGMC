using System.Data;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed record ExcelExistingSnapshot(
    long ContractId, long ObjectId, string Revision, bool Protected,
    IReadOnlyList<ExcelExistingDbRow> Rows, IReadOnlyList<string> UniqueSystems);

public sealed class ExcelExistingCommitUncertainException : Exception
{
    public string OperationId { get; }
    public ExcelExistingCommitUncertainException(string operationId, Exception inner)
        : base("Исход COMMIT неизвестен. Не повторяй импорт; проверь operationId=" + operationId, inner)
    {
        OperationId = operationId;
    }
}

/// <summary>
/// Pilot-only, non-destructive Excel reconciliation. No existing price, note, formula,
/// act, supplementary agreement, stage or shipment is ever written by this service.
/// </summary>
public static partial class SqlServerExcelExistingUpdate
{
    public static string FileFingerprint(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // Verify the SQL Server-selected database, not merely the caller-supplied
    // connection string. SQL Server aliases may differ from @@SERVERNAME, so
    // enforce the actual database and report the server for audit.
    private static void VerifyConnectedPilotTarget(SqlConnection conn, string connectionString)
    {
        var expected = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        using var cmd = new SqlCommand(
            "SELECT DB_NAME(), CONVERT(nvarchar(256),SERVERPROPERTY('ServerName'))", conn);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
            throw new InvalidOperationException("SQL Server не подтвердил выбранную базу данных.");
        var actual = reader.GetString(0);
        var server = reader.IsDBNull(1) ? "неизвестный сервер" : reader.GetString(1);
        if (!actual.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Небезопасная SQL-цель: сервер=" + server + "; база=" + actual +
                "; ожидалась " + expected + ". Запись запрещена.");
    }

    public static ExcelExistingSnapshot Read(string connectionString, long contractId)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (contractId <= 0) throw new ArgumentOutOfRangeException(nameof(contractId));
        using var conn = SqlServerDatabase.Open(connectionString);
        VerifyConnectedPilotTarget(conn, connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        string revision;
        using (var cmd = new SqlCommand(
            "SELECT [value] FROM dbo.meta WITH (HOLDLOCK) WHERE [key]=N'rev'", conn, tx))
            revision = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ??
                throw new InvalidOperationException("Не найдена meta.rev.");

        long objectId;
        bool protection;
        using (var cmd = new SqlCommand(
            @"SELECT c.object_id,c.locked,o.archived,
               CASE WHEN c.base_contract_id IS NOT NULL OR c.stage_of IS NOT NULL
                   OR c.stage_rest=1 OR EXISTS(SELECT 1 FROM dbo.contracts d
                   WHERE d.base_contract_id=c.id OR d.stage_of=c.id)
                   THEN 1 ELSE 0 END
              FROM dbo.contracts c WITH (HOLDLOCK)
              JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Договор не найден.");
            objectId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            protection = Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture) ||
                Convert.ToBoolean(r.GetValue(2), CultureInfo.InvariantCulture) ||
                Convert.ToBoolean(r.GetValue(3), CultureInfo.InvariantCulture);
        }

        var systems = new List<string>();
        using (var cmd = new SqlCommand(
            @"SELECT s.name,ISNULL(parent.name,N'') FROM dbo.systems s
              LEFT JOIN dbo.systems parent ON parent.id=s.parent_id
              WHERE s.contract_id=@contract", conn, tx))
        {
            cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(0);
                var parent = r.GetString(1);
                systems.Add(parent.Length == 0 ? name : parent + " / " + name);
            }
        }
        // A duplicate section name is not a safe insertion target.
        var unique = systems.GroupBy(x => ExcelExistingUpdatePlan.Normalize(x))
            .Where(g => g.Count() == 1).Select(g => g.First()).ToArray();

        var rows = new List<ExcelExistingDbRow>();
        using (var cmd = new SqlCommand(
            @"SELECT p.id,p.system_id,p.parent_id,ISNULL(parent_system.name,N''),
                s.name,ISNULL(parent_position.name,N''),ISNULL(p.num,N''),
                p.name,p.unit,p.qty,p.price,p.purchase_price,p.work_price,
                ISNULL(p.note,N''),ISNULL(p.formulas,N''),p.source_row,ISNULL(p.extra_json,N''),
                CASE WHEN p.parent_id IS NOT NULL OR p.out_of_kp=1 OR p.area_manual IS NOT NULL
                    OR EXISTS(SELECT 1 FROM dbo.positions child WHERE child.parent_id=p.id)
                    OR EXISTS(SELECT 1 FROM dbo.act_lines l WHERE l.position_id=p.id)
                    OR EXISTS(SELECT 1 FROM dbo.shipment_lines l WHERE l.position_id=p.id)
                    OR EXISTS(SELECT 1 FROM dbo.procurement q WHERE q.position_id=p.id)
                    OR EXISTS(SELECT 1 FROM dbo.movements m WHERE m.position_id=p.id)
                    OR EXISTS(SELECT 1 FROM dbo.positions stage WHERE stage.stage_src=p.id)
                    OR p.stage_src IS NOT NULL THEN 1 ELSE 0 END,
                CASE WHEN EXISTS(
                    SELECT 1 FROM dbo.act_lines l JOIN dbo.acts a ON a.id=l.act_id
                    WHERE l.position_id=p.id AND a.signed=1) THEN 1 ELSE 0 END
              FROM dbo.positions p
              JOIN dbo.systems s ON s.id=p.system_id
              LEFT JOIN dbo.systems parent_system ON parent_system.id=s.parent_id
              LEFT JOIN dbo.positions parent_position ON parent_position.id=p.parent_id
              WHERE s.contract_id=@contract
              ORDER BY s.[order],p.[order],p.id", conn, tx))
        {
            cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            cmd.CommandTimeout = 180;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var src = r.IsDBNull(15) ? (int?)null : Convert.ToInt32(r.GetValue(15), CultureInfo.InvariantCulture);
                var extra = r.GetString(16);
                var imported = src > 0;
                if (!imported && extra.Length > 0)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(extra);
                        imported = json.RootElement.ValueKind == JsonValueKind.Object &&
                            json.RootElement.TryGetProperty("src", out var value) &&
                            value.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(value.GetString());
                    }
                    catch (JsonException) { /* Malformed provenance is never trusted. */ }
                }
                var systemName = r.GetString(4);
                var parentName = r.GetString(3);
                var path = parentName.Length == 0 ? systemName : parentName + " / " + systemName;
                rows.Add(new ExcelExistingDbRow(
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                    r.IsDBNull(2) ? null : Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
                    path, r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                    Convert.ToDecimal(r.GetValue(9), CultureInfo.InvariantCulture),
                    Convert.ToDecimal(r.GetValue(10), CultureInfo.InvariantCulture),
                    r.IsDBNull(11) ? null : Convert.ToDecimal(r.GetValue(11), CultureInfo.InvariantCulture),
                    r.IsDBNull(12) ? null : Convert.ToDecimal(r.GetValue(12), CultureInfo.InvariantCulture),
                    r.GetString(13), r.GetString(14), src, extra,
                    Convert.ToBoolean(r.GetValue(17), CultureInfo.InvariantCulture),
                    Convert.ToBoolean(r.GetValue(18), CultureInfo.InvariantCulture), imported));
            }
        }
        tx.Commit();
        return new ExcelExistingSnapshot(contractId, objectId, revision, protection, rows, unique);
    }

    public static ExcelExistingUpdatePlan Prepare(string connectionString, long contractId,
        string filePath, IReadOnlyList<ExcelImportBlock> blocks)
    {
        var snapshot = Read(connectionString, contractId);
        return ExcelExistingUpdatePlan.Build(contractId, snapshot.Revision,
            FileFingerprint(filePath), snapshot.Rows,
            ExcelExistingUpdatePlan.Flatten(blocks), snapshot.UniqueSystems,
            snapshot.Protected, string.Join(";", blocks.Select(x =>
                x.Sheet + ":" + x.FirstRow + "-" + x.LastRow)));
    }

    public static bool WasCommitted(string connectionString, long contractId, string operationId)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (contractId <= 0 || operationId.Length != 64 || !operationId.All(Uri.IsHexDigit))
            throw new ArgumentException("Некорректный идентификатор операции.");
        using var conn = SqlServerDatabase.Open(connectionString);
        VerifyConnectedPilotTarget(conn, connectionString);
        using var command = new SqlCommand(
            @"SELECT details FROM dbo.change_log
              WHERE contract_id=@contract AND entity=N'contract'
                AND action=N'excel_safe_update'", conn);
        command.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0) &&
                reader.GetString(0).Contains("|" + operationId + "|", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static SqlServerDatabase.SaveResult Apply(string connectionString,
        string filePath, ExcelExistingUpdatePlan plan,
        CancellationToken cancellationToken = default)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        ArgumentNullException.ThrowIfNull(plan);
        var chosen = plan.Selected();
        if (FileFingerprint(filePath) != plan.FileSha256)
            throw new InvalidOperationException("Файл Excel изменён после предпросмотра. Построй план заново.");
        cancellationToken.ThrowIfCancellationRequested();
        var operationId = plan.OperationId();
        var prefix = "ExcelSafe|" + plan.FileSha256 + "|" + plan.ScopeKey + "|";
        var detail = prefix + operationId + "|" + plan.PlanId;
        using var conn = SqlServerDatabase.Open(connectionString);
        VerifyConnectedPilotTarget(conn, connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        // Resolve replay, undo and the current revision under ONE lock. A
        // read-only check on separate connections races a concurrent Undo.
        var lockedRevision = SqlServerDatabase.LockRevision(conn, tx);
        if (string.IsNullOrWhiteSpace(lockedRevision))
            throw new DBConcurrencyException("Отсутствует meta.rev.");

        // Same-file imports with a different plan are never replayed silently.
        bool exactReplay = false;
        bool differentPlan = false;
        using (var seen = new SqlCommand(
            @"SELECT details FROM dbo.change_log WITH (UPDLOCK,HOLDLOCK)
              WHERE contract_id=@contract AND entity=N'contract'
                AND action=N'excel_safe_update'", conn, tx))
        {
            seen.Parameters.Add("@contract", SqlDbType.BigInt).Value = plan.ContractId;
            using var reader = seen.ExecuteReader();
            while (reader.Read())
            {
                var prev = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (prev == detail)
                {
                    if (exactReplay)
                        throw new InvalidOperationException("Найден дублирующий журнал Excel. Требуется сверка.");
                    exactReplay = true;
                }
                else if (prev.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    differentPlan = true;
            }
        }
        if (differentPlan)
            throw new InvalidOperationException(
                "Этот файл Excel уже был применён с другим выбором. Повторное применение запрещено.");
        if (exactReplay)
        {
            if (UndoJournalExists(conn, tx, plan.ContractId, operationId))
                throw new InvalidOperationException(
                    "Этот импорт уже был отменён. Старый план нельзя повторять; построй новый предпросмотр.");
            var evidence = LoadUndoSnapshot(conn, tx, plan.ContractId, operationId);
            // No changes on this path. Never pass off a later, foreign revision
            // as the revision of the old Excel transaction.
            return new SqlServerDatabase.SaveResult(true, null, evidence.AfterRevision);
        }
        if (!string.Equals(lockedRevision, plan.Revision, StringComparison.Ordinal))
            throw new DBConcurrencyException("Смета изменилась после предпросмотра. Построй план заново.");

        long objectId;
        using (var check = new SqlCommand(
            @"SELECT c.object_id,c.locked,o.archived,
                CASE WHEN c.base_contract_id IS NOT NULL OR c.stage_of IS NOT NULL
                    OR c.stage_rest=1 OR EXISTS(SELECT 1 FROM dbo.contracts d WITH (HOLDLOCK)
                    WHERE d.base_contract_id=c.id OR d.stage_of=c.id)
                    THEN 1 ELSE 0 END
              FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK)
              JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id
              WHERE c.id=@id", conn, tx))
        {
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = plan.ContractId;
            using var r = check.ExecuteReader();
            if (!r.Read()) throw new DBConcurrencyException("Договор удалён.");
            objectId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            if (Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture) ||
                Convert.ToBoolean(r.GetValue(2), CultureInfo.InvariantCulture) ||
                Convert.ToBoolean(r.GetValue(3), CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Договор заблокирован, архивирован либо связан с ДС/этапами.");
        }

        var edits = chosen.Where(x => x.Action == ExcelExistingAction.ApplyQuantity).ToArray();
        var adds = chosen.Where(x => x.Action == ExcelExistingAction.AddPosition).ToArray();
        using (var create = new SqlCommand(
            @"CREATE TABLE #excel_changes(
                id BIGINT NOT NULL PRIMARY KEY, sy BIGINT NOT NULL, parent BIGINT NULL,
                old_qty FLOAT NOT NULL, new_qty FLOAT NOT NULL,
                old_name NVARCHAR(2000) NOT NULL, old_unit NVARCHAR(32) NOT NULL,
                old_price FLOAT NOT NULL,
                old_purchase_price FLOAT NULL, old_work_price FLOAT NULL,
                old_note NVARCHAR(MAX) NOT NULL,
                old_formula NVARCHAR(MAX) NOT NULL, old_extra NVARCHAR(MAX) NOT NULL,
                old_source_row INT NULL);
              CREATE TABLE #excel_new(
                idx INT NOT NULL PRIMARY KEY, sy BIGINT NOT NULL, [key] NVARCHAR(600) NOT NULL,
                num NVARCHAR(32) NOT NULL, basis NVARCHAR(64) NOT NULL,
                name NVARCHAR(2000) NOT NULL,
                unit NVARCHAR(32) NOT NULL, qty FLOAT NOT NULL, price FLOAT NOT NULL,
                purchase_price FLOAT NULL, work_price FLOAT NULL, note NVARCHAR(MAX) NOT NULL,
                source_row INT NOT NULL, extra_json NVARCHAR(MAX) NOT NULL);
              CREATE TABLE #excel_new_ids(idx INT NOT NULL PRIMARY KEY,id BIGINT NOT NULL);", conn, tx))
            create.ExecuteNonQuery();

        if (edits.Length > 0)
        {
            var table = new DataTable();
            foreach (var (name, type) in new (string, Type)[] {
                ("id",typeof(long)), ("sy",typeof(long)), ("parent",typeof(long)),
                ("old_qty",typeof(double)), ("new_qty",typeof(double)),
                ("old_name",typeof(string)), ("old_unit",typeof(string)),
                ("old_price",typeof(double)),
                ("old_purchase_price",typeof(double)), ("old_work_price",typeof(double)),
                ("old_note",typeof(string)),
                ("old_formula",typeof(string)), ("old_extra",typeof(string)),
                ("old_source_row",typeof(int)) }) table.Columns.Add(name, type);
            foreach (var d in edits)
            {
                var old = d.Existing!;
                table.Rows.Add(old.Id, old.SystemId, old.ParentId is long parent ? parent : DBNull.Value,
                    (double)old.Quantity, (double)d.Source!.Quantity,
                    old.Name, old.Unit, (double)old.Price,
                    old.PurchasePrice is decimal purchase ? (double)purchase : DBNull.Value,
                    old.WorkPrice is decimal work ? (double)work : DBNull.Value,
                    old.Note, old.Formulas, old.ExtraJson, old.SourceRow is int sr ? sr : DBNull.Value);
            }
            BulkCopy(conn, tx, "#excel_changes", table);
            cancellationToken.ThrowIfCancellationRequested();
            using (var validate = new SqlCommand(
                @"SELECT COUNT_BIG(*) FROM #excel_changes e
                  LEFT JOIN dbo.positions p WITH (UPDLOCK,HOLDLOCK) ON p.id=e.id
                  LEFT JOIN dbo.systems s WITH (HOLDLOCK) ON s.id=p.system_id
                  WHERE p.id IS NULL OR s.contract_id<>@contract OR p.system_id<>e.sy
                    OR ISNULL(p.parent_id,-1)<>ISNULL(e.parent,-1)
                    OR p.qty<>e.old_qty OR p.price<>e.old_price
                    OR EXISTS(SELECT p.purchase_price,p.work_price
                              EXCEPT SELECT e.old_purchase_price,e.old_work_price)
                    OR p.name<>e.old_name OR p.unit<>e.old_unit
                    OR ISNULL(p.note,N'')<>e.old_note
                    OR ISNULL(p.formulas,N'')<>e.old_formula
                    OR ISNULL(p.extra_json,N'')<>e.old_extra
                    OR ISNULL(p.source_row,-1)<>ISNULL(e.old_source_row,-1)
                    OR (ISNULL(p.source_row,0)<=0 AND
                        ISNULL(p.extra_json,N'') NOT LIKE N'%""src""%')
                    OR (ISNULL(p.formulas,N'') NOT IN (N'',N'{}'))
                    OR EXISTS(SELECT 1 FROM dbo.positions child WITH (HOLDLOCK) WHERE child.parent_id=e.id)
                    OR EXISTS(SELECT 1 FROM dbo.act_lines a WITH (HOLDLOCK) WHERE a.position_id=e.id)
                    OR EXISTS(SELECT 1 FROM dbo.shipment_lines sh WITH (HOLDLOCK) WHERE sh.position_id=e.id)
                    OR EXISTS(SELECT 1 FROM dbo.procurement q WITH (HOLDLOCK) WHERE q.position_id=e.id)
                    OR EXISTS(SELECT 1 FROM dbo.movements m WITH (HOLDLOCK) WHERE m.position_id=e.id)
                    OR EXISTS(SELECT 1 FROM dbo.positions st WITH (HOLDLOCK) WHERE st.stage_src=e.id)
                    OR p.stage_src IS NOT NULL OR p.parent_id IS NOT NULL
                    OR p.out_of_kp=1 OR p.area_manual IS NOT NULL", conn, tx))
            {
                validate.Parameters.Add("@contract", SqlDbType.BigInt).Value = plan.ContractId;
                validate.CommandTimeout = 180;
                if (Convert.ToInt64(validate.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    throw new DBConcurrencyException("Позиция, формула или связь изменились. Импорт отменён целиком.");
            }
            using (var update = new SqlCommand(
                @"UPDATE p SET qty=e.new_qty
                  FROM dbo.positions p JOIN #excel_changes e ON e.id=p.id", conn, tx))
            {
                update.CommandTimeout = 180;
                if (update.ExecuteNonQuery() != edits.Length)
                    throw new DBConcurrencyException("Не все объёмы изменены.");
            }
            using (var log = new SqlCommand(
                @"INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id)
                  SELECT SYSDATETIME(),@user,N'position',e.id,N'excel_qty',
                   CONCAT(N'Excel: ',CONVERT(NVARCHAR(40),e.old_qty),N' -> ',
                     CONVERT(NVARCHAR(40),e.new_qty),N'; plan=',@plan),
                   @contract,@obj FROM #excel_changes e", conn, tx))
            {
                CommonParams(log, plan.ContractId, objectId, plan.PlanId);
                log.ExecuteNonQuery();
            }
        }

        if (adds.Length > 0)
        {
            // One unique existing section per incoming system; never create or rename systems.
            var sectionMap = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var section = new SqlCommand(
                @"SELECT s.id,s.name,ISNULL(parent.name,N'')
                  FROM dbo.systems s WITH (UPDLOCK,HOLDLOCK)
                  LEFT JOIN dbo.systems parent ON parent.id=s.parent_id
                  WHERE s.contract_id=@contract", conn, tx))
            {
                section.Parameters.Add("@contract", SqlDbType.BigInt).Value = plan.ContractId;
                using var r = section.ExecuteReader();
                while (r.Read())
                {
                    var path = r.GetString(2).Length == 0 ? r.GetString(1) :
                        r.GetString(2) + " / " + r.GetString(1);
                    var key = ExcelExistingUpdatePlan.Normalize(path);
                    if (!sectionMap.TryAdd(key, r.GetInt64(0))) sectionMap[key] = -1;
                }
            }
            var table = new DataTable();
            foreach (var (name, type) in new (string, Type)[] {
                ("idx",typeof(int)),("sy",typeof(long)),("key",typeof(string)),
                ("num",typeof(string)),("basis",typeof(string)),
                ("name",typeof(string)),("unit",typeof(string)),
                ("qty",typeof(double)),("price",typeof(double)),
                ("purchase_price",typeof(double)),("work_price",typeof(double)),
                ("note",typeof(string)),("source_row",typeof(int)),("extra_json",typeof(string))
                }) table.Columns.Add(name, type);
            foreach (var d in adds)
            {
                var source = d.Source!;
                if (source.ParentOrdinal is not null ||
                    !sectionMap.TryGetValue(ExcelExistingUpdatePlan.Normalize(source.SectionPath), out var sy) ||
                    sy <= 0)
                    throw new DBConcurrencyException("Раздел неоднозначен или отсутствует.");
                var key = Catalog.Key(source.Name, source.Unit);
                if (key.Length > 600) throw new ArgumentException("Слишком длинный ключ каталога.");
                var extra = string.IsNullOrWhiteSpace(source.ExtraJson)
                    ? new Dictionary<string, string>()
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(source.ExtraJson)
                        ?? throw new InvalidOperationException("Некорректные данные источника Excel.");
                extra.TryAdd("src", source.Name);
                extra["excel_source_sha256"] = plan.FileSha256;
                extra["excel_imported_qty"] = source.Quantity.ToString(CultureInfo.InvariantCulture);
                var extraText = JsonSerializer.Serialize(extra);
                table.Rows.Add(source.Ordinal, sy, key, source.Number, source.Basis, source.Name,
                    source.Unit, (double)source.Quantity, (double)source.Price,
                    source.PurchasePrice is decimal purchase ? (double)purchase : DBNull.Value,
                    source.WorkPrice is decimal work ? (double)work : DBNull.Value,
                    source.Note, source.SourceRow, extraText);
            }
            BulkCopy(conn, tx, "#excel_new", table);
            cancellationToken.ThrowIfCancellationRequested();
            using (var duplicates = new SqlCommand(
                @"SELECT COUNT_BIG(*) FROM #excel_new n
                  WHERE EXISTS(
                    SELECT 1 FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK)
                    WHERE p.system_id=n.sy AND p.parent_id IS NULL
                        AND p.name=n.name AND p.unit=n.unit)", conn, tx))
            {
                if (Convert.ToInt64(duplicates.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    throw new DBConcurrencyException("Новая позиция уже существует. Повтори сравнение.");
            }
            using (var catalog = new SqlCommand(
                @"WITH src AS (SELECT [key],MIN(name) name,MIN(unit) unit
                  FROM #excel_new GROUP BY [key])
                  INSERT dbo.catalog_items([key],name,unit)
                  SELECT src.[key],src.name,src.unit FROM src
                  WHERE NOT EXISTS(SELECT 1 FROM dbo.catalog_items c WITH (UPDLOCK,HOLDLOCK)
                      WHERE c.[key]=src.[key])", conn, tx))
                catalog.ExecuteNonQuery();
            using (var insert = new SqlCommand(
                @"MERGE dbo.positions AS target
                  USING (
                    SELECT n.*,c.id AS catalog_id,
                      COALESCE((SELECT MAX(p.[order])+1
                         FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK)
                         WHERE p.system_id=n.sy),0) +
                      ROW_NUMBER() OVER(PARTITION BY n.sy ORDER BY n.idx)-1 AS new_order
                    FROM #excel_new n JOIN dbo.catalog_items c ON c.[key]=n.[key]
                  ) AS source ON 1=0
                  WHEN NOT MATCHED THEN INSERT
                    (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,purchase_price,
                     note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual,
                     work_price,cost_note,cost_color,work_sell,work_cost)
                  VALUES (source.sy,NULL,source.catalog_id,source.num,source.basis,source.name,source.unit,
                    source.qty,source.price,source.purchase_price,source.note,source.extra_json,
                    source.source_row,source.new_order,0,N'',NULL,NULL,source.work_price,
                    N'',N'',NULL,NULL)
                  OUTPUT source.idx,INSERTED.id INTO #excel_new_ids(idx,id);", conn, tx))
            {
                insert.CommandTimeout = 180;
                if (insert.ExecuteNonQuery() != adds.Length)
                    throw new DBConcurrencyException("Не все новые позиции записаны.");
            }
            // Record each inserted position ID for reconciliation and audit.
            using (var rowLog = new SqlCommand(
                @"INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id)
                  SELECT SYSDATETIME(),@user,N'position',ids.id,N'excel_add',
                    CONCAT(N'Excel row ',n.source_row,N': ',n.name,N'; plan=',@plan),
                    @contract,@obj
                  FROM #excel_new_ids ids JOIN #excel_new n ON n.idx=ids.idx", conn, tx))
            {
                CommonParams(rowLog, plan.ContractId, objectId, plan.PlanId);
                if (rowLog.ExecuteNonQuery() != adds.Length)
                    throw new DBConcurrencyException("Не все добавления отражены в журнале.");
            }
            using (var log = new SqlCommand(
                @"INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id)
                  VALUES(SYSDATETIME(),@user,N'contract',@contract,N'excel_added',
                    CONCAT(N'Added: ',@count,N'; plan=',@plan),@contract,@obj)", conn, tx))
            {
                CommonParams(log, plan.ContractId, objectId, plan.PlanId);
                log.Parameters.Add("@count", SqlDbType.Int).Value = adds.Length;
                log.ExecuteNonQuery();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using (var journal = new SqlCommand(
            @"INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id)
              VALUES(SYSDATETIME(),@user,N'contract',@contract,
                N'excel_safe_update',@details,@contract,@obj)", conn, tx))
        {
            CommonParams(journal, plan.ContractId, objectId, plan.PlanId);
            journal.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = detail;
            journal.ExecuteNonQuery();
        }
        SqlServerWorkspace.BumpRevision(conn, tx);

        // Capture the reversible quantity delta and every newly generated position ID
        // in the SAME transaction. An add-containing batch is deliberately not
        // undoable automatically: deletion needs separate dependency validation.
        var quantitySnapshot = edits.Select(d =>
        {
            var old = d.Existing!;
            return new ExcelExistingQuantityUndoRow(old.Id, old.SystemId, old.ParentId,
                old.Name, old.Unit, old.Quantity, d.Source!.Quantity, old.Price,
                old.PurchasePrice, old.WorkPrice, old.Note, old.Formulas,
                old.SourceRow, old.ExtraJson);
        }).ToArray();
        var addedIds = new List<long>();
        using (var ids = new SqlCommand("SELECT id FROM #excel_new_ids ORDER BY idx", conn, tx))
        using (var reader = ids.ExecuteReader())
            while (reader.Read()) addedIds.Add(reader.GetInt64(0));
        if (addedIds.Count != adds.Length)
            throw new DBConcurrencyException("Не все ID новых Excel-позиций подтверждены.");
        string appliedRevision;
        using (var rev = new SqlCommand(
            "SELECT [value] FROM dbo.meta WITH (HOLDLOCK) WHERE [key]=N'rev'", conn, tx))
            appliedRevision = Convert.ToString(rev.ExecuteScalar(), CultureInfo.InvariantCulture) ??
                throw new DBConcurrencyException("Нет ревизии после Excel обновления.");
        var snapshot = new ExcelExistingUndoSnapshot(1, plan.ContractId, operationId,
            plan.PlanId, plan.Revision, appliedRevision, quantitySnapshot, addedIds);
        snapshot.Validate(operationId, plan.ContractId);
        using (var evidence = new SqlCommand(
            @"INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id)
              VALUES(SYSDATETIME(),@user,N'contract',@contract,
                N'excel_snapshot_v1',@details,@contract,@obj)", conn, tx))
        {
            CommonParams(evidence, plan.ContractId, objectId, plan.PlanId);
            evidence.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                JsonSerializer.Serialize(snapshot);
            if (evidence.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Снимок отката Excel не записан.");
        }
        // Do not observe cancellation after this point: an exception from Commit may
        // mean the server committed while the client lost its acknowledgement.
        try { tx.Commit(); }
        catch (Exception ex)
        {
            throw new ExcelExistingCommitUncertainException(operationId, ex);
        }
        // Use the revision observed in the committed transaction. A second
        // network read after COMMIT could fail and falsely look like a failed write.
        return new SqlServerDatabase.SaveResult(true, null, appliedRevision);
    }

    private static void CommonParams(SqlCommand cmd, long contractId, long objectId, string planId)
    {
        var user = Environment.UserName;
        cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value =
            user[..Math.Min(user.Length, 64)];
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
        cmd.Parameters.Add("@plan", SqlDbType.NVarChar, 64).Value = planId;
    }

    private static void BulkCopy(SqlConnection conn, SqlTransaction tx, string destination, DataTable source)
    {
        using var copy = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = destination, BatchSize = 500, BulkCopyTimeout = 180
        };
        foreach (DataColumn col in source.Columns)
            copy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
        copy.WriteToServer(source);
    }
}
