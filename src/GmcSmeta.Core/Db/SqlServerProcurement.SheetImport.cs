using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Fail-closed equipment-control import. Unlike legacy ImportPlan it never matches
/// by name alone, overwrites manual values, or creates movements/estimate positions.
/// </summary>
public static partial class SqlServerProcurement
{
    public sealed record SheetRow(
        long? PositionId, string System, string Name, string Unit, decimal Quantity,
        string Supplier, string Stage, string OrderRef, string InvoiceRef,
        decimal? CostPrice, DateTime? DueDate, string Replacement, string Note);

    public sealed record SheetCandidate(
        long Id, string System, string Name, string Unit, decimal Quantity, bool Linked,
        long? SupplierId, string Supplier, string Stage, string OrderRef, string InvoiceRef,
        decimal? CostPrice, DateTime? DueDate, string Replacement, string Note);

    public sealed record SheetDecision(int RowNumber, long? PositionId, string State, string Reason);
    public sealed record SheetPreview(string Revision, IReadOnlyList<SheetDecision> Decisions)
    {
        public int Ready => Decisions.Count(x => x.State == "READY");
        public int Blocked => Decisions.Count(x => x.State == "BLOCKED");
        public int Unchanged => Decisions.Count(x => x.State == "UNCHANGED");
    }

