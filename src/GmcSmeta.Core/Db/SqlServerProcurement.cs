using System.Data;
using System.Globalization;
using System.Threading;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Закупки (procurement.py): «актуальный» слой строк КП — поставщик, этап
/// (заказано/счёт/оплачено/по факту…), заявка/счёт, закупочная цена, срок,
/// замена, примечание; движения (пришло на склад / выдано / возвраты) и
/// производный статус. Статусы и их цвета — как в STATUS эталона.
/// </summary>
public static partial class SqlServerProcurement
{
    public const decimal Eps = 0.000001m;
    public const string ShopSupplier = "Цех";

    public static readonly (string Code, string Label, string Bg, string Fg)[] StatusTable =
    {
        ("none", "Не заказано", "#FFFFFF", "#B91C1C"),
        ("skip", "Не заказываем", "#F4F4F5", "#71717A"),
        ("zero", "0 по КП", "#F4F4F5", "#A1A1AA"),
        ("fact", "По факту", "#F1F5F9", "#475569"),
        ("ordered", "Заказано", "#EFF6FF", "#1D4ED8"),
        ("shop_queued", "Заявка в цех", "#EEF2FF", "#4338CA"),
        ("shop_work", "В изготовлении", "#FAE8FF", "#A21CAF"),
        ("invoice", "Счёт", "#FEF9C3", "#854D0E"),
        ("paid", "Оплачено", "#F3E8FF", "#7E22CE"),
        ("part_stock", "Пришло частично", "#FFEDD5", "#C2410C"),
        ("stock", "На складе", "#E0F2FE", "#0369A1"),
        ("part_issued", "Выдано частично", "#ECFCCB", "#4D7C0F"),
        ("issued", "Выдано", "#DCFCE7", "#15803D"),
    };

    public static string StatusLabel(string code) =>
        StatusTable.FirstOrDefault(s => s.Code == code).Label;

    /// <summary>Map user-facing Russian stage names in imported Excel to stable SQL codes.</summary>
    public static string NormalizeStage(string? stage)
    {
        var source = (stage ?? "").Trim();
        if (source.Length == 0) return "";
        var normalized = source.ToLowerInvariant().Replace('ё', 'е');
        foreach (var item in StatusTable)
        {
            if (string.Equals(item.Code, source, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Label.Replace('ё', 'е'), normalized, StringComparison.OrdinalIgnoreCase))
                return item.Code;
        }
        return normalized switch
        {
            "счет" => "invoice",
            "по факту / монтажники" or "по факту/монтажники" => "fact",
            "не заказываем / монтажники" => "skip",
            "—" or "-" => "none",
            _ => throw new ArgumentException("Неизвестный этап закупки: " + source),
        };
    }

    private static readonly HashSet<string> Waiting = new()
    { "none", "ordered", "invoice", "paid", "shop_queued", "shop_work", "part_stock" };

    /// <summary>Производный статус строки (procurement.status_of): количества первыми, затем этап.</summary>
    public static string StatusOf(decimal qty, decimal received, decimal issued, string stage)
    {
        if (qty <= Eps && received <= Eps && issued <= Eps)
            return stage == "skip" ? "skip" : "zero";
        if (issued > Eps)
            return issued >= qty - Eps ? "issued" : "part_issued";
        if (received > Eps)
            return received >= qty - Eps ? "stock" : "part_stock";
        return StatusTable.Any(s => s.Code == stage) ? stage : "none";
    }

    /// <summary>Строка «Закупок» — верхняя строка сметы с её закупочным слоем.</summary>
    public sealed record BuyRow(
        long PositionId, string SystemName, string Name, string Unit, decimal Qty, decimal Price,
        string Status, string StatusLabel, string StatusBg, string StatusFg,
        string? Supplier, string Stage, string OrderRef, string InvoiceRef,
        decimal? CostPrice, DateTime? DueDate, string Replacement, string Note,
        bool DirectToSite, decimal Received, decimal Issued, bool Work, bool OutOfKp,
        string[] Flags, string Source = "buy", long SystemId = 0, long? ParentSystemId = null,
        DateTime? LastIssueDate = null, string PurchaseName = "", DateTime? PaidDate = null,
        string Responsible = "", long? ParentPositionId = null);

    private static string[] FlagsOf(decimal qty, decimal received, decimal issued,
        string replacement, DateTime? dueDate, string dueState, bool outOfKp)
    {
        var flags = new List<string>();
        if (received > qty + Eps || issued > qty + Eps)
            flags.Add($"сверх КП: {Math.Max(received, issued) - qty:0.######}");
        if (received > Eps && issued > received + Eps)
            flags.Add("выдано больше, чем пришло");
        if (replacement.Length > 0) flags.Add("замена");
        if (dueState == "late" && dueDate is not null)
            flags.Add($"просрочена поставка (ждали к {dueDate.Value:dd.MM})");
        if (outOfKp) flags.Add("вне КП");
        return flags.ToArray();
    }

