using System.Data;
using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed record SpecSqlPreview(string Revision, SpecUpdateDiff Diff, string? Blocker);
public sealed record SpecSqlApplyResult(string Revision, long HistoryId, string Title,
    decimal TotalBefore, decimal TotalAfter);
public sealed record SpecSqlHistory(long Id, DateTime CreatedAt, string User,
    string File, string Title, decimal TotalBefore, decimal TotalAfter, string Data);

/// <summary>Последняя ревизия спецификации — для лёгкой проверки «изм.».</summary>
public sealed record SpecRevisionHead(long Id, string? Title, string? User);

/// <summary>
/// SQL Server-only import of Python spec_update.apply. All requested changes are
/// re-planned under Serializable/meta.rev, then applied atomically. The pilot
/// guard is intentional until SQL Server/KS/DS integration acceptance is complete.
/// </summary>
public static class SqlServerSpecUpdate
{
    public static IReadOnlyList<SpecIncomingSystem> Incoming(ExcelImportBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var order = 0;
        return block.Sections.Select(section => new SpecIncomingSystem(section.Name,
            section.Positions.Select(p => new SpecIncomingPosition(
                p.Name, p.Unit, p.Quantity, p.Note, p.SourceRow > 0 ? p.SourceRow : ++order))
                .ToArray())).ToArray();
    }