    private static string SheetNorm(string? value) =>
        string.Join(' ', (value ?? "").Replace('ё', 'е').Replace('Ё', 'Е')
            .Replace('\u00a0', ' ').Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static (string System, string Name, string Unit, decimal Qty) SheetKey(
        string system, string name, string unit, decimal qty) =>
        (SheetNorm(system), SheetNorm(name), SheetNorm(unit).TrimEnd('.'), qty);

    // Keep exact existing spelling, whitespace and case when normalized values agree.
    public static string FillSheetText(string existing, string proposed) =>
        string.IsNullOrWhiteSpace(existing) ? proposed.Trim() : existing;

    private static bool SheetConflicts(string proposed, string existing) =>
        !string.IsNullOrWhiteSpace(proposed) && !string.IsNullOrWhiteSpace(existing) &&
        SheetNorm(proposed) != SheetNorm(existing);

    /// <summary>
    /// Explicit position ID resolves collisions. Otherwise system, name, unit and
    /// quantity must point to EXACTLY one position. Duplicated input is blocked.
    /// Existing non-empty manual values cannot be replaced.
    /// </summary>
    public static IReadOnlyList<SheetDecision> PlanSheet(
        IReadOnlyList<SheetRow> rows, IReadOnlyList<SheetCandidate> candidates, bool contractLocked)
    {
        var byId = candidates.ToDictionary(x => x.Id);
        var byKey = candidates.GroupBy(x => SheetKey(x.System, x.Name, x.Unit, x.Quantity))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var result = new List<SheetDecision>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            SheetDecision Block(string reason, long? id = null) => new(i + 2, id, "BLOCKED", reason);
            if (contractLocked) { result.Add(Block("Договор заблокирован.")); continue; }
            if (string.IsNullOrWhiteSpace(row.System) || string.IsNullOrWhiteSpace(row.Name) ||
                string.IsNullOrWhiteSpace(row.Unit) || row.Quantity < 0m ||
                decimal.Round(row.Quantity, 6) != row.Quantity)
            {
                result.Add(Block("Нужны система, наименование, единица и корректное количество."));
                continue;
            }
            if (row.CostPrice is < 0m or > 1_000_000_000m ||
                row.CostPrice is decimal cp && decimal.Round(cp, 2) != cp)
            {
                result.Add(Block("Цена закупки должна быть неотрицательной, с точностью до копейки."));
                continue;
            }
            if (row.Supplier.Length > 255 || row.OrderRef.Length > 255 ||
                row.InvoiceRef.Length > 255 || row.Replacement.Length > 1000 ||
                row.Note.Length > 4000)
            {
                result.Add(Block("Слишком длинное значение в импортируемой строке."));
                continue;
            }
            var key = SheetKey(row.System, row.Name, row.Unit, row.Quantity);
            SheetCandidate? candidate = null;
            if (row.PositionId is long explicitId)
            {
                if (!byId.TryGetValue(explicitId, out candidate) ||
                    SheetKey(candidate.System, candidate.Name, candidate.Unit, candidate.Quantity) != key)
                {
                    result.Add(Block("ID не найден в договоре или реквизиты позиции отличаются.", explicitId));
                    continue;
                }
            }
            else if (!byKey.TryGetValue(key, out var matches) || matches.Length == 0)
            {
                result.Add(Block("Строка не найдена; автоматическое добавление вне КП запрещено."));
                continue;
            }
            else if (matches.Length != 1)
            {
                result.Add(Block("Неоднозначное совпадение; укажите ID позиции."));
                continue;
            }
            else candidate = matches[0];

            var existing = candidate!;
            if (EstimateStatistics.IsWorkName(existing.Name))
            {
                result.Add(Block("Работы не относятся к закупаемым материалам.", existing.Id));
                continue;
            }
            if (existing.Linked)
            {
                result.Add(Block("Есть КС, движение или ведомость; импорт в эту строку запрещён.", existing.Id));
                continue;
            }
            string stage;
            try { stage = string.IsNullOrWhiteSpace(row.Stage) ? "" : NormalizeStage(row.Stage); }
            catch (ArgumentException)
            {
                result.Add(Block("Неизвестный закупочный статус.", existing.Id));
                continue;
            }
            string? conflict = null;
            if (SheetConflicts(row.Supplier, existing.Supplier)) conflict = "Поставщик уже задан вручную.";
            else if (SheetConflicts(stage, existing.Stage)) conflict = "Статус уже задан.";
            else if (SheetConflicts(row.OrderRef, existing.OrderRef)) conflict = "Номер заявки уже задан.";
            else if (SheetConflicts(row.InvoiceRef, existing.InvoiceRef)) conflict = "Номер счёта уже задан.";
            else if (row.CostPrice is decimal cost && existing.CostPrice is decimal old &&
                cost != old) conflict = "Закупочная цена уже задана; перезапись запрещена.";
            else if (row.DueDate is DateTime due && existing.DueDate is DateTime oldDue &&
                due.Date != oldDue.Date) conflict = "Срок поставки уже задан.";
            else if (SheetConflicts(row.Replacement, existing.Replacement)) conflict = "Замена уже задана.";
            else if (SheetConflicts(row.Note, existing.Note)) conflict = "Примечание уже задано.";
            if (conflict is not null)
            {
                result.Add(Block(conflict, existing.Id));
                continue;
            }
            var changes = (row.Supplier.Trim().Length > 0 && existing.Supplier.Trim().Length == 0) ||
                (stage.Length > 0 && existing.Stage.Length == 0) ||
                (row.OrderRef.Trim().Length > 0 && existing.OrderRef.Trim().Length == 0) ||
                (row.InvoiceRef.Trim().Length > 0 && existing.InvoiceRef.Trim().Length == 0) ||
                (row.CostPrice is not null && existing.CostPrice is null) ||
                (row.DueDate is not null && existing.DueDate is null) ||
                (row.Replacement.Trim().Length > 0 && existing.Replacement.Trim().Length == 0) ||
                (row.Note.Trim().Length > 0 && existing.Note.Trim().Length == 0);
            result.Add(new SheetDecision(i + 2, existing.Id,
                changes ? "READY" : "UNCHANGED", changes ? "Можно заполнить пустые поля." : "Данных для изменения нет."));
        }
        var duplicateIds = result.Where(x => x.PositionId.HasValue)
            .GroupBy(x => x.PositionId!.Value)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return result.Select(x => x.PositionId is long id && duplicateIds.Contains(id)
            ? x with { State = "BLOCKED", Reason = "Дублирование позиции в импортируемом листе." }
            : x).ToArray();
    }