    /// <summary>
    /// One contract-scoped aggregation of movements for all buying rows.
    /// Unlike five correlated aggregates per position, the grouped input is read
    /// once and covers only the requested contract. Projection order is unchanged.
    /// </summary>
    public const int MaxIncrementalBuyRows = 256;

    public static string BuyRowsQuery() => BuyRowsQuery(0);

    public static string BuyRowsQuery(int positionCount)
    {
        if (positionCount < 0 || positionCount > MaxIncrementalBuyRows)
            throw new ArgumentOutOfRangeException(nameof(positionCount));
        var ids = string.Join(",", Enumerable.Range(0, positionCount).Select(i => $"@pos{i}"));
        var movementFilter = positionCount == 0 ? "" : " AND m.position_id IN (" + ids + ")";
        var positionFilter = positionCount == 0 ? "" : " AND p.id IN (" + ids + ")";
        return
        "SELECT p.id, sy.name, p.name, p.unit, p.qty, p.price, p.out_of_kp, " +
        "ISNULL(mv.qty_in,0) - ISNULL(mv.qty_back_supplier,0) AS received, " +
        "ISNULL(mv.qty_out,0) - ISNULL(mv.qty_back,0) AS issued, " +
        "ISNULL(pr.stage,N''), ISNULL(pr.order_ref,N''), ISNULL(pr.invoice_ref,N''), pr.cost_price, " +
        "pr.due_date, ISNULL(pr.replacement,N''), ISNULL(pr.note,N''), " +
        "ISNULL(pr.direct_to_site,0), sup.name, ISNULL(pr.source,N'buy'), sy.id, sy.parent_id, " +
        "mv.last_issue_date, ISNULL(pr.buy_name,N''), pr.paid_date, ISNULL(pr.responsible,N''), p.parent_id, ISNULL(pp.name,N'') " +
        "FROM dbo.positions p " +
        "JOIN dbo.systems sy ON sy.id=p.system_id " +
        "LEFT JOIN dbo.positions pp ON pp.id=p.parent_id " +
        "LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " +
        "LEFT JOIN dbo.suppliers sup ON sup.id=pr.supplier_id " +
        "LEFT JOIN (" +
        " SELECT m.position_id, " +
        " SUM(CASE WHEN m.kind=N'in' THEN m.qty ELSE 0 END) AS qty_in, " +
        " SUM(CASE WHEN m.kind=N'back_supplier' THEN m.qty ELSE 0 END) AS qty_back_supplier, " +
        " SUM(CASE WHEN m.kind=N'out' THEN m.qty ELSE 0 END) AS qty_out, " +
        " SUM(CASE WHEN m.kind=N'back' THEN m.qty ELSE 0 END) AS qty_back, " +
        " MAX(CASE WHEN m.kind=N'out' THEN m.[date] END) AS last_issue_date " +
        " FROM dbo.movements m " +
        " JOIN dbo.positions mp ON mp.id=m.position_id " +
        " JOIN dbo.systems ms ON ms.id=mp.system_id " +
        " WHERE ms.contract_id=@id" + movementFilter + " GROUP BY m.position_id" +
        ") mv ON mv.position_id=p.id " +
        "WHERE sy.contract_id=@id" + positionFilter + " ORDER BY sy.[order], sy.id, p.[order], p.id";
    }