    public static SpecSqlPreview Preview(string connectionString, long contractId,
        IReadOnlyList<SpecIncomingSystem> incoming)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx) ??
            throw new InvalidOperationException("SQL Server: meta.rev отсутствует.");
        var doc = ReadDocument(conn, tx, contractId);
        var snapshot = ReadPositions(conn, tx, contractId);
        var diff = SpecUpdatePlan.Compare(snapshot.Systems, snapshot.Positions, incoming);
        tx.Commit();
        return new SpecSqlPreview(rev, diff, doc.Blocker);
    }

    public static IReadOnlyList<SpecSqlHistory> ReadHistory(string connectionString, long contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            @"SELECT id,created_at,[user],[file],title,total_before,total_after,data
              FROM dbo.spec_revisions WHERE contract_id=@id ORDER BY id DESC", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        var result = new List<SpecSqlHistory>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new SpecSqlHistory(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToDateTime(r.GetValue(1), CultureInfo.InvariantCulture),
                Text(r, 2), Text(r, 3), Text(r, 4), Decimal(r, 5),
                Decimal(r, 6), Text(r, 7)));
        return result;
    }

    /// <summary>
    /// Лёгкая проверка «изм.»: только последняя ревизия вместо полного дампа
    /// таблицы. Читается на том же соединении, что и смета (S-01).
    /// </summary>
    public static SpecRevisionHead? LatestRevision(SqlConnection conn, SqlTransaction? tx,
        long contractId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var cmd = new SqlCommand(
            "SELECT TOP(1) id,title,[user] FROM dbo.spec_revisions " +
            "WHERE contract_id=@id ORDER BY id DESC", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        var found = r.Read();
        token.ThrowIfCancellationRequested();
        if (!found) return null;
        return new SpecRevisionHead(
            Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2));
    }

    public static SpecSqlApplyResult Apply(string connectionString, long contractId,
        string sourceFile, IReadOnlyList<SpecIncomingSystem> incoming,
        IReadOnlyCollection<string> selectedKeys, bool keepOrder, string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(selectedKeys);
        if (contractId <= 0 || selectedKeys.Count == 0)
            throw new ArgumentException("Нужно выбрать хотя бы одно изменение.");
        if (incoming.Sum(x => x.Positions.Count) > 100_000)
            throw new ArgumentException("В спецификации более 100 000 строк.");
        cancellationToken.ThrowIfCancellationRequested();
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        var doc = ReadDocument(conn, tx, contractId);
        if (doc.Blocker is not null) throw new InvalidOperationException(doc.Blocker);
        var snapshot = ReadPositions(conn, tx, contractId);
        var fresh = SpecUpdatePlan.Compare(snapshot.Systems, snapshot.Positions, incoming);
        var byKey = fresh.Changes.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var keys = selectedKeys.ToHashSet(StringComparer.Ordinal);
        if (keys.Count != selectedKeys.Count || keys.Any(k => !byKey.ContainsKey(k)))
            throw new DBConcurrencyException("Предпросмотр спецификации устарел. Прочитай Excel заново.");
        var chosen = fresh.Changes.Where(c => keys.Contains(c.Key)).ToArray();
        if (chosen.Length == 0) throw new InvalidOperationException("Нет изменений для применения.");
        var totalBefore = Total(conn, tx, contractId);
        var byName = snapshot.Systems.ToDictionary(s => SpecUpdatePlan.Normalize(s.Name), s => s.Id);

        long EnsureSystem(string name)
        {
            var key = SpecUpdatePlan.Normalize(name);
            if (byName.TryGetValue(key, out var existing)) return existing;
            using var insert = new SqlCommand(@"
                INSERT dbo.systems(contract_id,parent_id,name,[order],smr_amount,heading,source_num,stage_sys_src)
                OUTPUT INSERTED.id
                VALUES(@contract,NULL,@name,
                    (SELECT COALESCE(MAX([order]),-1)+1 FROM dbo.systems WHERE contract_id=@contract),
                    NULL,N'',N'',NULL)", conn, tx);
            insert.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            insert.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = name;
            var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            byName[key] = id;
            return id;
        }

        // 1. Renames preserve existing system IDs and references.
        foreach (var change in chosen.Where(x => x.Kind == "sys_rename"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var oldName = SpecUpdatePlan.Normalize(change.OldSystem);
            if (!byName.TryGetValue(oldName, out var id))
                throw new DBConcurrencyException("Переименовываемый раздел исчез.");
            if (byName.TryGetValue(SpecUpdatePlan.Normalize(change.System), out var duplicate) &&
                duplicate != id)
                throw new InvalidOperationException("Переименование создало бы дубль раздела.");
            using var cmd = new SqlCommand(
                "UPDATE dbo.systems SET name=@name WHERE id=@id AND contract_id=@contract", conn, tx);
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = change.System;
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (cmd.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Раздел исчез.");
            byName.Remove(oldName);
            byName[SpecUpdatePlan.Normalize(change.System)] = id;
        }
        foreach (var change in chosen.Where(x => x.Kind == "sys_add"))
            EnsureSystem(change.System);

        // Capture source prices before any row is deleted; Python carries a
        // price across systems instead of silently zeroing it on a move.
        var sourcePrices = snapshot.Positions.Where(p => p.FromSpecification)
            .GroupBy(p => (SpecUpdatePlan.ItemKey(p.SourceName, p.Unit),
                System: SpecUpdatePlan.Normalize(snapshot.Systems.Single(s => s.Id == p.SystemId).Name)))
            .ToDictionary(g => g.Key, g => g.First());

        // 2. Qty changes preserve manually entered name, price and procurement.
        foreach (var change in chosen.Where(x => x.Kind == "qty"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (id, index) in change.MatchingIds.Select((id, i) => (id, i)))
            {
                var original = snapshot.Positions.Single(p => p.Id == id);
                if (original.HasReferences)
                    throw new InvalidOperationException(
                        $"«{change.Name}» связана с КС, закупками, этапами или выдачей. " +
                        "Изменение количества требует ручной проверки связанных документов.");
                using var cmd = new SqlCommand(@"
                    UPDATE dbo.positions SET qty=@qty,
                    formulas=CASE WHEN ISJSON(formulas)=1
                        THEN JSON_MODIFY(formulas, '$.qty', NULL) ELSE N'' END
                    WHERE id=@id AND system_id=@sys", conn, tx);
                cmd.Parameters.Add("@qty", SqlDbType.Float).Value =
                    (double)(index == 0 ? change.NewQuantity ?? 0 : 0);
                cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                cmd.Parameters.Add("@sys", SqlDbType.BigInt).Value = original.SystemId;
                if (cmd.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Позиция исчезла при обновлении количества.");
            }
        }

        // 3. Rows removed from the new specification. Existing references are
        // kept with quantity zero, never cascaded to procurement/KS/shipments.
        foreach (var change in chosen.Where(x => x.Kind is "del" or "move_out"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var id in change.MatchingIds)
            {
                var original = snapshot.Positions.Single(p => p.Id == id);
                var hasComponents = snapshot.Positions.Any(p => p.ParentId == id);
                if (original.HasReferences || hasComponents)
                {
                    using var cmd = new SqlCommand(@"
                        UPDATE dbo.positions SET qty=0,
                            note=CASE WHEN note LIKE N'%нет в изм.%' THEN note
                            WHEN LEN(COALESCE(note,N''))=0 THEN N'нет в изм.'
                            ELSE CONCAT(note,N'; нет в изм.') END,
                            formulas=CASE WHEN ISJSON(formulas)=1
                                THEN JSON_MODIFY(formulas,'$.qty',NULL) ELSE N'' END
                        WHERE id=@id", conn, tx);
                    cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                    if (cmd.ExecuteNonQuery() != 1)
                        throw new DBConcurrencyException("Позиция исчезла при обнулении.");
                }
                else
                {
                    using var cmd = new SqlCommand(
                        "DELETE FROM dbo.positions WHERE id=@id AND parent_id IS NULL", conn, tx);
                    cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                    if (cmd.ExecuteNonQuery() != 1)
                        throw new DBConcurrencyException("Позиция исчезла при удалении.");
                }
            }
        }

        // 4. New rows. Catalog entries are created atomically. Imported schedule
        // quantities do not replace financial prices; new items start at zero.
        foreach (var change in chosen.Where(x => x.Kind is "add" or "move_in"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.Name.Length is 0 or > 2000 ||
                change.Unit.Length > 32 || change.System.Length > 255)
                throw new ArgumentException("Слишком длинное наименование или единица измерения.");
            var system = EnsureSystem(change.System);
            var price = 0m;
            decimal? purchase = null;
            var note = "";
            if (change.Kind == "move_in" &&
                sourcePrices.TryGetValue((SpecUpdatePlan.ItemKey(change.Name, change.Unit),
                    SpecUpdatePlan.Normalize(change.OldSystem)), out var source))
            {
                price = source.Price;
                purchase = source.PurchasePrice;
                note = source.Note;
            }
            var catalog = Catalog.Key(change.Name, change.Unit);
            if (catalog.Length > 600)
                throw new ArgumentException("Ключ каталога слишком длинный; нельзя обрезать и связывать с чужим материалом.");
            using (var ensure = new SqlCommand(@"
                IF NOT EXISTS(SELECT 1 FROM dbo.catalog_items WITH (UPDLOCK,HOLDLOCK) WHERE [key]=@key)
                    INSERT dbo.catalog_items([key],name,unit) VALUES(@key,@name,@unit)", conn, tx))
            {
                ensure.Parameters.Add("@key", SqlDbType.NVarChar, 600).Value = catalog;
                ensure.Parameters.Add("@name", SqlDbType.NVarChar, 2000).Value = change.Name;
                ensure.Parameters.Add("@unit", SqlDbType.NVarChar, 32).Value = change.Unit;
                ensure.ExecuteNonQuery();
            }
            using (var insert = new SqlCommand(@"
                INSERT dbo.positions
                (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,purchase_price,
                 note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual,
                 work_price,cost_note,cost_color,work_sell,work_cost)
                SELECT @sys,NULL,c.id,N'',N'',@name,@unit,@qty,@price,@purchase,
                    @note,@extra,@sourceRow,
                    (SELECT COALESCE(MAX([order]),-1)+1 FROM dbo.positions WHERE system_id=@sys),
                    0,N'',NULL,NULL,NULL,N'',N'',NULL,NULL
                FROM dbo.catalog_items c WHERE c.[key]=@key", conn, tx))
            {
                insert.Parameters.Add("@sys", SqlDbType.BigInt).Value = system;
                insert.Parameters.Add("@name", SqlDbType.NVarChar, 2000).Value = change.Name;
                insert.Parameters.Add("@unit", SqlDbType.NVarChar, 32).Value = change.Unit;
                insert.Parameters.Add("@qty", SqlDbType.Float).Value = (double)(change.NewQuantity ?? 0);
                insert.Parameters.Add("@price", SqlDbType.Float).Value = (double)price;
                insert.Parameters.Add("@purchase", SqlDbType.Float).Value =
                    purchase is decimal p ? (double)p : DBNull.Value;
                insert.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = note;
                insert.Parameters.Add("@extra", SqlDbType.NVarChar, -1).Value =
                    JsonSerializer.Serialize(new Dictionary<string, string> { ["src"] = change.Name });
                insert.Parameters.Add("@sourceRow", SqlDbType.Int).Value = change.Order;
                insert.Parameters.Add("@key", SqlDbType.NVarChar, 600).Value = catalog;
                if (insert.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Не удалось добавить строку спецификации.");
            }
        }

        // 5. Removed systems are deleted only if literally empty, including no
        // hand-added rows and no subsections. No cascade is ever used.
        foreach (var change in chosen.Where(x => x.Kind == "sys_del"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byName.TryGetValue(SpecUpdatePlan.Normalize(change.System), out var id))
                continue;
            using var cmd = new SqlCommand(@"
                DELETE FROM dbo.systems
                WHERE id=@id AND contract_id=@contract AND
                NOT EXISTS(SELECT 1 FROM dbo.positions WHERE system_id=@id) AND
                NOT EXISTS(SELECT 1 FROM dbo.systems WHERE parent_id=@id)", conn, tx);
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (cmd.ExecuteNonQuery() == 1)
                byName.Remove(SpecUpdatePlan.Normalize(change.System));
        }

        if (!keepOrder)
            Reorder(conn, tx, contractId, incoming, cancellationToken);

        var totalAfter = Total(conn, tx, contractId);
        var json = JsonSerializer.Serialize(chosen.Select(x => new
        {
            kind=x.Kind, system=x.System, name=x.Name, unit=x.Unit,
            old_qty=x.OldQuantity, new_qty=x.NewQuantity, price=x.Price,
            pos_id=x.PositionId, old_system=x.OldSystem, warn=x.Warning,
            on=true, order=x.Order
        }));
        long historyId;
        string title;
        using (var insert = new SqlCommand(@"
            INSERT dbo.spec_revisions
                (contract_id,created_at,[user],[file],title,total_before,total_after,data)
            OUTPUT INSERTED.id
            SELECT @contract,SYSDATETIME(),@user,@file,
                CONCAT(N'изм. ',(SELECT COUNT_BIG(*)+1 FROM dbo.spec_revisions WHERE contract_id=@contract)),
                @before,@after,@data", conn, tx))
        {
            insert.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            insert.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value =
                Environment.UserName[..Math.Min(64,Environment.UserName.Length)];
            insert.Parameters.Add("@file", SqlDbType.NVarChar, -1).Value = sourceFile;
            insert.Parameters.Add("@before", SqlDbType.Float).Value = (double)totalBefore;
            insert.Parameters.Add("@after", SqlDbType.Float).Value = (double)totalAfter;
            insert.Parameters.Add("@data", SqlDbType.NVarChar, -1).Value = json;
            historyId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using (var titleCmd = new SqlCommand(
            "SELECT title FROM dbo.spec_revisions WHERE id=@id AND contract_id=@contract",conn,tx))
        {
            titleCmd.Parameters.Add("@id", SqlDbType.BigInt).Value = historyId;
            titleCmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            title = Convert.ToString(titleCmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
        }
        using (var log = new SqlCommand(@"
            INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id)
            VALUES(SYSDATETIME(),@user,N'contract',@contract,N'spec_update',
                @details,@contract,@object)", conn,tx))
        {
            log.Parameters.Add("@user", SqlDbType.NVarChar,64).Value =
                Environment.UserName[..Math.Min(64, Environment.UserName.Length)];
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = doc.ObjectId;
            log.Parameters.Add("@details", SqlDbType.NVarChar,-1).Value =
                $"{title}: изменено {chosen.Count(x=>x.Kind=="qty")}, " +
                $"новых {chosen.Count(x=>x.Kind is "add" or "move_in")}, " +
                $"убрано {chosen.Count(x=>x.Kind is "del" or "move_out")}";
            log.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        SqlServerWorkspace.BumpRevision(conn, tx);
        // Unknown outcome is deliberately propagated, never automatically retried.
        tx.Commit();
        return new SpecSqlApplyResult(SqlServerDatabase.Revision(conn), historyId,
            title, totalBefore, totalAfter);
    }

    private sealed record Document(long ObjectId,string? Blocker);
    private sealed record PositionSnapshot(IReadOnlyList<SpecExistingSystem> Systems,
        IReadOnlyList<SpecExistingPosition> Positions);

    private static Document ReadDocument(SqlConnection conn, SqlTransaction tx, long id)
    {
        using var cmd = new SqlCommand(@"
            SELECT c.object_id,c.locked,c.kind,c.m2_mode,c.stage_of,o.archived,
                c.smr_mode,
                CASE WHEN EXISTS(SELECT 1 FROM dbo.contracts s WHERE s.stage_of=c.id)
                     THEN 1 ELSE 0 END,
                CASE WHEN EXISTS(SELECT 1 FROM dbo.acts a WHERE a.contract_id=c.id AND a.signed=1)
                     THEN 1 ELSE 0 END
            FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id
            WHERE c.id=@id", conn,tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        using var r=cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Документ отсутствует.");
        var objectId=Convert.ToInt64(r.GetValue(0),CultureInfo.InvariantCulture);
        string? blocker=null;
        if (Convert.ToBoolean(r.GetValue(1),CultureInfo.InvariantCulture))
            blocker="Подписанный документ запрещено обновлять.";
        else if (Convert.ToBoolean(r.GetValue(5),CultureInfo.InvariantCulture))
            blocker="Архивный объект нельзя изменять.";
        else if (!r.IsDBNull(4) || Convert.ToBoolean(r.GetValue(7),CultureInfo.InvariantCulture))
            blocker="Документ связан с этапами. Обновляй его через управление этапами.";
        else if (Convert.ToBoolean(r.GetValue(8),CultureInfo.InvariantCulture))
            blocker="У документа есть подписанные КС-2: обновление требует отдельной приёмки.";
        else if (Text(r,3) is "sys" or "all")
            blocker="Для КП со свёрткой м² обновление спецификации ещё не разрешено.";
        else if (Text(r,6) == "systems")
            blocker="Для СМР по системам требуется отдельная сверка перед обновлением.";
        else if (Text(r,2) != "kp")
            blocker="Обновление спецификаций выполняется только для КП.";
        return new Document(objectId,blocker);
    }

    private static PositionSnapshot ReadPositions(SqlConnection conn,SqlTransaction tx,long contractId)
    {
        var systems=new List<SpecExistingSystem>();
        using(var cmd=new SqlCommand(@"
            SELECT id,name,[order],parent_id FROM dbo.systems WITH(UPDLOCK,HOLDLOCK)
            WHERE contract_id=@id ORDER BY [order],id",conn,tx))
        {
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            using var r=cmd.ExecuteReader();
            while(r.Read())systems.Add(new SpecExistingSystem(
                Convert.ToInt64(r.GetValue(0),CultureInfo.InvariantCulture),
                Text(r,1),Convert.ToInt32(r.GetValue(2),CultureInfo.InvariantCulture),
                r.IsDBNull(3)?null:Convert.ToInt64(r.GetValue(3),CultureInfo.InvariantCulture)));
        }
        var rows=new List<SpecExistingPosition>();
        using(var cmd=new SqlCommand(@"
            SELECT p.id,p.system_id,p.parent_id,p.name,p.unit,p.qty,p.price,
                p.purchase_price,p.note,p.extra_json,p.source_row,p.out_of_kp,
                CASE WHEN EXISTS(SELECT 1 FROM dbo.act_lines a WHERE a.position_id=p.id)
                  OR EXISTS(SELECT 1 FROM dbo.movements m WHERE m.position_id=p.id)
                  OR EXISTS(SELECT 1 FROM dbo.shipment_lines sl WHERE sl.position_id=p.id)
                  OR EXISTS(SELECT 1 FROM dbo.procurement q WHERE q.position_id=p.id)
                  OR EXISTS(SELECT 1 FROM dbo.positions st WHERE st.stage_src=p.id)
                  OR p.stage_src IS NOT NULL THEN 1 ELSE 0 END
            FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK)
            JOIN dbo.systems sy ON sy.id=p.system_id
            WHERE sy.contract_id=@id ORDER BY sy.[order],p.[order],p.id",conn,tx))
        {
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            cmd.CommandTimeout=180;
            using var r=cmd.ExecuteReader();
            while(r.Read())
            {
                var extra=Text(r,9);
                var source=SourceName(extra);
                rows.Add(new SpecExistingPosition(
                    Convert.ToInt64(r.GetValue(0),CultureInfo.InvariantCulture),
                    Convert.ToInt64(r.GetValue(1),CultureInfo.InvariantCulture),
                    r.IsDBNull(2)?null:Convert.ToInt64(r.GetValue(2),CultureInfo.InvariantCulture),
                    Text(r,3),Text(r,4),Decimal(r,5),Decimal(r,6),
                    r.IsDBNull(7)?null:Decimal(r,7),Text(r,8),
                    !r.IsDBNull(10)||!string.IsNullOrWhiteSpace(source),source,
                    Convert.ToBoolean(r.GetValue(12),CultureInfo.InvariantCulture),
                    r.IsDBNull(10)?null:Convert.ToInt32(r.GetValue(10),CultureInfo.InvariantCulture),
                    Convert.ToBoolean(r.GetValue(11),CultureInfo.InvariantCulture)));
            }
        }
        return new PositionSnapshot(systems,rows);
    }

    private static string SourceName(string? extra)
    {
        if(string.IsNullOrWhiteSpace(extra)) return "";
        try
        {
            using var json=JsonDocument.Parse(extra);
            if(json.RootElement.ValueKind==JsonValueKind.Object &&
                json.RootElement.TryGetProperty("src",out var src) &&
                src.ValueKind==JsonValueKind.String)
                return src.GetString() ?? "";
        }
        catch(JsonException) { /* historical empty/corrupt extra_json */ }
        return "";
    }

    /// <summary>Full KP total, with the same decimal rounding and form tags as Totals.ReadOne.</summary>
    private static decimal Total(SqlConnection conn,SqlTransaction tx,long contractId)
    {
        string form,mode;
        decimal rate,smrAmount,smrPercent,transport,pnr;
        using(var cmd=new SqlCommand(@"
            SELECT form,vat_rate,smr_mode,smr_amount,smr_percent,transport_amount,pnr_amount
            FROM dbo.contracts WHERE id=@id",conn,tx))
        {
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            using var r=cmd.ExecuteReader();
            if(!r.Read())throw new DBConcurrencyException("Документ исчез при расчёте итога.");
            form=Text(r,0);rate=Decimal(r,1);mode=Text(r,2);
            smrAmount=Decimal(r,3);smrPercent=Decimal(r,4);
            transport=Decimal(r,5);pnr=Decimal(r,6);
        }
        bool roundRows=false,vatTop=false;
        using(var cmd=new SqlCommand("SELECT rules FROM dbo.tags WHERE [key]=@form",conn,tx))
        {
            cmd.Parameters.Add("@form",SqlDbType.NVarChar,64).Value=form;
            var json=Convert.ToString(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
            if(!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    using var tags=JsonDocument.Parse(json);
                    if(tags.RootElement.ValueKind==JsonValueKind.Object)
                    {
                        roundRows=tags.RootElement.TryGetProperty("round_rows",out var rr) &&
                            rr.ValueKind==JsonValueKind.True;
                        vatTop=tags.RootElement.TryGetProperty("vat_top",out var vt) &&
                            vt.ValueKind==JsonValueKind.True;
                    }
                }
                catch(JsonException) { }
            }
        }
        decimal materials=0,work=0,systemsSmr=0;
        using(var cmd=new SqlCommand(
            "SELECT COALESCE(SUM(smr_amount),0) FROM dbo.systems WHERE contract_id=@id",conn,tx))
        {
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            systemsSmr=DecimalObject(cmd.ExecuteScalar());
        }
        using(var cmd=new SqlCommand(@"
            SELECT p.qty,p.price,p.work_price,p.out_of_kp FROM dbo.positions p
            JOIN dbo.systems s ON s.id=p.system_id
            WHERE s.contract_id=@id AND p.parent_id IS NULL",conn,tx))
        {
            cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            using var r=cmd.ExecuteReader();
            while(r.Read())
            {
                if(Convert.ToBoolean(r.GetValue(3),CultureInfo.InvariantCulture))continue;
                var qty=Decimal(r,0);
                var amount=qty*Decimal(r,1);
                materials+=roundRows?Money.Round2(amount):amount;
                work+=qty*Decimal(r,2);
            }
        }
        materials=Money.Round2(materials);
        var smr=mode switch
        {
            "percent"=>Money.Round2(materials*smrPercent/100m),
            "systems"=>Money.Round2(systemsSmr),
            "pos"=>Money.Round2(work),
            _=>smrAmount
        };
        var net=materials+smr+transport+pnr;
        return vatTop?Money.Round2(net+Money.Round2(net*rate/100m)):net;
    }

    private static void Reorder(SqlConnection conn,SqlTransaction tx,long contractId,
        IReadOnlyList<SpecIncomingSystem> incoming,CancellationToken cancellationToken)
    {
        var current=ReadPositions(conn,tx,contractId);
        var incomingMap=incoming.GroupBy(s=>SpecUpdatePlan.Normalize(s.Name))
            .ToDictionary(g=>g.Key,g=>g.SelectMany(s=>s.Positions)
                .GroupBy(p=>SpecUpdatePlan.ItemKey(p.Name,p.Unit))
                .ToDictionary(items=>items.Key,items=>items.Min(p=>p.Order)));
        var data=new DataTable();
        data.Columns.Add("id",typeof(long));
        data.Columns.Add("ord",typeof(int));
        data.Columns.Add("source",typeof(int));
        foreach(var sy in current.Systems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows=current.Positions.Where(p=>p.SystemId==sy.Id).ToList();
            var tops=rows.Where(p=>p.ParentId is null).ToList();
            var children=rows.Where(p=>p.ParentId is not null)
                .GroupBy(p=>p.ParentId!.Value).ToDictionary(g=>g.Key,g=>g.ToList());
            var spec=new List<(SpecExistingPosition P,int Rank)>();
            var handAfter=new Dictionary<long,List<SpecExistingPosition>>();
            long previous=0;
            var incomingRanks=incomingMap.GetValueOrDefault(SpecUpdatePlan.Normalize(sy.Name));
            foreach(var p in tops)
            {
                if(p.FromSpecification)
                {
                    var key=SpecUpdatePlan.ItemKey(
                        string.IsNullOrWhiteSpace(p.SourceName)?p.Name:p.SourceName,p.Unit);
                    var rank=incomingRanks is not null&&incomingRanks.TryGetValue(key,out var found)?
                        found:p.SourceRow??int.MaxValue;
                    spec.Add((p,rank));
                    previous=p.Id;
                }
                else
                {
                    if(!handAfter.TryGetValue(previous,out var list))
                        handAfter[previous]=list=new List<SpecExistingPosition>();
                    list.Add(p);
                }
            }
            var ordered=new List<SpecExistingPosition>();
            if(handAfter.TryGetValue(0,out var before))ordered.AddRange(before);
            var ranks=new Dictionary<long,int>();
            foreach(var (p,rank) in spec.OrderBy(x=>x.Rank).ThenBy(x=>x.P.SourceRow))
            {
                ordered.Add(p);
                ranks[p.Id]=rank==int.MaxValue?p.SourceRow??int.MaxValue:rank;
                if(handAfter.TryGetValue(p.Id,out var after))ordered.AddRange(after);
            }
            var ordinal=0;
            foreach(var row in ordered)
            {
                data.Rows.Add(row.Id,ordinal++,ranks.GetValueOrDefault(row.Id,row.SourceRow??int.MaxValue));
                if(children.TryGetValue(row.Id,out var subs))
                    foreach(var child in subs)
                        data.Rows.Add(child.Id,ordinal++,child.SourceRow??int.MaxValue);
            }
            if(ordinal!=rows.Count)
                throw new InvalidOperationException("При сортировке потеряны строки раздела.");
        }
        using(var create=new SqlCommand(
            "CREATE TABLE #spec_order(id BIGINT PRIMARY KEY,ord INT NOT NULL,source INT NOT NULL)",conn,tx))
            create.ExecuteNonQuery();
        using(var bulk=new SqlBulkCopy(conn,SqlBulkCopyOptions.Default,tx))
        {
            bulk.DestinationTableName="#spec_order";
            bulk.BatchSize=1000;
            bulk.BulkCopyTimeout=180;
            bulk.ColumnMappings.Add("id","id");
            bulk.ColumnMappings.Add("ord","ord");
            bulk.ColumnMappings.Add("source","source");
            bulk.WriteToServer(data);
        }
        using(var update=new SqlCommand(@"
            UPDATE p SET [order]=o.ord,
                source_row=CASE WHEN p.source_row IS NULL THEN NULL ELSE o.source END
            FROM dbo.positions p JOIN #spec_order o ON o.id=p.id
            JOIN dbo.systems sy ON sy.id=p.system_id WHERE sy.contract_id=@contract",conn,tx))
        {
            update.CommandTimeout=180;
            update.Parameters.Add("@contract",SqlDbType.BigInt).Value=contractId;
            if(update.ExecuteNonQuery()!=data.Rows.Count)
                throw new DBConcurrencyException("Перенумерация охватила не все позиции.");
        }
    }

    private static string Text(SqlDataReader r,int i) =>
        r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
    private static decimal Decimal(SqlDataReader r,int i) =>
        r.IsDBNull(i)?0:Money.FromDouble(Convert.ToDouble(r.GetValue(i),CultureInfo.InvariantCulture));
    private static decimal DecimalObject(object? value) =>
        value is null or DBNull?0:Money.FromDouble(Convert.ToDouble(value,CultureInfo.InvariantCulture));
}
