using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Комплектующая скопированной строки: цена всегда 0, как в Python _row_data.</summary>
public sealed record CopiedChild(string Name, string Unit, decimal Qty, string Note);

/// <summary>Строка внутреннего буфера сметы (kp_clipboard._row_data): формулы и закупка сохраняются.</summary>
public sealed record CopiedRow(string Name, string Unit, decimal Qty, decimal Price,
    decimal? PurchasePrice, string Note, string Formulas, IReadOnlyList<CopiedChild>? Children)
{
    // Internal copy preserves the position's SMR selling price. External TSV
    // callers leave this null; no price is invented from the catalog.
    public decimal? WorkPrice { get; init; }
}

/// <summary>Atomic batch insertion into SQL Server; never performs per-cell network writes.</summary>
public static partial class SqlServerDatabase
{
    /// <summary>
    /// Вставить скопированные строки (внутренний буфер или Ctrl+D) под <paramref name="afterPositionId"/>
    /// (после его комплектующих) одним пакетом — перенос add_positions_bulk: нумерация один раз,
    /// записи журнала нет (у вставки из буфера нет своей записи — как в Python), отмена через undo.
    /// </summary>
    public static SaveResult InsertCopiedRows(string connectionString, long contractId,
        long sectionId, long? afterPositionId, IReadOnlyList<CopiedRow> rows, string expectedRevision)
    {
        if (contractId <= 0 || sectionId <= 0 || rows is null || rows.Count is 0 or > PastedPositions.MaxRows)
            return SaveResult.Fail("Выбери раздел и от 1 до 5000 строк.", expectedRevision);
        var total = 0;
        foreach (var r in rows)
        {
            total += 1 + (r.Children?.Count ?? 0);
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 2000 || r.Unit.Length > 32 ||
                r.Note.Length > 10_000 || r.Formulas.Length > 10_000 ||
                r.Qty < 0 || r.Price < 0 || r.PurchasePrice < 0 || r.WorkPrice < 0 ||
                r.Children is { } kids && kids.Any(c => string.IsNullOrWhiteSpace(c.Name) ||
                    c.Name.Length > 2000 || c.Unit.Length > 32 || c.Note.Length > 10_000 || c.Qty < 0) ||
                Catalog.Key(r.Name, r.Unit).Length > 600)
                return SaveResult.Fail("Данные вставки не прошли проверку. Проверь названия, единицы и цены.", expectedRevision);
        }
        if (total > PastedPositions.MaxRows)
            return SaveResult.Fail("Слишком много строк для вставки (лимит 5000).", expectedRevision);

        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = LockRevision(conn, tx);
        if (rev is null) return SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Данные изменены другим сотрудником. Сначала обнови смету.", rev);
        var contract = LoadPositionContract(conn, tx, contractId);
        if (contract is null) return SaveResult.Fail("Документ не найден.", rev);
        if (contract.Value.Locked) return SaveResult.Fail("Подписанная смета недоступна для вставки.", rev);
        var ordered = TreeOrder(LoadOrderedPositions(conn, tx, contractId, sectionId));
        var insertAt = ordered.Count;
        if (afterPositionId is long after)
        {
            var idx = ordered.FindIndex(p => p.Id == after);
            if (idx < 0) return SaveResult.Fail("Выбранная позиция вставки отсутствует в разделе.", rev);
            var rootId = ordered[idx].ParentId ?? after;
            insertAt = ordered.FindLastIndex(p => p.Id == rootId || p.ParentId == rootId) + 1;
        }

