using System.Data;
using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Pilot-only, reversible version of Python area.collapse / area.expand.
/// Never run on production: reorganises persistent positions and section IDs.
/// One SQL Server SERIALIZABLE transaction and one meta.rev bump per operation.
/// </summary>
public static partial class SqlServerDatabase
{
    public sealed record AreaTransformResult(bool Saved, int Rows, int Groups,
        int Skipped, int NoArea, string? Error, string Revision)
    {
        public static AreaTransformResult Fail(string message, string revision) =>
            new(false, 0, 0, 0, 0, message, revision);
    }

    public static AreaTransformResult CollapseArea(string connectionString,
        long contractId, bool wholeContract, string expectedRevision)
    {
        if (contractId <= 0)
            return AreaTransformResult.Fail("Не выбран документ для сворачивания м².", expectedRevision);
        if (!AreaPilotAllowed(connectionString))
            return AreaTransformResult.Fail(
                "Сворачивание в м² разрешено только в тестовой базе GmcSmetaPilot_*.",
                expectedRevision);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx);
        if (revision is null) return AreaTransformResult.Fail("Нет meta.rev.", expectedRevision);
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            return AreaTransformResult.Fail("Договор изменён другим сотрудником. Обнови смету.", revision);

        var (objectId, title, reject) = AreaGuard(conn, tx, contractId);
        if (reject is not null) return AreaTransformResult.Fail(reject, revision);

        var rows = ReadCollapsePositions(conn, tx, contractId);
        var plan = AreaCollapsePlan.Build(rows, wholeContract);
        if (plan.Groups.Count == 0)
        {
            tx.Commit();
            return new AreaTransformResult(true, 0, 0, plan.Excluded, plan.NoArea, null, revision);
        }