    /// <summary>Read a bounded subset at exactly the committed revision. No SQL writes.</summary>
    public static IReadOnlyList<BuyRow>? ReadChangedBuyRows(string connectionString, int contractId,
        IReadOnlyList<long> positionIds, string committedRevision)
    {
        if (string.IsNullOrEmpty(committedRevision) || positionIds.Count == 0 ||
            positionIds.Count > MaxIncrementalBuyRows || positionIds.Any(id => id <= 0) ||
            positionIds.Distinct().Count() != positionIds.Count) return null;
        using var conn = SqlServerDatabase.Open(connectionString);
        if (SqlServerDatabase.Revision(conn) != committedRevision) return null;
        var rows = new List<BuyRow>();
        using (var read = new SqlCommand(BuyRowsQuery(positionIds.Count), conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            for (var i = 0; i < positionIds.Count; i++)
                read.Parameters.Add($"@pos{i}", SqlDbType.BigInt).Value = positionIds[i];
            using var reader = read.ExecuteReader();
            var today = DateTime.Today;
            while (reader.Read()) rows.Add(MapBuyRow(reader, today));
        }
        if (SqlServerDatabase.Revision(conn) != committedRevision ||
            rows.Count != positionIds.Count) return null;
        return rows;
    }

    /// <summary>Строки «Закупок» документа с производными статусами и пометками.</summary>
    public static IReadOnlyList<BuyRow> BuyRows(string connectionString, int contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var read = new SqlCommand(BuyRowsQuery(), conn);
        read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var reader = read.ExecuteReader();
        return ReadBuyRows(reader, DateTime.Today);
    }

    /// <summary>All systems of a contract, including empty parents. Read-only for SystemPicker.</summary>
    public sealed record SystemChoice(long Id, long? ParentId, string Name, int Order);

    private const string SystemChoicesQuery =
        "SELECT id, parent_id, name, [order] FROM dbo.systems " +
        "WHERE contract_id=@id ORDER BY [order],id";

    public static IReadOnlyList<SystemChoice> SystemChoices(string connectionString, int contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(SystemChoicesQuery, conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var reader = cmd.ExecuteReader();
        return ReadSystemChoices(reader);
    }

    internal static IReadOnlyList<BuyRow> ReadBuyRows(IDataReader reader, DateTime today, CancellationToken token = default)
    {
        var rows = new List<BuyRow>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            rows.Add(MapBuyRow(reader, today));
        }
        return rows;
    }

    private static IReadOnlyList<SystemChoice> ReadSystemChoices(IDataReader reader, CancellationToken token = default)
    {
        var systems = new List<SystemChoice>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            systems.Add(new SystemChoice(
                Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null :
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture)));
        }
        return systems;
    }

    public sealed record BuySnapshot(IReadOnlyList<BuyRow> Rows,
        IReadOnlyList<SystemChoice> Systems, string Revision, DateTime Day);

    /// <summary>
    /// Read rows and all systems (including empty parents) in one batch on
    /// one guarded connection. The revision fence spans both result sets.
    /// No snapshot is returned if another writer changed the revision.
    /// </summary>
    public static BuySnapshot ReadBuySnapshot(string connectionString, int contractId) =>
        ReadBuySnapshotIfChanged(connectionString, contractId, "", null)
        ?? throw new InvalidOperationException("Не получен снимок закупок.");

    public static bool CanReuseBuySnapshot(string publishedRevision, string observedRevision,
        DateTime? publishedDay, DateTime today) =>
        !string.IsNullOrWhiteSpace(publishedRevision) &&
        string.Equals(publishedRevision, observedRevision, StringComparison.Ordinal) &&
        publishedDay == today;

    /// <summary>
    /// Null means the caller's published snapshot still matches the server
    /// and today's date. A miss reads the full snapshot on the same connection.
    /// Callers must never pass a write receipt or unconfirmed UI state.
    /// </summary>
    public static BuySnapshot? ReadBuySnapshotIfChanged(string connectionString, int contractId,
        string publishedRevision, DateTime? publishedDay, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(connectionString);
        string Revision() => token.CanBeCanceled
            ? SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Revision(conn);
        var revision = Revision();
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(revision))
            throw new InvalidOperationException("Данные изменились при чтении закупок. Обновите таблицу.");

        var day = DateTime.Today;
        if (CanReuseBuySnapshot(publishedRevision, revision, publishedDay, day))
            return null;

        IReadOnlyList<BuyRow> rows;
        IReadOnlyList<SystemChoice> systems;
        using (var cmd = new SqlCommand(BuyRowsQuery() + ";\n" + SystemChoicesQuery, conn))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var registration = SqlReadCancellation.Register(cmd, token);
            using var reader = SqlReadCancellation.ExecuteReader(cmd, token);
            rows = ReadBuyRows(reader, day, token);
            token.ThrowIfCancellationRequested();
            if (!reader.NextResult())
                throw new InvalidOperationException("Не получен список систем закупок.");
            systems = ReadSystemChoices(reader, token);
        }
        token.ThrowIfCancellationRequested();
        if (Revision() != revision)
            throw new InvalidOperationException("Данные изменились при чтении закупок. Обновите таблицу.");
        token.ThrowIfCancellationRequested();
        return new BuySnapshot(rows, systems, revision, day);
    }

    /// <summary>
    /// Decode the SQL Server BuyRows SELECT. Supplier is column 17,
    /// direct_to_site column 16 and note column 15. Additional read-only
    /// projection columns: source 18, system ID 19, parent system ID 20,
    /// last issue date 21. Older fixtures remain supported.
    /// Legacy 18-column fixtures remain supported for regression tests.
    /// </summary>
    public static BuyRow MapBuyRow(IDataRecord r, DateTime today)
    {
        string Text(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
        decimal? Dec(int i) => r.IsDBNull(i) ? null :
            Money.FromDouble(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
        decimal Qty(int i) => Dec(i) ?? 0m;

        var qty = Qty(4);
        var received = Qty(7);
        var issued = Qty(8);
        var stage = Text(9);
        var replacement = Text(14);
        var dueDate = r.IsDBNull(13) ? (DateTime?)null :
            Convert.ToDateTime(r.GetValue(13), CultureInfo.InvariantCulture);
        var outOfKp = !r.IsDBNull(6) &&
            Convert.ToBoolean(r.GetValue(6), CultureInfo.InvariantCulture);
        var directToSite = !r.IsDBNull(16) &&
            Convert.ToBoolean(r.GetValue(16), CultureInfo.InvariantCulture);
        var supplier = r.IsDBNull(17) ? null : Text(17);
        var name = Text(2);
        var work = EstimateStatistics.IsWorkName(name) || (r.FieldCount > 26 && EstimateStatistics.IsWorkName(Text(26)));
        var status = StatusOf(qty, received, issued, stage);
        var meta = StatusTable.First(s => s.Code == status);
        var dueState = DueStateOf(status, dueDate, today);

        return new BuyRow(
            Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
            Text(1), name, Text(3), qty, Qty(5),
            status, meta.Label, meta.Bg, meta.Fg,
            supplier, stage, Text(10), Text(11), Dec(12), dueDate,
            replacement, Text(15), directToSite,
            received, issued, work, outOfKp,
            FlagsOf(qty, received, issued, replacement, dueDate, dueState, outOfKp),
            r.FieldCount > 18 ? Text(18) : "buy",
            r.FieldCount > 19 ? Convert.ToInt64(r.GetValue(19), CultureInfo.InvariantCulture) : 0,
            r.FieldCount > 20 && !r.IsDBNull(20)
                ? Convert.ToInt64(r.GetValue(20), CultureInfo.InvariantCulture) : null,
            r.FieldCount > 21 && !r.IsDBNull(21)
                ? Convert.ToDateTime(r.GetValue(21), CultureInfo.InvariantCulture) : null,
            r.FieldCount > 22 ? Text(22) : "",
            r.FieldCount > 23 && !r.IsDBNull(23) ? Convert.ToDateTime(r.GetValue(23), CultureInfo.InvariantCulture) : null,
            r.FieldCount > 24 ? Text(24) : "",
            r.FieldCount > 25 && !r.IsDBNull(25) ? Convert.ToInt64(r.GetValue(25), CultureInfo.InvariantCulture) : null);
    }

    /// <summary>
    /// A null supplier argument means "do not change", not "clear".
    /// An explicitly empty supplier name means "clear" (resolved ID = null).
    /// </summary>
    public static long? SupplierIdForUpdate(long? currentId, string? supplierInput, long? resolvedId) =>
        supplierInput is null ? currentId : resolvedId;

    public static decimal? CostPriceForUpdate(decimal? oldValue, decimal? input, bool clear) =>
        clear ? null : input ?? oldValue;

    public static DateTime? DueDateForUpdate(DateTime? oldValue, DateTime? input, bool clear) =>
        clear ? null : input ?? oldValue;

    /// <summary>Состояние срока поставки (procurement.due_state): late/soon/ok/done/none.</summary>
    private static string? DueStateOf(string status, DateTime? dueDate, DateTime today)
    {
        if (dueDate is null) return null;
        if (!Waiting.Contains(status)) return "done";
        var days = (dueDate.Value.Date - today.Date).Days;
        return days < 0 ? "late" : days <= 3 ? "soon" : "ok";
    }

    /// <summary>
    /// Пакетное обновление закупочного слоя строк (procurement.set_proc): поставщик
    /// (создаётся по имени), этап, заявка/счёт, закупочная цена, срок, замена,
    /// примечание, «прямо на объект». Журнал procurement/update по каждой строке.
    /// </summary>
    public static SqlServerDatabase.SaveResult SetProc(string connectionString, int contractId,
        IReadOnlyList<long> positionIds, string? supplier, string? stage, string? orderRef,
        string? invoiceRef, decimal? costPrice, DateTime? dueDate, string? replacement,
        string? note, bool? directToSite, string expectedRevision,
        bool clearCostPrice = false, bool clearDueDate = false,
        DateTime? paidDate = null, bool clearPaidDate = false, string? explicitSource = null,
        string? responsible = null)
    {
        if (explicitSource is not null && explicitSource is not ("shop" or "buy"))
            return SqlServerDatabase.SaveResult.Fail("Неизвестный источник закупки.", expectedRevision);
        RequirePilotWrite(connectionString);
        if (contractId <= 0 || positionIds is null || positionIds.Count == 0 ||
            positionIds.Any(id => id <= 0))
            return SqlServerDatabase.SaveResult.Fail("Не выбраны корректные позиции закупки.", expectedRevision);
        if (positionIds.Count > 2500)
            return SqlServerDatabase.SaveResult.Fail("За раз можно изменить не более 2500 позиций.", expectedRevision);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови закупки.", rev);

        // All IDs must belong to the selected contract before any supplier is
        // created or any procurement row changed. Bulk operations are atomic.
        foreach (var positionId in positionIds.Distinct())
        {
            using var scope = new SqlCommand(
                "SELECT COUNT_BIG(*) FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "JOIN dbo.contracts c ON c.id=sy.contract_id " +
                "WHERE p.id=@position AND sy.contract_id=@contract AND c.locked=0", conn, tx);
            scope.Parameters.Add("@position", SqlDbType.BigInt).Value = positionId;
            scope.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (Convert.ToInt64(scope.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                return SqlServerDatabase.SaveResult.Fail(
                    "Часть позиций не принадлежит текущему договору. Изменения отменены.", rev);
        }

        long? supplierId = null;
        if (supplier is not null && supplier.Trim().Length > 0)
        {
            var clean = supplier.Trim();
            using (var find = new SqlCommand(
                "SELECT id FROM dbo.suppliers WHERE name=@name", conn, tx))
            {
                find.Parameters.AddWithValue("@name", clean);
                var value = find.ExecuteScalar();
                if (value is not null and not DBNull)
                    supplierId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            if (supplierId is null)
                using (var all = new SqlCommand("SELECT id,name FROM dbo.suppliers", conn, tx))
                    using (var r = all.ExecuteReader())
                        while (r.Read())
                            if (string.Equals(r.GetString(1), clean, StringComparison.OrdinalIgnoreCase))
                            {
                                supplierId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                                break;
                            }
            if (supplierId is null)
                using (var create = new SqlCommand(
                    "INSERT dbo.suppliers (name) OUTPUT INSERTED.id VALUES (@name)", conn, tx))
                {
                    create.Parameters.AddWithValue("@name", clean);
                    supplierId = Convert.ToInt64(create.ExecuteScalar(), CultureInfo.InvariantCulture);
                }
        }

        var changedAny = false;
        foreach (var positionId in positionIds.Distinct())
        {
            var changes = new List<string>();
            long? procurementId = null;
            using (var find = new SqlCommand(
                "SELECT id, supplier_id, stage, order_ref, invoice_ref, cost_price, due_date, " +
                "replacement, note, direct_to_site, source, paid_date, responsible FROM dbo.procurement WHERE position_id=@id", conn, tx))
            {
                find.Parameters.AddWithValue("@id", positionId);
                using var r = find.ExecuteReader();
                if (r.Read())
                {
                    procurementId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                    var same = (SupplierId: r.IsDBNull(1) ? (long?)null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                        Stage: r.IsDBNull(2) ? "" : r.GetString(2),
                        OrderRef: r.IsDBNull(3) ? "" : r.GetString(3),
                        InvoiceRef: r.IsDBNull(4) ? "" : r.GetString(4),
                        CostPrice: r.IsDBNull(5) ? (decimal?)null : Money.FromDouble(Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture)),
                        DueDate: r.IsDBNull(6) ? (DateTime?)null : Convert.ToDateTime(r.GetValue(6), CultureInfo.InvariantCulture),
                        Replacement: r.IsDBNull(7) ? "" : r.GetString(7),
                        Note: r.IsDBNull(8) ? "" : r.GetString(8),
                        Direct: !r.IsDBNull(9) && Convert.ToBoolean(r.GetValue(9), CultureInfo.InvariantCulture),
                        Source: r.IsDBNull(10) ? "" : r.GetString(10),
                        PaidDate: r.IsDBNull(11) ? (DateTime?)null : Convert.ToDateTime(r.GetValue(11), CultureInfo.InvariantCulture),
                        Responsible: r.IsDBNull(12) ? "" : r.GetString(12));
                    if (supplier is not null && same.SupplierId != supplierId)
                        changes.Add($"поставщик → {supplier.Trim()}");
                    if (stage is not null && same.Stage != stage) changes.Add($"stage → {stage}");
                    if (orderRef is not null && same.OrderRef != orderRef) changes.Add($"order_ref → {orderRef}");
                    if (invoiceRef is not null && same.InvoiceRef != invoiceRef) changes.Add($"invoice_ref → {invoiceRef}");
                    if ((costPrice is not null || clearCostPrice) &&
                        same.CostPrice != CostPriceForUpdate(same.CostPrice, costPrice, clearCostPrice))
                        changes.Add(clearCostPrice ? "cost_price → пусто" :
                            $"cost_price → {costPrice!.Value.ToString(CultureInfo.InvariantCulture)}");
                    if ((dueDate is not null || clearDueDate) &&
                        same.DueDate != DueDateForUpdate(same.DueDate, dueDate, clearDueDate))
                        changes.Add(clearDueDate ? "due_date → пусто" :
                            $"due_date → {dueDate!.Value:dd.MM.yyyy}");
                    if (replacement is not null && same.Replacement != replacement) changes.Add("replacement");
                    if (responsible is not null && same.Responsible != responsible) changes.Add("responsible");
                    if (note is not null && same.Note != note) changes.Add("note");
                    if (directToSite is not null && same.Direct != directToSite)
                        changes.Add($"direct_to_site → {directToSite.Value}");
                    if (paidDate is not null && same.PaidDate?.Date != paidDate.Value.Date)
                        changes.Add($"paid_date → {paidDate:dd.MM.yyyy}");
                    if (clearPaidDate && same.PaidDate is not null) changes.Add("paid_date → пусто");
                    var source = explicitSource ?? (stage is "shop_queued" or "shop_work" ? "shop" :
                        supplier is not null && supplier.Trim().Length > 0
                        ? (supplier.Trim().StartsWith("цех", StringComparison.OrdinalIgnoreCase) ? "shop" : "buy") : same.Source);
                    if (same.Source != source)
                        changes.Add($"source → {source}");
                    if (changes.Count == 0) continue;
                    using var update = new SqlCommand(
                        "UPDATE dbo.procurement SET supplier_id=@supplier, stage=@stage, order_ref=@order, " +
                        "invoice_ref=@invoice, cost_price=@cost, due_date=@due, replacement=@repl, " +
                        "note=@note, responsible=@responsible, direct_to_site=@direct, source=@source,paid_date=@paid,updated_at=SYSDATETIME(), " +
                        "updated_by=@user WHERE id=@id", conn, tx);
                    var updatedSupplierId = SupplierIdForUpdate(same.SupplierId, supplier, supplierId);
                    update.Parameters.AddWithValue("@supplier",
                        updatedSupplierId is null ? DBNull.Value : (object)updatedSupplierId.Value);
                    update.Parameters.AddWithValue("@stage", stage ?? same.Stage);
                    update.Parameters.AddWithValue("@order", orderRef ?? same.OrderRef);
                    update.Parameters.AddWithValue("@invoice", invoiceRef ?? same.InvoiceRef);
                    var updatedCost = CostPriceForUpdate(same.CostPrice, costPrice, clearCostPrice);
                    var updatedDue = DueDateForUpdate(same.DueDate, dueDate, clearDueDate);
                    update.Parameters.AddWithValue("@cost",
                        updatedCost is null ? DBNull.Value : (object)(double)updatedCost.Value);
                    update.Parameters.AddWithValue("@due",
                        updatedDue is null ? DBNull.Value : (object)updatedDue.Value);
                    update.Parameters.AddWithValue("@repl", replacement ?? same.Replacement);
                    update.Parameters.AddWithValue("@note", note ?? same.Note);
                    update.Parameters.AddWithValue("@responsible", responsible ?? same.Responsible);
                    update.Parameters.AddWithValue("@direct",
                        directToSite is null ? (same.Direct ? 1 : 0) : (directToSite.Value ? 1 : 0));
                    update.Parameters.AddWithValue("@source", source);
                    update.Parameters.AddWithValue("@paid", clearPaidDate ? DBNull.Value :
                        paidDate is null ? (object?)same.PaidDate ?? DBNull.Value : paidDate.Value.Date);
                    update.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
                    update.Parameters.AddWithValue("@id", procurementId.Value);
                    if (update.ExecuteNonQuery() != 1)
                        throw new DBConcurrencyException("Запись закупки изменилась во время сохранения.");
                }
            }
            if (procurementId is null)
            {
                // Закупки ещё нет: создаём с переданными полями.
                // responsible/buy_name — NOT NULL без дефолта, как в эталоне (пустые строки).
                using var create = new SqlCommand(
                    "INSERT dbo.procurement (position_id,supplier_id,stage,order_ref,invoice_ref,cost_price," +
                    "due_date,replacement,note,direct_to_site,source,paid_date,responsible,buy_name," +
                    "updated_at,updated_by) " +
                    "VALUES (@id,@supplier,@stage,@order,@invoice,@cost,@due,@repl,@note,@direct,@source,@paid," +
                    "@responsible,(SELECT name FROM dbo.positions WHERE id=@id)," +
                    "SYSDATETIME(),@user)", conn, tx);
                create.Parameters.AddWithValue("@id", positionId);
                create.Parameters.AddWithValue("@supplier", supplierId is null ? DBNull.Value : supplierId.Value);
                create.Parameters.AddWithValue("@stage", stage ?? "");
                create.Parameters.AddWithValue("@order", orderRef ?? "");
                create.Parameters.AddWithValue("@invoice", invoiceRef ?? "");
                create.Parameters.AddWithValue("@cost", costPrice is null ? DBNull.Value : (object)(double)costPrice.Value);
                create.Parameters.AddWithValue("@due", dueDate is null ? DBNull.Value : dueDate.Value);
                create.Parameters.AddWithValue("@repl", replacement ?? "");
                create.Parameters.AddWithValue("@note", note ?? "");
                create.Parameters.AddWithValue("@responsible", responsible ?? "");
                create.Parameters.AddWithValue("@direct", directToSite is true ? 1 : 0);
                create.Parameters.AddWithValue("@source", explicitSource ?? (
                    stage is "shop_queued" or "shop_work" ||
                    supplier is not null && supplier.Trim().ToLowerInvariant().StartsWith("цех") ? "shop" : "buy"));
                create.Parameters.AddWithValue("@paid", paidDate is null ? DBNull.Value : paidDate.Value.Date);
                create.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
                create.ExecuteNonQuery();
                changes.Add("создана запись закупки");
                if (supplier is not null && supplier.Trim().Length > 0)
                    changes.Add($"поставщик → {supplier.Trim()}");
                if (stage is not null && stage.Length > 0) changes.Add($"stage → {stage}");
            }
            if (changes.Count == 0) continue;
            changedAny = true;
            using (var log = new SqlCommand(
                "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details) " +
                "VALUES (@ts,@user,N'procurement',@id,N'update',@details)", conn, tx))
            {
                log.Parameters.AddWithValue("@ts", DateTime.Now);
                log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
                log.Parameters.AddWithValue("@id", positionId);
                var positionName = PositionName(conn, tx, positionId);
                var shortName = positionName[..Math.Min(60, positionName.Length)];
                log.Parameters.AddWithValue("@details", $"{shortName}: {string.Join("; ", changes)}");
                log.ExecuteNonQuery();
            }
        }
        var updated = changedAny ? SqlServerDatabase.BumpPositionRevision(conn, tx) : rev;
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>Движение по строке: пришло на склад / выдано / возвраты (procurement.add_movement).</summary>
    [Obsolete("Use AddMovements for receipts/returns or IssueViaListBatch for outgoing issues.")]
    public static SqlServerDatabase.SaveResult AddMovement(string connectionString, long positionId,
        string kind, decimal qty, DateTime? when, string doc, string note, string expectedRevision) =>
        SqlServerDatabase.SaveResult.Fail(
            "Старый метод движения отключён. Используйте пакетное движение или ведомость выдачи.", expectedRevision);


    /// <summary>Movement history for one position. Read-only, contract-scoped.</summary>
    public sealed record MovementHistoryRow(
        long Id, long PositionId, string Kind, string KindLabel, decimal Qty,
        DateTime? Date, string Document, string Note, string User, long? ShipmentId);

    public static string MovementKindLabel(string kind) => kind switch
    {
        "in" => "Пришло на склад",
        "out" => "Выдано на объект",
        "back" => "Возврат с объекта",
        "back_supplier" => "Возврат поставщику",
        _ => kind,
    };

    /// <summary>Reader mapping independent of SQL Server (offline regression fixtures).</summary>
    public static MovementHistoryRow MapMovement(IDataRecord r)
    {
        string ReadText(int i) => r.IsDBNull(i) ? "" : Convert.ToString(
            r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
        var kind = ReadText(2);
        return new MovementHistoryRow(
            Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
            kind, MovementKindLabel(kind),
            Money.FromDouble(Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture)),
            r.IsDBNull(4) ? null : Convert.ToDateTime(r.GetValue(4), CultureInfo.InvariantCulture),
            ReadText(5), ReadText(6), ReadText(7),
            r.IsDBNull(8) ? null : Convert.ToInt64(r.GetValue(8), CultureInfo.InvariantCulture));
    }

    public static IReadOnlyList<MovementHistoryRow> MovementsForPosition(
        string connectionString, int contractId, long positionId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        return MovementsForPosition(conn, contractId, positionId);
    }

    public static IReadOnlyList<MovementHistoryRow> MovementsForPosition(
        SqlConnection conn, int contractId, long positionId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var cmd = new SqlCommand(
            "SELECT m.id,m.position_id,m.kind,m.qty,m.[date],ISNULL(m.doc,N'')," +
            "ISNULL(m.note,N''),ISNULL(m.[user],N''),m.shipment_id " +
            "FROM dbo.movements m " +
            "JOIN dbo.positions p ON p.id=m.position_id " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@contract AND m.position_id=@position " +
            "ORDER BY m.[date] DESC,m.id DESC", conn);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        cmd.Parameters.Add("@position", SqlDbType.BigInt).Value = positionId;
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var reader = SqlReadCancellation.ExecuteReader(cmd, token);
        var movements = new List<MovementHistoryRow>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            movements.Add(MapMovement(reader));
        }
        token.ThrowIfCancellationRequested();
        return movements;
    }

    /// <summary>Python proc_view.py:delete_movement never deletes shipment-backed issues.</summary>
    public static string? MovementDeletionError(MovementHistoryRow? movement, long positionId)
    {
        if (movement is null || movement.PositionId != positionId)
            return "Движение не найдено в выбранной позиции.";
        return movement.ShipmentId is not null
            ? "Эта выдача сделана по ведомости. Отмените её на вкладке «Выдача»."
            : null;
    }

    /// <summary>
    /// Delete a single independent movement using optimistic revision and row lock.
    /// Shipment-linked movements must be reversed through the shipment editor.
    /// </summary>
    public static SqlServerDatabase.SaveResult DeleteMovement(
        string connectionString, int contractId, long positionId,
        long movementId, string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail(
            "Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail(
                "Данные изменены другим сотрудником. Обновите закупки.", rev);

        MovementHistoryRow? movement = null;
        string name = "";
        using (var read = new SqlCommand(
            "SELECT m.id,m.position_id,m.kind,m.qty,m.[date],ISNULL(m.doc,N'')," +
            "ISNULL(m.note,N''),ISNULL(m.[user],N''),m.shipment_id,p.name " +
            "FROM dbo.movements m WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.positions p ON p.id=m.position_id " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "JOIN dbo.contracts c ON c.id=sy.contract_id " +
            "WHERE m.id=@mid AND m.position_id=@position AND sy.contract_id=@contract AND c.locked=0", conn, tx))
        {
            read.Parameters.Add("@mid", SqlDbType.BigInt).Value = movementId;
            read.Parameters.Add("@position", SqlDbType.BigInt).Value = positionId;
            read.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                movement = MapMovement(reader);
                name = reader.IsDBNull(9) ? "" : reader.GetString(9);
            }
        }
        var blocker = MovementDeletionError(movement, positionId);
        if (blocker is not null) return SqlServerDatabase.SaveResult.Fail(blocker, rev);

        using (var delete = new SqlCommand(
            "DELETE FROM dbo.movements WHERE id=@mid AND position_id=@position " +
            "AND shipment_id IS NULL", conn, tx))
        {
            delete.Parameters.Add("@mid", SqlDbType.BigInt).Value = movementId;
            delete.Parameters.Add("@position", SqlDbType.BigInt).Value = positionId;
            if (delete.ExecuteNonQuery() != 1)
                return SqlServerDatabase.SaveResult.Fail(
                    "Движение изменилось или удалено. Обновите закупки.", rev);
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (@ts,@user,N'movement',@id,N'delete',@details,@contract)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = movementId;
            log.Parameters.AddWithValue("@details",
                $"{name[..Math.Min(60,name.Length)]}: удалено движение " +
                $"{movement!.KindLabel} {movement.Qty:0.######}" +
                (movement.Date is null ? "" : $" ({movement.Date.Value:dd.MM.yyyy})"));
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    private static string PositionName(SqlConnection conn, SqlTransaction tx, long positionId)
    {
        using var cmd = new SqlCommand("SELECT name FROM dbo.positions WHERE id=@id", conn, tx);
        cmd.Parameters.AddWithValue("@id", positionId);
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    private static void RequirePilotWrite(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.InitialCatalog.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Запись C# разрешена только в отдельную тестовую базу GmcSmetaPilot_*. Рабочая база не изменена.");
    }
    /// <summary>Строка контрольной таблицы для импорта (procurement.RowPlan, упрощённо:
    /// колонки распознаются по заголовкам файла).</summary>
    public sealed record PlanRow(string Name, string Unit, string Supplier, string Stage,
        string OrderRef, string InvoiceRef, decimal? CostPrice, DateTime? DueDate,
        string Replacement, string Note);

    /// <summary>
    /// Legacy name+unit importer is intentionally disabled: it could silently
    /// match repeated equipment in different systems and overwrite manual prices.
    /// Use PreviewSheet and ApplySheet with explicit collision resolution instead.
    /// </summary>
    [Obsolete("Unsafe importer disabled; use PreviewSheet/ApplySheet.")]
    public static (int Applied, int Unmatched) ImportPlan(string connectionString, int contractId,
        IReadOnlyList<PlanRow> rows, bool overwrite, string expectedRevision) =>
        throw new NotSupportedException("Старый импорт по имени отключён: используйте безопасный предпросмотр ведомости.");

    /// <summary>Legacy object-only issue API is disabled. Contract-scoped issuance must use
    /// IssueViaListBatch; corrections to a fixed list use the shipment editor.</summary>
    [Obsolete("Use IssueViaListBatch or SqlServerShipments.SetQty.")]
    public static (long ShipmentId, decimal Remaining) IssueViaList(string connectionString,
        long objectId, long positionId, decimal delta, DateTime when, string recipient,
        string expectedRevision) =>
        throw new NotSupportedException("Старый метод выдачи отключён. Используйте ведомость или пакетную выдачу.");

}