        WritePositionOrder(conn, tx, sectionId, ordered);
        InsertRowsAt(conn, tx, sectionId, insertAt, rows);
        var updated = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, updated);
    }

    /// <summary>
    /// Механика пакетной вставки (общая для буфера и замены): сдвиг хвоста под количество
    /// строк с комплектующими, bulk-copy во временную таблицу, каталог пакета одним
    /// запросом, верхние строки с OUTPUT id → комплектующие ссылаются на свои строки.
    /// Вызывается после WritePositionOrder — нумерация последовательная от insertAt.
    /// </summary>
    private static void InsertRowsAt(SqlConnection conn, SqlTransaction tx,
        long sectionId, int insertAt, IReadOnlyList<CopiedRow> rows)
    {
        var total = rows.Sum(r => 1 + (r.Children?.Count ?? 0));
        using (var shift = new SqlCommand(
            "UPDATE dbo.positions SET [order]=[order]+@count " +
            "WHERE system_id=@section AND [order]>=@at", conn, tx))
        {
            shift.Parameters.Add("@count", SqlDbType.Int).Value = total;
            shift.Parameters.Add("@section", SqlDbType.BigInt).Value = sectionId;
            shift.Parameters.Add("@at", SqlDbType.Int).Value = insertAt;
            shift.ExecuteNonQuery();
        }
        using (var create = new SqlCommand(
            "CREATE TABLE #copy_rows (idx INT PRIMARY KEY, parent_idx INT NULL, [key] NVARCHAR(600) NOT NULL, " +
            "name NVARCHAR(MAX) NOT NULL, unit NVARCHAR(32) NOT NULL, qty FLOAT NOT NULL, price FLOAT NOT NULL, " +
            "purchase_price FLOAT NULL, note NVARCHAR(MAX) NOT NULL, formulas NVARCHAR(MAX) NOT NULL, work_price FLOAT NULL); " +
            "CREATE TABLE #made (idx INT PRIMARY KEY, id BIGINT NOT NULL)", conn, tx))
            create.ExecuteNonQuery();

        var table = new DataTable();
        table.Columns.Add("idx", typeof(int));
        table.Columns.Add("parent_idx", typeof(int));
        table.Columns.Add("key", typeof(string));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("unit", typeof(string));
        table.Columns.Add("qty", typeof(double));
        table.Columns.Add("price", typeof(double));
        table.Columns.Add("purchase_price", typeof(double));
        table.Columns.Add("note", typeof(string));
        table.Columns.Add("formulas", typeof(string));
        table.Columns.Add("work_price", typeof(double));
        var idxSeq = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var topIdx = idxSeq++;
            table.Rows.Add(topIdx, DBNull.Value, Catalog.Key(r.Name, r.Unit), r.Name, r.Unit, (double)r.Qty,
                (double)r.Price, r.PurchasePrice is null ? DBNull.Value : (object)(double)r.PurchasePrice.Value,
                r.Note, r.Formulas,
                r.WorkPrice is null ? DBNull.Value : (object)(double)r.WorkPrice.Value);
            foreach (var child in r.Children ?? Array.Empty<CopiedChild>())
            {
                table.Rows.Add(idxSeq++, topIdx, Catalog.Key(child.Name, child.Unit), child.Name,
                    child.Unit, (double)child.Qty, 0d, DBNull.Value, child.Note, "", DBNull.Value);
            }
        }
        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx))
        {
            bulk.DestinationTableName = "#copy_rows";
            bulk.BatchSize = 250;
            bulk.BulkCopyTimeout = 60;
            foreach (DataColumn column in table.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            bulk.WriteToServer(table);
        }
        // Каталог пакета: недостающие записи одним запросом (как add_positions_bulk).
        using (var catalog = new SqlCommand(
            "WITH source AS (SELECT [key], MIN(name) AS name, MIN(unit) AS unit " +
            "FROM #copy_rows GROUP BY [key]) " +
            "INSERT INTO dbo.catalog_items ([key],name,unit) " +
            "SELECT src.[key],src.name,src.unit FROM source src " +
            "WHERE NOT EXISTS (SELECT 1 FROM dbo.catalog_items ci WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE ci.[key]=src.[key])", conn, tx))
            catalog.ExecuteNonQuery();

        // Верхние строки: parent_id NULL, formulas из копии; id фиксируем для комплектующих.
        using (var insert = new SqlCommand(
            "MERGE dbo.positions AS target " +
            "USING (SELECT p.*,ci.id AS catalog_id FROM #copy_rows p " +
            "JOIN dbo.catalog_items ci ON ci.[key]=p.[key] WHERE p.parent_idx IS NULL) AS source ON 1=0 " +
            "WHEN NOT MATCHED THEN INSERT (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price," +
            "purchase_price,note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual," +
            "work_price,cost_note,cost_color,work_sell,work_cost) " +
            "VALUES (@section,NULL,source.catalog_id,N'',N'',source.name,source.unit,source.qty,source.price,source.purchase_price," +
            "source.note,N'',NULL,@at+source.idx,0,source.formulas,NULL,NULL,source.work_price,N'',N'',NULL,NULL) " +
            "OUTPUT source.idx, INSERTED.id INTO #made (idx,id);", conn, tx))
        {
            insert.Parameters.Add("@section", SqlDbType.BigInt).Value = sectionId;
            insert.Parameters.Add("@at", SqlDbType.Int).Value = insertAt;
            if (insert.ExecuteNonQuery() != rows.Count)
                throw new DBConcurrencyException("Вставка строк неполная. Транзакция отменена.");
        }
        // Комплектующие: цена 0, родитель — своя строка из #made.
        using (var insertKids = new SqlCommand(
            "INSERT INTO dbo.positions (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price," +
            "purchase_price,note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual," +
            "work_price,cost_note,cost_color,work_sell,work_cost) " +
            "SELECT @section,m.id,ci.id,N'',N'',p.name,p.unit,p.qty,0,NULL,p.note,N'',NULL,@at+p.idx,0,N'',NULL,NULL,NULL,N'',N'',NULL,NULL " +
            "FROM #copy_rows p JOIN #made m ON m.idx = p.parent_idx " +
            "JOIN dbo.catalog_items ci ON ci.[key]=p.[key]", conn, tx))
        {
            insertKids.Parameters.Add("@section", SqlDbType.BigInt).Value = sectionId;
            insertKids.Parameters.Add("@at", SqlDbType.Int).Value = insertAt;
            if (insertKids.ExecuteNonQuery() != total - rows.Count)
                throw new DBConcurrencyException("Вставка комплектующих неполная. Транзакция отменена.");
        }
        using (var cleanup = new SqlCommand("DROP TABLE #copy_rows; DROP TABLE #made;", conn, tx))
            cleanup.ExecuteNonQuery();
    }

    /// <summary>Инструкция замены строки КП: null — поле не меняется (kp_clipboard.replace_rows).</summary>
    public sealed record ReplaceUpdate(long PositionId, string? Name, string? Unit,
        decimal? Qty, decimal? Price, decimal? PurchasePrice);

    /// <summary>
    /// Заменить выделенные строки КП данными из буфера (kp_clipboard.replace_rows).
    /// Один-в-один (<paramref name="updates"/>): строки правятся на месте — примечания,
    /// комплектующие, закупки и место в системе остаются; разное число
    /// (<paramref name="deleteIds"/> + <paramref name="insertRows"/>): выделенные удаляются
    /// с защитой актов и выдач, скопированные встанут на их место. Одна транзакция,
    /// meta.rev, журнал по позиции как update_position; вставка журнала не пишет.
    /// </summary>
    public static SaveResult ReplaceRows(string connectionString, long contractId,
        IReadOnlyList<ReplaceUpdate> updates, IReadOnlyList<long> deleteIds,
        IReadOnlyList<CopiedRow> insertRows, string expectedRevision)
    {
        var mismatch = deleteIds is { Count: > 0 };
        if (updates is null || deleteIds is null || insertRows is null)
            return SaveResult.Fail("Данные замены не переданы.", expectedRevision);
        if (updates.Count == 0 && !mismatch)
            return SaveResult.Fail("Выделите строки КП, которые заменить.", expectedRevision);
        if (mismatch && (updates.Count > 0 || insertRows.Count == 0))
            return SaveResult.Fail("Замена с разным числом строк: нужны строки буфера для вставки.", expectedRevision);
        if (updates.Count > 5000 || deleteIds.Count > 5000 || insertRows.Count > PastedPositions.MaxRows)
            return SaveResult.Fail("Слишком много строк для замены (лимит 5000).", expectedRevision);
        var targets = mismatch ? deleteIds.ToArray() : updates.Select(u => u.PositionId).ToArray();
        if (contractId <= 0 || targets.Any(id => id <= 0) || targets.Distinct().Count() != targets.Length)
            return SaveResult.Fail("Некорректные или повторяющиеся позиции замены.", expectedRevision);
        foreach (var u in updates)
        {
            if (u.Qty is < 0 || u.Price is < 0 || u.PurchasePrice is < 0 ||
                u.Name is { Length: 0 or > 2000 } || u.Unit is { Length: > 32 })
                return SaveResult.Fail("Данные замены не прошли проверку.", expectedRevision);
        }

        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = LockRevision(conn, tx);
        if (rev is null) return SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Данные изменены другим сотрудником. Сначала обнови смету.", rev);
        var contract = LoadPositionContract(conn, tx, contractId);
        if (contract is null) return SaveResult.Fail("Документ не найден.", rev);
        if (contract.Value.Locked) return SaveResult.Fail("Подписанная смета недоступна для замены.", rev);

        // Validate every target, not just the first deleted row. Do not silently
        // skip missing rows or allow a foreign document into a destructive batch.
        var targetSections = ReadReplaceTargetSections(conn, tx, contractId, targets);
        if (targetSections.Count != targets.Length)
            return SaveResult.Fail("Не все позиции замены принадлежат выбранной смете.", rev);

        // Якорь вставки при разном числе: место первой удаляемой строки в порядке показа.
        int insertAt = 0;
        long replaceSystemId = 0;
        List<OrderedPosition> ordered = new();
        if (mismatch)
        {
            replaceSystemId = targetSections[deleteIds[0]];
            ordered = TreeOrder(LoadOrderedPositions(conn, tx, contractId, replaceSystemId));
            var doomedRoots = deleteIds.ToHashSet();
            insertAt = ordered.FindIndex(p => doomedRoots.Contains(p.Id) || doomedRoots.Contains(p.ParentId ?? -1));
            if (insertAt < 0) insertAt = ordered.Count;
        }

        // 1) Обновление один-в-один (каждая строка — как update_position: только реальные поля).
        foreach (var u in updates)
        {
            var details = UpdateReplacePosition(conn, tx, contractId, contract.Value.ObjectId,
                contract.Value.DocumentName, u);
            if (details is null) continue;                    // нет изменений или строка исчезла
        }

        // 2) Разное число: вставить скопированные на место, затем удалить выделенные.
        if (mismatch)
        {
            var sections = targetSections.Values.Distinct().ToArray();
            WritePositionOrder(conn, tx, replaceSystemId, ordered);
            InsertRowsAt(conn, tx, replaceSystemId, insertAt, insertRows);
            DeleteReplaced(conn, tx, contractId, deleteIds);
            foreach (var section in sections)
                RenumberSection(conn, tx, section);
        }

        var updated = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, updated);
    }

    private static Dictionary<long, long> ReadReplaceTargetSections(SqlConnection conn, SqlTransaction tx,
        long contractId, IReadOnlyList<long> targets)
    {
        var result = new Dictionary<long, long>();
        // SQL Server supports at most 2100 parameters per command. Keep the
        // documented 5000-row operation below that limit.
        foreach (var batch in targets.Chunk(500))
        {
            using var cmd = new SqlCommand { Connection = conn, Transaction = tx };
            cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            var names = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                var name = "@p" + i.ToString(CultureInfo.InvariantCulture);
                names.Add(name);
                cmd.Parameters.Add(name, SqlDbType.BigInt).Value = batch[i];
            }
            cmd.CommandText = "SELECT p.id,p.system_id FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
                "JOIN dbo.systems sy ON sy.id=p.system_id WHERE sy.contract_id=@contract " +
                "AND p.parent_id IS NULL AND p.out_of_kp=0 AND p.id IN (" + string.Join(",", names) + ")";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture));
        }
        return result;
    }

    /// <summary>Правка одной строки замены: только реальные поля, формулы чисел сбрасываются,
    /// каталог перенаправляется при смене имени/единицы, цена синхронизируется в акты.
    /// Возвращает текст журнала или null, если менять было нечего.</summary>
    private static string? UpdateReplacePosition(SqlConnection conn, SqlTransaction tx,
        long contractId, long objectId, string documentName, ReplaceUpdate u)
    {
        decimal oldQty, oldPrice;
        decimal? oldPurchase;
        string oldName, oldUnit, oldNote, oldFormulas, oldExtra;
        bool locked;
        long systemId;
        string systemName, contractTitle;
        using (var read = new SqlCommand(
            "SELECT p.qty, p.price, p.purchase_price, p.name, p.unit, p.note, p.formulas, p.extra_json, " +
            "c.locked, sy.id, sy.name, c.name " +
            "FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.systems sy ON sy.id=p.system_id JOIN dbo.contracts c ON c.id=sy.contract_id " +
            "WHERE p.id=@id AND sy.contract_id=@cid", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = u.PositionId;
            read.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            if (!r.Read()) return null;
            oldQty = r.IsDBNull(0) ? 0m : Convert.ToDecimal(r.GetValue(0), CultureInfo.InvariantCulture);
            oldPrice = r.IsDBNull(1) ? 0m : Convert.ToDecimal(r.GetValue(1), CultureInfo.InvariantCulture);
            oldPurchase = r.IsDBNull(2) ? null : Convert.ToDecimal(r.GetValue(2), CultureInfo.InvariantCulture);
            oldName = r.IsDBNull(3) ? "" : r.GetString(3);
            oldUnit = r.IsDBNull(4) ? "" : r.GetString(4);
            oldNote = r.IsDBNull(5) ? "" : r.GetString(5);
            oldFormulas = r.IsDBNull(6) ? "" : r.GetString(6);
            oldExtra = r.IsDBNull(7) ? "" : r.GetString(7);
            locked = !r.IsDBNull(8) && Convert.ToBoolean(r.GetValue(8), CultureInfo.InvariantCulture);
            systemId = Convert.ToInt64(r.GetValue(9), CultureInfo.InvariantCulture);
            systemName = r.IsDBNull(10) ? "" : r.GetString(10);
            contractTitle = r.IsDBNull(11) ? "" : r.GetString(11);
        }
        if (locked) return null;

        var changes = new List<(string Field, object? After, object? Before, string Column)>();
        if (u.Name is not null && u.Name != oldName) changes.Add(("name", u.Name, oldName, "name"));
        if (u.Unit is not null && u.Unit != oldUnit) changes.Add(("unit", u.Unit, oldUnit, "unit"));
        if (u.Qty is { } qty && qty != oldQty) changes.Add(("qty", qty, oldQty, "qty"));
        if (u.Price is { } price && price != oldPrice) changes.Add(("price", price, oldPrice, "price"));
        if (u.PurchasePrice is { } purchase && purchase != oldPurchase)
            changes.Add(("purchase_price", purchase, oldPurchase, "purchase_price"));
        if (changes.Count == 0) return null;

        // Формула чисел, записанного «каким-то другим способом», сбрасывается (update_position).
        JsonObject? formulas = null;
        try
        {
            formulas = string.IsNullOrWhiteSpace(oldFormulas) ? new JsonObject() :
                JsonNode.Parse(oldFormulas) as JsonObject ?? throw new FormatException("Некорректный JSON формул.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new InvalidOperationException("Нельзя безопасно обновить формулы позиции: " + ex.Message);
        }
        foreach (var numeric in new[] { "qty", "price", "purchase_price" })
            if (changes.Any(c => c.Field == numeric)) formulas.Remove(numeric);

        long? catalogId = null;
        var newName = changes.FirstOrDefault(c => c.Field == "name").After as string ?? oldName;
        var newUnit = changes.FirstOrDefault(c => c.Field == "unit").After as string ?? oldUnit;
        if (changes.Any(c => c.Field is "name" or "unit") && !string.IsNullOrWhiteSpace(newName))
        {
            var key = Catalog.Key(newName, newUnit);
            using var catalog = new SqlCommand(
                "SELECT id FROM dbo.catalog_items WITH (UPDLOCK,HOLDLOCK) WHERE [key]=@key", conn, tx);
            catalog.Parameters.Add("@key", SqlDbType.NVarChar, 600).Value = key;
            var found = catalog.ExecuteScalar();
            if (found is not null and not DBNull)
                catalogId = Convert.ToInt64(found, CultureInfo.InvariantCulture);
            else
            {
                using var create = new SqlCommand(
                    "INSERT INTO dbo.catalog_items ([key],name,unit) OUTPUT INSERTED.id " +
                    "VALUES (@key,@name,@unit)", conn, tx);
                create.Parameters.Add("@key", SqlDbType.NVarChar, 600).Value = key;
                create.Parameters.Add("@name", SqlDbType.NVarChar, -1).Value = newName;
                create.Parameters.Add("@unit", SqlDbType.NVarChar, 32).Value = newUnit;
                catalogId = Convert.ToInt64(create.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        // «Из спецификации» пометка больше не относится при смене названия.
        string? newExtra = null;
        if (changes.Any(c => c.Field == "name") && oldExtra.Length > 0)
        {
            try
            {
                var extra = JsonNode.Parse(oldExtra) as JsonObject;
                if (extra is not null && extra.Remove("src"))
                    newExtra = extra.Count > 0 ? extra.ToJsonString() : "";
            }
            catch (JsonException) { /* испорченный extra_json не трогаем */ }
        }

        var set = new List<string>();
        foreach (var (field, after, _, column) in changes) set.Add($"[{column}]=@{field}");
        if (changes.Any(c => c.Field is "name" or "unit")) set.Add("catalog_item_id=@catalog");
        if (newExtra is not null) set.Add("extra_json=@extra");
        if (changes.Any(c => c.Field is "qty" or "price" or "purchase_price")) set.Add("formulas=@formulas");
        using (var update = new SqlCommand(
            $"UPDATE dbo.positions SET {string.Join(", ", set)} WHERE id=@id", conn, tx))
        {
            foreach (var (field, after, _, _) in changes)
                update.Parameters.AddWithValue($"@{field}",
                    after is decimal d ? (object)(double)d : after);
            update.Parameters.AddWithValue("@catalog",
                catalogId is null ? DBNull.Value : (object)catalogId.Value);
            update.Parameters.AddWithValue("@extra", (object?)newExtra ?? DBNull.Value);
            update.Parameters.AddWithValue("@formulas", formulas.ToJsonString());
            update.Parameters.AddWithValue("@id", u.PositionId);
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Позиция исчезла во время замены.");
        }

        if (changes.Any(c => c.Field == "price"))
            SyncActWorkTouched(conn, tx, u.PositionId, price: changes.First(c => c.Field == "price").After as decimal?);
        var details = $"{contractTitle} / {systemName} / {oldName[..Math.Min(60, oldName.Length)]}: " +
            string.Join("; ", changes.Select(c =>
                $"{c.Field}: «{c.Before ?? ""}» → «{c.After ?? ""}»"));
        LogReplace(conn, tx, u.PositionId, details, contractId, objectId, documentName, systemName);
        return details;
    }

    private static void SyncActWorkTouched(SqlConnection conn, SqlTransaction tx,
        long positionId, decimal? price)
    {
        using var acts = new SqlCommand(
            "UPDATE dbo.act_lines SET price=@v WHERE position_id=@id " +
            "AND act_id IN (SELECT id FROM dbo.acts WHERE signed=0)", conn, tx);
        acts.Parameters.Add("@v", SqlDbType.Float).Value = (double)(price ?? 0m);
        acts.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
        acts.ExecuteNonQuery();
        SyncActWork(conn, tx, TouchedUnsignedActs(conn, tx, positionId, ContractOf(conn, tx, positionId)));
    }

    private static long ContractOf(SqlConnection conn, SqlTransaction tx, long positionId)
    {
        using var cmd = new SqlCommand(
            "SELECT sy.contract_id FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id WHERE p.id=@id",
            conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void LogReplace(SqlConnection conn, SqlTransaction tx, long positionId,
        string details, long contractId, long objectId, string documentName, string systemName)
    {
        using var cmd = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'position',@id,N'update',@details,@contract,@object)", conn, tx);
        cmd.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
        cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
        cmd.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        cmd.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Удалить заменённые строки: защита актов (любых) и выдач, детали и дети первыми.</summary>
    private static void DeleteReplaced(SqlConnection conn, SqlTransaction tx, long contractId, IReadOnlyList<long> deleteIds)
    {
        using (var table = new SqlCommand(
            "CREATE TABLE #replace_delete(id BIGINT NOT NULL PRIMARY KEY)", conn, tx))
            table.ExecuteNonQuery();
        var ids = deleteIds.ToList();
        for (var offset = 0; offset < ids.Count; offset += 250)
        {
            using var fill = new SqlCommand { Connection = conn, Transaction = tx };
            var placeholders = new List<string>();
            for (var i = 0; i < Math.Min(250, ids.Count - offset); i++)
            {
                var param = "@p" + i.ToString(CultureInfo.InvariantCulture);
                placeholders.Add("(" + param + ")");
                fill.Parameters.Add(param, SqlDbType.BigInt).Value = ids[offset + i];
            }
            fill.CommandText = "INSERT INTO #replace_delete(id) VALUES " + string.Join(",", placeholders);
            fill.ExecuteNonQuery();
        }
        using (var children = new SqlCommand(
            "INSERT INTO #replace_delete(id) SELECT p.id FROM dbo.positions p " +
            "JOIN #replace_delete AS d ON d.id=p.parent_id " +
            "WHERE NOT EXISTS (SELECT 1 FROM #replace_delete AS e WHERE e.id=p.id)", conn, tx))
            children.ExecuteNonQuery();

        using (var scope = new SqlCommand(
            "SELECT TOP(1) p.id FROM #replace_delete d JOIN dbo.positions p ON p.id=d.id " +
            "LEFT JOIN dbo.systems sy ON sy.id=p.system_id LEFT JOIN dbo.positions parent ON parent.id=p.parent_id " +
            "WHERE sy.id IS NULL OR sy.contract_id<>@contract OR p.out_of_kp=1 " +
            "OR (p.parent_id IS NOT NULL AND (parent.id IS NULL OR parent.system_id<>p.system_id))", conn, tx))
        {
            scope.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (scope.ExecuteScalar() is not null)
                throw new InvalidOperationException("Обнаружены чужие или несогласованные комплектующие. Замена отменена.");
        }

        using (var check = new SqlCommand(
            "SELECT TOP(1) p.name,a.number FROM dbo.act_lines AS al WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #replace_delete AS d ON d.id=al.position_id " +
            "JOIN dbo.positions AS p ON p.id=al.position_id " +
            "JOIN dbo.acts AS a ON a.id=al.act_id", conn, tx))
        {
            using var reader = check.ExecuteReader();
            if (reader.Read())
            {
                var name = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var number = reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "";
                throw new InvalidOperationException(
                    $"«{name[..Math.Min(60, name.Length)]}» есть в КС-2 № {number}. Сначала убери строку из акта.");
            }
        }
        using (var check = new SqlCommand(
            "SELECT TOP(1) m.id FROM dbo.movements AS m WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #replace_delete AS d ON d.id=m.position_id WHERE m.shipment_id IS NOT NULL", conn, tx))
            if (check.ExecuteScalar() is not null)
                throw new InvalidOperationException("Позиция связана с проведённой выдачей. Проверь ведомость выдачи.");
        using (var check = new SqlCommand(
            "SELECT TOP(1) sl.id FROM dbo.shipment_lines AS sl WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #replace_delete AS d ON d.id=sl.position_id " +
            "JOIN dbo.shipments AS sh ON sh.id=sl.shipment_id WHERE sh.fixed=1", conn, tx))
            if (check.ExecuteScalar() is not null)
                throw new InvalidOperationException("Позиция связана с закреплённой ведомостью выдачи.");

        foreach (var table in new[] { "procurement", "movements", "shipment_lines" })
            using (var delete = new SqlCommand(
                $"DELETE t FROM dbo.[{table}] AS t JOIN #replace_delete AS d ON d.id=t.position_id", conn, tx))
                delete.ExecuteNonQuery();
        using (var deleteSubs = new SqlCommand(
            "DELETE p FROM dbo.positions AS p JOIN #replace_delete AS d ON d.id=p.parent_id", conn, tx))
            deleteSubs.ExecuteNonQuery();
        using (var deleteRoots = new SqlCommand(
            "DELETE p FROM dbo.positions AS p JOIN #replace_delete AS d ON d.id=p.id", conn, tx))
            deleteRoots.ExecuteNonQuery();
        using (var cleanup = new SqlCommand("DROP TABLE #replace_delete;", conn, tx))
            cleanup.ExecuteNonQuery();
    }

    /// <summary>Перенумеровать раздел с уплотнением порядков (gaps после удаления).</summary>
    private static void RenumberSection(SqlConnection conn, SqlTransaction tx, long sectionId)
    {
        using var renumber = new SqlCommand(
            "WITH ord AS (SELECT id,ROW_NUMBER() OVER (ORDER BY [order],id)-1 AS n " +
            "FROM dbo.positions WHERE system_id=@system) " +
            "UPDATE p SET [order]=CONVERT(INT,ord.n) " +
            "FROM dbo.positions AS p JOIN ord ON ord.id=p.id", conn, tx);
        renumber.Parameters.Add("@system", SqlDbType.BigInt).Value = sectionId;
        renumber.ExecuteNonQuery();
    }

    public static SaveResult PastePositions(string connectionString, long contractId,
        long sectionId, long? afterPositionId, IReadOnlyList<PastedPosition> rows, string expectedRevision)
    {
        if (contractId <= 0 || sectionId <= 0 || rows is null || rows.Count is 0 or > PastedPositions.MaxRows)
            return SaveResult.Fail("Выбери раздел и от 1 до 5000 строк Excel.", expectedRevision);
        // This method is public: validate callers other than the WPF clipboard as well.
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 2000 || r.Unit.Length > 32 ||
                r.Note.Length > 10_000 || r.Qty < 0 || r.Price < 0 || r.PurchasePrice < 0 ||
                Catalog.Key(r.Name, r.Unit).Length > 600)
                return SaveResult.Fail("Данные вставки не прошли проверку. Проверь названия, единицы и цены.", expectedRevision);
        }
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = LockRevision(conn, tx);
        if (rev is null) return SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Данные изменены другим сотрудником. Сначала обнови смету.", rev);
        var contract = LoadPositionContract(conn, tx, contractId);
        if (contract is null) return SaveResult.Fail("Документ не найден.", rev);
        if (contract.Value.Locked) return SaveResult.Fail("Подписанная смета недоступна для вставки.", rev);
        var ordered = TreeOrder(LoadOrderedPositions(conn, tx, contractId, sectionId));
        var insertAt = ordered.Count;
        if (afterPositionId is long after)
        {
            var idx = ordered.FindIndex(p => p.Id == after);
            if (idx < 0) return SaveResult.Fail("Выбранная позиция вставки отсутствует в разделе.", rev);
            var rootId = ordered[idx].ParentId ?? after;
            insertAt = ordered.FindLastIndex(p => p.Id == rootId || p.ParentId == rootId) + 1;
        }

        // Repack original ordering once to remove gaps, then shift only the tail.
        WritePositionOrder(conn, tx, sectionId, ordered);
        using (var shift = new SqlCommand(
            "UPDATE dbo.positions SET [order]=[order]+@count " +
            "WHERE system_id=@section AND [order]>=@at", conn, tx))
        {
            shift.Parameters.Add("@count", SqlDbType.Int).Value = rows.Count;
            shift.Parameters.Add("@section", SqlDbType.BigInt).Value = sectionId;
            shift.Parameters.Add("@at", SqlDbType.Int).Value = insertAt;
            shift.ExecuteNonQuery();
        }
        using (var create = new SqlCommand(
            "CREATE TABLE #paste_rows (idx INT PRIMARY KEY, [key] NVARCHAR(600) NOT NULL, " +
            "name NVARCHAR(MAX) NOT NULL, unit NVARCHAR(32) NOT NULL, " +
            "qty FLOAT NOT NULL, price FLOAT NOT NULL, purchase_price FLOAT NULL, note NVARCHAR(MAX) NOT NULL)", conn, tx))
            create.ExecuteNonQuery();
        var table = new DataTable();
        table.Columns.Add("idx", typeof(int));
        table.Columns.Add("key", typeof(string));
        table.Columns.Add("name", typeof(string));
        table.Columns.Add("unit", typeof(string));
        table.Columns.Add("qty", typeof(double));
        table.Columns.Add("price", typeof(double));
        table.Columns.Add("purchase_price", typeof(double));
        table.Columns.Add("note", typeof(string));
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            table.Rows.Add(i, Catalog.Key(r.Name, r.Unit), r.Name, r.Unit, (double)r.Qty,
                (double)r.Price, r.PurchasePrice is null ? DBNull.Value : (object)(double)r.PurchasePrice.Value, r.Note);
        }
        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx))
        {
            bulk.DestinationTableName = "#paste_rows";
            bulk.BatchSize = 250;
            bulk.BulkCopyTimeout = 60;
            foreach (DataColumn column in table.Columns)
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            bulk.WriteToServer(table);
        }
        // Create missing catalog entries once per key (same name/unit normalization as editing a cell).
        using (var catalog = new SqlCommand(
            "WITH source AS (SELECT [key], MIN(name) AS name, MIN(unit) AS unit " +
            "FROM #paste_rows GROUP BY [key]) " +
            "INSERT INTO dbo.catalog_items ([key],name,unit) " +
            "SELECT src.[key],src.name,src.unit FROM source src " +
            "WHERE NOT EXISTS (SELECT 1 FROM dbo.catalog_items ci WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE ci.[key]=src.[key])", conn, tx))
            catalog.ExecuteNonQuery();

        // The order column carries the exact sequence regardless of INSERT/identity ordering.
        using (var insert = new SqlCommand(
            "INSERT INTO dbo.positions (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price," +
            "purchase_price,note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual," +
            "work_price,cost_note,cost_color,work_sell,work_cost) " +
            "SELECT @section,NULL,ci.id,N'',N'',p.name,p.unit,p.qty,p.price,p.purchase_price," +
            "p.note,N'',NULL,@at+p.idx,0,N'',NULL,NULL,NULL,N'',N'',NULL,NULL " +
            "FROM #paste_rows p JOIN dbo.catalog_items ci ON ci.[key]=p.[key]", conn, tx))
        {
            insert.Parameters.Add("@section", SqlDbType.BigInt).Value = sectionId;
            insert.Parameters.Add("@at", SqlDbType.Int).Value = insertAt;
            if (insert.ExecuteNonQuery() != rows.Count)
                throw new DBConcurrencyException("Импорт позиций неполный. Транзакция отменена.");
        }
        LogSectionChange(conn, tx, sectionId, contractId, contract.Value.ObjectId, "paste",
            $"{contract.Value.DocumentName}: вставлено из Excel {rows.Count} позиций в раздел {sectionId}");
        var updated = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, updated);
    }
}