    private static (bool Locked, List<SheetCandidate> Candidates) SheetSnapshot(
        SqlConnection conn, SqlTransaction? tx, int contractId)
    {
        bool locked;
        using (var contract = new SqlCommand(
            "SELECT c.locked FROM dbo.contracts c" + (tx is null ? "" : " WITH (UPDLOCK,HOLDLOCK)") +
            " JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id " +
            "AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0", conn, tx))
        {
            contract.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            var flag = contract.ExecuteScalar();
            if (flag is null || flag is DBNull)
                throw new InvalidOperationException("Договор не найден.");
            locked = Convert.ToBoolean(flag, CultureInfo.InvariantCulture);
        }
        var result = new List<SheetCandidate>();
        using (var cmd = new SqlCommand(
            "SELECT p.id,sy.name,p.name,p.unit,p.qty,pr.supplier_id,ISNULL(sup.name,N'')," +
            "ISNULL(pr.stage,N''),ISNULL(pr.order_ref,N''),ISNULL(pr.invoice_ref,N'')," +
            "COALESCE(pr.cost_price,p.purchase_price),pr.due_date,ISNULL(pr.replacement,N''),ISNULL(pr.note,N'')," +
            "CASE WHEN EXISTS(SELECT 1 FROM dbo.act_lines al WHERE al.position_id=p.id) " +
            "OR EXISTS(SELECT 1 FROM dbo.movements m WHERE m.position_id=p.id) " +
            "OR EXISTS(SELECT 1 FROM dbo.shipment_lines sl WHERE sl.position_id=p.id) " +
            "THEN 1 ELSE 0 END AS linked " +
            "FROM dbo.positions p" + (tx is null ? "" : " WITH (UPDLOCK,HOLDLOCK)") +
            " JOIN dbo.systems sy ON sy.id=p.system_id " +
            "LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " +
            "LEFT JOIN dbo.suppliers sup ON sup.id=pr.supplier_id " +
            "WHERE sy.contract_id=@contract ORDER BY sy.[order],sy.id,p.[order],p.id", conn, tx))
        {
            cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new SheetCandidate(
                    SqlInteger.ReadInt64(r, 0),
                    r.GetString(1), r.GetString(2), r.GetString(3),
                    decimal.Round(Convert.ToDecimal(r.GetValue(4), CultureInfo.InvariantCulture), 6),
                    SqlFlag.Read(r, 14),
                    r.IsDBNull(5) ? null : SqlInteger.ReadInt64(r, 5),
                    r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9),
                    r.IsDBNull(10) ? null : Convert.ToDecimal(r.GetValue(10), CultureInfo.InvariantCulture),
                    r.IsDBNull(11) ? null : r.GetDateTime(11),
                    r.GetString(12), r.GetString(13)));
        }
        return (locked, result);
    }

    public static SheetPreview PreviewSheet(string connectionString, int contractId,
        IReadOnlyList<SheetRow> rows)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        var revision = SqlServerDatabase.Revision(conn);
        var snapshot = SheetSnapshot(conn, null, contractId);
        if (string.IsNullOrEmpty(revision) || SqlServerDatabase.Revision(conn) != revision)
            throw new InvalidOperationException("Данные изменились при чтении контрольной ведомости. Повторите предпросмотр.");
        return new SheetPreview(revision, PlanSheet(rows, snapshot.Candidates, snapshot.Locked));
    }

    /// <summary>Serializable revalidation; no blind retry after an ambiguous COMMIT.</summary>
    public static SqlServerDatabase.SaveResult ApplySheet(string connectionString, int contractId,
        IReadOnlyList<SheetRow> rows, string expectedRevision)
    {
        if (rows.Count is 0 or > 2500)
            return SqlServerDatabase.SaveResult.Fail("Допускается 1–2500 строк за одну операцию.", expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        if (!conn.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Фактическая SQL база не является изолированной пилотной.");
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null || !string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Ревизия SQL изменилась. Повторите preview.", rev ?? "");
        var snapshot = SheetSnapshot(conn, tx, contractId);
        var decisions = PlanSheet(rows, snapshot.Candidates, snapshot.Locked);
        var blocked = decisions.Where(x => x.State == "BLOCKED").Take(3).ToArray();
        if (blocked.Length > 0)
            return SqlServerDatabase.SaveResult.Fail(
                "Импорт отменён целиком: " +
                string.Join("; ", blocked.Select(x => $"строка {x.RowNumber}: {x.Reason}")), rev);
        var byId = snapshot.Candidates.ToDictionary(x => x.Id);
        foreach (var decision in decisions.Where(x => x.State == "READY"))
        {
            var row = rows[decision.RowNumber - 2];
            var current = byId[decision.PositionId!.Value];
            var supplierId = current.SupplierId;
            if (string.IsNullOrWhiteSpace(current.Supplier) && !string.IsNullOrWhiteSpace(row.Supplier))
            {
                using var supplier = new SqlCommand(
                    "SELECT id FROM dbo.suppliers WITH (UPDLOCK,HOLDLOCK) WHERE name=@name", conn, tx);
                supplier.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = row.Supplier.Trim();
                var found = supplier.ExecuteScalar();
                if (found is not null and not DBNull)
                    supplierId = Convert.ToInt64(found, CultureInfo.InvariantCulture);
                else
                {
                    using var add = new SqlCommand(
                        "INSERT dbo.suppliers(name) OUTPUT INSERTED.id VALUES(@name)", conn, tx);
                    add.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = row.Supplier.Trim();
                    supplierId = Convert.ToInt64(add.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
            }
            var stage = FillSheetText(current.Stage,
                string.IsNullOrWhiteSpace(row.Stage) ? "" : NormalizeStage(row.Stage));
            var source = stage is "shop_queued" or "shop_work" ||
                (!string.IsNullOrWhiteSpace(row.Supplier) && row.Supplier.Trim().StartsWith("цех", StringComparison.OrdinalIgnoreCase))
                ? "shop" : "buy";
            var sql = @"IF EXISTS(SELECT 1 FROM dbo.procurement WITH (UPDLOCK,HOLDLOCK) WHERE position_id=@id)
                UPDATE dbo.procurement SET supplier_id=@supplier,stage=@stage,order_ref=@order,
                    invoice_ref=@invoice,cost_price=@cost,due_date=@due,replacement=@replacement,
                    note=@note,source=CASE WHEN @source=N'shop' THEN N'shop' ELSE source END,
                    updated_at=SYSDATETIME(),updated_by=@user WHERE position_id=@id
                ELSE
                INSERT dbo.procurement(position_id,supplier_id,stage,order_ref,invoice_ref,cost_price,
                    due_date,replacement,note,direct_to_site,source,updated_at,updated_by)
                VALUES(@id,@supplier,@stage,@order,@invoice,@cost,@due,@replacement,@note,0,
                    @source,SYSDATETIME(),@user)";
            using (var update = new SqlCommand(sql, conn, tx))
            {
                update.Parameters.Add("@id", SqlDbType.BigInt).Value = current.Id;
                update.Parameters.Add("@supplier", SqlDbType.BigInt).Value = supplierId is null ? DBNull.Value : supplierId.Value;
                update.Parameters.Add("@stage", SqlDbType.NVarChar, 40).Value = stage;
                update.Parameters.Add("@order", SqlDbType.NVarChar, 255).Value = FillSheetText(current.OrderRef, row.OrderRef);
                update.Parameters.Add("@invoice", SqlDbType.NVarChar, 255).Value = FillSheetText(current.InvoiceRef, row.InvoiceRef);
                update.Parameters.Add("@cost", SqlDbType.Float).Value =
                    (current.CostPrice ?? row.CostPrice) is decimal price ? (object)(double)price : DBNull.Value;
                update.Parameters.Add("@due", SqlDbType.DateTime2).Value = (current.DueDate ?? row.DueDate) is DateTime value ? (object)value.Date : DBNull.Value;
                update.Parameters.Add("@replacement", SqlDbType.NVarChar, -1).Value = FillSheetText(current.Replacement, row.Replacement);
                update.Parameters.Add("@note", SqlDbType.NVarChar, -1).Value = FillSheetText(current.Note, row.Note);
                update.Parameters.Add("@source", SqlDbType.NVarChar, 32).Value = source;
                update.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
                if (update.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Не удалось заполнить закупочную строку; импорт отменён.");
            }
            using var log = new SqlCommand(
                "INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id) " +
                "VALUES(SYSDATETIME(),@user,N'procurement',@id,N'update',@details,@contract)", conn, tx);
            log.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = current.Id;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                "Безопасный импорт контрольной ведомости: " + current.Name;
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.ExecuteNonQuery();
        }
        var changed = decisions.Count(x => x.State == "READY");
        var next = changed == 0 ? rev : SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, next);
    }
}