        var target = wholeContract ? FindOrCreateAreaSystem(conn, tx, contractId) : 0;
        var summaries = ExistingAreaSummaries(conn, tx, contractId);
        var groupsChanged = 0;
        var rowsChanged = 0;
        foreach (var group in plan.Groups)
        {
            var destination = wholeContract ? target : group.OriginalSystemId!.Value;
            var key = (destination, group.Key);
            if (!summaries.TryGetValue(key, out var summary))
            {
                var newSummary = InsertAreaSummary(conn, tx, destination,
                    group.Title, group.Key,
                    AreaCollapsePlan.PricePerSquareMeter(group.Money, group.Area),
                    group.Purchase == 0m ? null :
                        AreaCollapsePlan.PricePerSquareMeter(group.Purchase, group.Area));
                summaries.Add(key, newSummary);
                summary = newSummary;
            }
            else if (AreaReferenced(conn, tx, summary.Id))
            {
                return AreaTransformResult.Fail(
                    "Сводная строка м² уже связана с актом или выдачей. " +
                    "Повторное сворачивание отменено.", revision);
            }

            foreach (var child in group.Rows)
            {
                // Use the exact server-side copy of extra_json (not a UI string).
                var original = rows.First(x => x.Id == child.Id);
                var extras = AreaCollapsePlan.CollapsedMetadata(
                    original.ExtraJson, child.SystemId, child.Order);
                using var move = new SqlCommand(
                    "UPDATE dbo.positions SET system_id=@system,parent_id=@parent," +
                    "extra_json=@extra WHERE id=@id AND parent_id IS NULL", conn, tx);
                move.Parameters.Add("@system", SqlDbType.BigInt).Value = destination;
                move.Parameters.Add("@parent", SqlDbType.BigInt).Value = summary.Id;
                move.Parameters.Add("@extra", SqlDbType.NVarChar, -1).Value = extras;
                move.Parameters.Add("@id", SqlDbType.BigInt).Value = child.Id;
                if (move.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Состав исходных позиций изменился.");
                rowsChanged++;
            }

            // Rebuild amounts from every original source row after the move.
            // Incremental weighted totals can compound two-decimal rounding
            // errors across repeated collapses and edits.
            var merged = ReadAreaSummaryTotals(conn, tx, summary.Id);
            using (var update = new SqlCommand(
                "UPDATE dbo.positions SET qty=@qty,price=@price,purchase_price=@purchase " +
                "WHERE id=@id", conn, tx))
            {
                update.Parameters.Add("@qty", SqlDbType.Float).Value = (double)merged.Qty;
                update.Parameters.Add("@price", SqlDbType.Float).Value = (double)merged.Price;
                update.Parameters.Add("@purchase", SqlDbType.Float).Value =
                    merged.PurchasePrice is decimal p ? (object)(double)p : DBNull.Value;
                update.Parameters.Add("@id", SqlDbType.BigInt).Value = summary.Id;
                if (update.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Сводная строка м² исчезла.");
            }
            summaries[key] = summary with
            {
                Qty = merged.Qty, Price = merged.Price, Purchase = merged.PurchasePrice
            };
            groupsChanged++;
        }

        using (var mode = new SqlCommand(
            "UPDATE dbo.contracts SET m2_mode=@mode WHERE id=@id", conn, tx))
        {
            mode.Parameters.Add("@mode", SqlDbType.NVarChar, 8).Value = wholeContract ? "all" : "sys";
            mode.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            if (mode.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Исходный договор исчез.");
        }
        AreaLog(conn, tx, contractId, objectId,
            $"{title}: свёрнуто в м² — {rowsChanged} строк в {groupsChanged} групп" +
            (wholeContract ? " (на всё КП)" : " (по системам)"));
        var next = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new AreaTransformResult(true, rowsChanged, groupsChanged,
            plan.Excluded, plan.NoArea, null, next);
    }

    public static AreaTransformResult ExpandArea(string connectionString,
        long contractId, long? onlySummaryId, string expectedRevision)
    {
        if (contractId <= 0 || onlySummaryId is <= 0)
            return AreaTransformResult.Fail("Недопустимая сводная строка м².", expectedRevision);
        if (!AreaPilotAllowed(connectionString))
            return AreaTransformResult.Fail(
                "Разворачивание м² разрешено только в тестовой базе GmcSmetaPilot_*.",
                expectedRevision);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var revision = LockRevision(conn, tx);
        if (revision is null) return AreaTransformResult.Fail("Нет meta.rev.", expectedRevision);
        if (!string.Equals(revision, expectedRevision, StringComparison.Ordinal))
            return AreaTransformResult.Fail("Смета изменена другим пользователем. Обнови её.", revision);

        var (objectId, title, reject) = AreaGuard(conn, tx, contractId);
        if (reject is not null) return AreaTransformResult.Fail(reject, revision);

        var allSystems = new HashSet<long>();
        using (var systems = new SqlCommand(
            "SELECT id FROM dbo.systems WITH (HOLDLOCK) WHERE contract_id=@contract", conn, tx))
        {
            systems.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var r = systems.ExecuteReader();
            while (r.Read())
                allSystems.Add(SqlInteger.ReadInt64(r, 0));
        }

        var summaries = new List<(long Id, long SystemId, string Key)>();
        using (var read = new SqlCommand(
            "SELECT p.id,p.system_id,p.extra_json " +
            "FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@contract AND p.parent_id IS NULL", conn, tx))
        {
            read.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var id = SqlInteger.ReadInt64(r, 0);
                if (onlySummaryId is not null && onlySummaryId != id) continue;
                var key = AreaCollapsePlan.ExistingKey(r.IsDBNull(2) ? "" : r.GetString(2));
                if (key.Length > 0)
                    summaries.Add((id,
                        SqlInteger.ReadInt64(r, 1), key));
            }
        }
        if (onlySummaryId is not null && summaries.Count == 0)
            return AreaTransformResult.Fail("Выбранная строка не является сводной м².", revision);
        if (summaries.Count == 0)
        {
            tx.Commit();
            return new AreaTransformResult(true, 0, 0, 0, 0, null, revision);
        }

        var originSystems = new HashSet<long>();
        var restored = 0;
        foreach (var summary in summaries)
        {
            if (AreaReferenced(conn, tx, summary.Id))
                return AreaTransformResult.Fail(
                    "Сводная строка м² связана с актом, закупкой или выдачей.", revision);
            var children = new List<(long Id, string Json, long Home, int Order)>();
            using (var query = new SqlCommand(
                "SELECT id,extra_json FROM dbo.positions WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE parent_id=@summary ORDER BY [order],id", conn, tx))
            {
                query.Parameters.Add("@summary", SqlDbType.BigInt).Value = summary.Id;
                using var r = query.ExecuteReader();
                while (r.Read())
                {
                    var id = SqlInteger.ReadInt64(r, 0);
                    var json = r.IsDBNull(1) ? "" : r.GetString(1);
                    var home = AreaCollapsePlan.OriginalLocation(json);
                    if (home is null || !allSystems.Contains(home.Value.System))
                        return AreaTransformResult.Fail(
                            "Не найден исходный раздел или порядок позиции. Разворачивание отменено.", revision);
                    children.Add((id, json, home.Value.System, home.Value.Order));
                }
            }
            foreach (var child in children)
            {
                if (AreaReferenced(conn, tx, child.Id))
                    return AreaTransformResult.Fail(
                        "Комплектующая сводной м² связана с актом/закупкой/выдачей.", revision);
                // Reversible collapse is defined only for original root rows.
                using (var descendants = new SqlCommand(
                    "SELECT TOP(1) id FROM dbo.positions WHERE parent_id=@id", conn, tx))
                {
                    descendants.Parameters.Add("@id", SqlDbType.BigInt).Value = child.Id;
                    if (descendants.ExecuteScalar() is not null)
                        return AreaTransformResult.Fail(
                            "У вложенной строки появились собственные комплектующие.", revision);
                }
                using var restore = new SqlCommand(
                    "UPDATE dbo.positions SET system_id=@home,parent_id=NULL,[order]=@order," +
                    "extra_json=@extra WHERE id=@id AND parent_id=@summary", conn, tx);
                restore.Parameters.Add("@home", SqlDbType.BigInt).Value = child.Home;
                restore.Parameters.Add("@order", SqlDbType.Int).Value = child.Order;
                restore.Parameters.Add("@extra", SqlDbType.NVarChar, -1).Value =
                    AreaCollapsePlan.RestoredMetadata(child.Json);
                restore.Parameters.Add("@id", SqlDbType.BigInt).Value = child.Id;
                restore.Parameters.Add("@summary", SqlDbType.BigInt).Value = summary.Id;
                if (restore.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Комплектующая исчезла во время разворачивания.");
                originSystems.Add(child.Home);
                restored++;
            }
            using (var delete = new SqlCommand(
                "DELETE FROM dbo.positions WHERE id=@id AND parent_id IS NULL", conn, tx))
            {
                delete.Parameters.Add("@id", SqlDbType.BigInt).Value = summary.Id;
                if (delete.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Сводная строка была изменена.");
            }
            originSystems.Add(summary.SystemId);
        }

        // Original m2_order was captured before collapse. On restoration it
        // sorts naturally alongside the untouched positions. A set-based
        // rank update avoids 2500 individual roundtrips.
        foreach (var system in originSystems)
        {
            using var renumber = new SqlCommand(
                "WITH ord AS (SELECT id,ROW_NUMBER() OVER (ORDER BY [order],id)-1 AS rn " +
                "FROM dbo.positions WHERE system_id=@system) " +
                "UPDATE p SET [order]=CONVERT(INT,ord.rn) " +
                "FROM dbo.positions p JOIN ord ON ord.id=p.id", conn, tx);
            renumber.Parameters.Add("@system", SqlDbType.BigInt).Value = system;
            renumber.ExecuteNonQuery();
        }

        // Remove the synthetic whole-KP area system only if truly unused.
        using (var cleanup = new SqlCommand(
            "DELETE FROM dbo.systems WHERE contract_id=@contract AND name=@name " +
            "AND NOT EXISTS (SELECT 1 FROM dbo.positions WHERE system_id=dbo.systems.id) " +
            "AND NOT EXISTS (SELECT 1 FROM dbo.systems child WHERE child.parent_id=dbo.systems.id)",
            conn, tx))
        {
            cleanup.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            cleanup.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = Area.AllSystems;
            cleanup.ExecuteNonQuery();
        }

        if (onlySummaryId is null)
            using (var mode = new SqlCommand(
                "UPDATE dbo.contracts SET m2_mode=N'' WHERE id=@id", conn, tx))
            {
                mode.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
                mode.ExecuteNonQuery();
            }

        AreaLog(conn, tx, contractId, objectId,
            $"{title}: м² развёрнуто обратно — {restored} строк");
        var next = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new AreaTransformResult(true, restored, summaries.Count, 0, 0, null, next);
    }

    private sealed record AreaSummaryRow(long Id, decimal Qty,
        decimal Price, decimal? Purchase);
    private static bool AreaPilotAllowed(string connectionString)
    {
        try
        {
            return new SqlConnectionStringBuilder(connectionString).InitialCatalog
                .StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private static (long ObjectId, string Name, string? Problem) AreaGuard(
        SqlConnection conn, SqlTransaction tx, long contractId)
    {
        long objectId;
        string name;
        using (var contract = new SqlCommand(
            "SELECT object_id,name,kind,locked,stage_of FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE id=@id", conn, tx))
        {
            contract.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = contract.ExecuteReader();
            if (!r.Read()) return (0, "", "Документ уже удалён.");
            objectId = SqlInteger.ReadInt64(r, 0);
            name = r.IsDBNull(1) ? "" : r.GetString(1);
            if (!r.IsDBNull(3) && SqlFlag.Read(r, 3))
                return (objectId, name, "Подписанный договор менять нельзя.");
            if (!r.IsDBNull(4))
                return (objectId, name,
                    "Сворачивание/разворачивание пока доступно только для полного КП, а не для этапов.");
            if (r.IsDBNull(2) || !string.Equals(r.GetString(2), "kp", StringComparison.OrdinalIgnoreCase))
                return (objectId, name, "Операция пока доступна только для полного КП.");
        }
        using (var stages = new SqlCommand(
            "SELECT TOP(1) id FROM dbo.contracts WITH (HOLDLOCK) WHERE stage_of=@full", conn, tx))
        {
            stages.Parameters.Add("@full", SqlDbType.BigInt).Value = contractId;
            if (stages.ExecuteScalar() is not null)
                return (objectId, name,
                    "У КП есть этапы или документ «Остаток». Сначала разберись с распределением.");
        }
        return (objectId, name, null);
    }

    private static List<AreaCollapsePlan.Position> ReadCollapsePositions(
        SqlConnection conn, SqlTransaction tx, long contractId)
    {
        var rows = new List<AreaCollapsePlan.Position>();
        using var cmd = new SqlCommand(
            @"SELECT p.id,p.system_id,sy.name,p.name,p.unit,p.qty,p.price,p.purchase_price,
                 p.area_manual,p.[order],p.extra_json,p.out_of_kp,
                 CASE WHEN EXISTS (SELECT 1 FROM dbo.positions child WHERE child.parent_id=p.id)
                      THEN 1 ELSE 0 END AS has_child,
                 CASE WHEN EXISTS (SELECT 1 FROM dbo.movements m WHERE m.position_id=p.id)
                        OR EXISTS (SELECT 1 FROM dbo.shipment_lines sl WHERE sl.position_id=p.id)
                        OR EXISTS (SELECT 1 FROM dbo.act_lines al WHERE al.position_id=p.id)
                        OR EXISTS (SELECT 1 FROM dbo.procurement pr WHERE pr.position_id=p.id)
                      THEN 1 ELSE 0 END AS referenced
              FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK)
              JOIN dbo.systems sy ON sy.id=p.system_id
              WHERE sy.contract_id=@contract AND p.parent_id IS NULL
              ORDER BY sy.[order],sy.id,p.[order],p.id", conn, tx);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = SqlInteger.ReadInt64(r, 0);
            var sid = SqlInteger.ReadInt64(r, 1);
            decimal Dec(int i) => r.IsDBNull(i) ? 0m :
                Convert.ToDecimal(r.GetValue(i), CultureInfo.InvariantCulture);
            string Str(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
            rows.Add(new AreaCollapsePlan.Position(
                id, sid, Str(2), Str(3), Str(4), Dec(5), Dec(6),
                r.IsDBNull(7) ? null : Dec(7),
                r.IsDBNull(8) ? null :
                    Convert.ToDouble(r.GetValue(8), CultureInfo.InvariantCulture),
                SqlInteger.ReadInt32(r, 9), Str(10),
                !r.IsDBNull(11) && SqlFlag.Read(r, 11),
                SqlFlag.Read(r, 12),
                SqlFlag.Read(r, 13)));
        }
        return rows;
    }

    private static long FindOrCreateAreaSystem(SqlConnection conn, SqlTransaction tx,
        long contractId)
    {
        using (var find = new SqlCommand(
            "SELECT TOP(1) id FROM dbo.systems WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE contract_id=@contract AND name=@name ORDER BY id", conn, tx))
        {
            find.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            find.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = Area.AllSystems;
            var id = find.ExecuteScalar();
            if (id is not null && id is not DBNull)
                return Convert.ToInt64(id, CultureInfo.InvariantCulture);
        }
        using var insert = new SqlCommand(
            "INSERT INTO dbo.systems (contract_id,name,[order],parent_id,smr_amount,heading,source_num,stage_sys_src) " +
            "OUTPUT INSERTED.id VALUES (@contract,@name," +
            "(SELECT ISNULL(MAX([order]),-1)+1 FROM dbo.systems WHERE contract_id=@contract)," +
            "NULL,NULL,N'',N'',NULL)", conn, tx);
        insert.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = Area.AllSystems;
        return Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static Dictionary<(long SystemId, string Key), AreaSummaryRow> ExistingAreaSummaries(
        SqlConnection conn, SqlTransaction tx, long contractId)
    {
        var result = new Dictionary<(long, string), AreaSummaryRow>();
        using var cmd = new SqlCommand(
            "SELECT p.id,p.system_id,p.extra_json,p.qty,p.price,p.purchase_price FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@contract AND p.parent_id IS NULL", conn, tx);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = AreaCollapsePlan.ExistingKey(r.IsDBNull(2) ? "" : r.GetString(2));
            if (key.Length == 0) continue;
            var id = SqlInteger.ReadInt64(r, 0);
            var systemId = SqlInteger.ReadInt64(r, 1);
            var qty = r.IsDBNull(3) ? 0m :
                Convert.ToDecimal(r.GetValue(3), CultureInfo.InvariantCulture);
            var price = r.IsDBNull(4) ? 0m :
                Convert.ToDecimal(r.GetValue(4), CultureInfo.InvariantCulture);
            var purchase = r.IsDBNull(5) ? (decimal?)null :
                Convert.ToDecimal(r.GetValue(5), CultureInfo.InvariantCulture);
            if (!result.TryAdd((systemId, key), new AreaSummaryRow(id, qty, price, purchase)))
                throw new InvalidOperationException("Повторяющиеся сводные строки м² с одинаковым материалом.");
        }
        return result;
    }

    private static AreaSummaryRow InsertAreaSummary(SqlConnection conn, SqlTransaction tx,
        long systemId, string title, string key, decimal price, decimal? purchase)
    {
        using var insert = new SqlCommand(
            @"INSERT INTO dbo.positions
             (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,
              purchase_price,note,extra_json,source_row,[order],out_of_kp,formulas,
              stage_src,area_manual,work_price,cost_note,cost_color,work_sell,work_cost)
              OUTPUT INSERTED.id
              VALUES (@system,NULL,NULL,N'',N'',@name,N'м2',0,@price,
              @purchase,N'свёрнуто: строки внутри (▸)',@extra,NULL,
              (SELECT ISNULL(MAX([order]),-1)+1 FROM dbo.positions WHERE system_id=@system),
              0,N'',NULL,NULL,NULL,N'',N'',NULL,NULL)", conn, tx);
        insert.Parameters.Add("@system", SqlDbType.BigInt).Value = systemId;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, -1).Value = title;
        insert.Parameters.Add("@price", SqlDbType.Float).Value = (double)price;
        insert.Parameters.Add("@purchase", SqlDbType.Float).Value =
            purchase is decimal p ? (object)(double)p : DBNull.Value;
        insert.Parameters.Add("@extra", SqlDbType.NVarChar, -1).Value =
            JsonSerializer.Serialize(new Dictionary<string, string> { ["m2"] = key });
        var id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new AreaSummaryRow(id, 0m, price, purchase);
    }

    private static AreaCollapsePlan.SummaryValues ReadAreaSummaryTotals(
        SqlConnection conn, SqlTransaction tx, long summaryId)
    {
        var children = new List<AreaCollapsePlan.SummaryComponent>();
        using var cmd = new SqlCommand(
            "SELECT name,unit,qty,price,purchase_price,area_manual " +
            "FROM dbo.positions WITH (UPDLOCK,HOLDLOCK) WHERE parent_id=@summary " +
            "ORDER BY [order],id", conn, tx);
        cmd.Parameters.Add("@summary", SqlDbType.BigInt).Value = summaryId;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string Str(int i) => reader.IsDBNull(i) ? "" : reader.GetString(i);
            decimal Num(int i) => reader.IsDBNull(i) ? 0m :
                Convert.ToDecimal(reader.GetValue(i), CultureInfo.InvariantCulture);
            children.Add(new AreaCollapsePlan.SummaryComponent(
                Str(0), Str(1), Num(2), Num(3),
                reader.IsDBNull(4) ? null : Num(4),
                reader.IsDBNull(5) ? null :
                    Convert.ToDouble(reader.GetValue(5), CultureInfo.InvariantCulture)));
        }
        return AreaCollapsePlan.SumSourceRows(children);
    }

    private static bool AreaReferenced(SqlConnection conn, SqlTransaction tx, long id)
    {
        using var cmd = new SqlCommand(
            @"SELECT CASE WHEN
                EXISTS (SELECT 1 FROM dbo.act_lines WHERE position_id=@id) OR
                EXISTS (SELECT 1 FROM dbo.shipment_lines WHERE position_id=@id) OR
                EXISTS (SELECT 1 FROM dbo.movements WHERE position_id=@id) OR
                EXISTS (SELECT 1 FROM dbo.procurement WHERE position_id=@id)
                THEN 1 ELSE 0 END", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void AreaLog(SqlConnection conn, SqlTransaction tx, long contractId,
        long objectId, string details)
    {
        using var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@time,@user,N'contract',@id,N'update',@details,@contract,@object)", conn, tx);
        log.Parameters.Add("@time", SqlDbType.DateTime2).Value = DateTime.Now;
        log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
        log.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
        log.ExecuteNonQuery();
    }
}
