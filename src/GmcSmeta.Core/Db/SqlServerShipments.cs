using System.Data;
using System.Globalization;
using System.Threading;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Выдача на объект — ведомости (shipments.py): обычная ведомость собирается,
/// затем «фиксируется» — каждая строка становится движением «выдано на объект».
/// Фиксированная ведомость правится с перепроведением движений. «Остаток к выдаче»
/// = количество по КП − выданное.
/// </summary>
public static class SqlServerShipments
{
    private const decimal Eps = 0.000001m;

    public sealed record ShipmentInfo(long Id, string Number, DateTime Date,
        string Recipient, string IssuedBy, bool Fixed, int Lines, string Note);

    public sealed record ShipmentLineRow(long LineId, long PositionId, long ShipmentId,
        string Name, string Unit, decimal Qty, decimal Price, decimal QtyByKp, decimal Issued);

    public sealed record IssuableRow(long ContractId, long PositionId, long SystemId,
        string SystemName, string Name, string Unit, decimal Qty, decimal Price,
        decimal Received, decimal Issued, bool OutOfKp, bool IsSub = false);

    public sealed record ShipmentDocument(long Id, string Title);

    // Keep targeted queries well below SQL Server's 2100-parameter limit.
    public const int MaxIncrementalRows = 256;
    public sealed record ShipmentChanges(ShipmentInfo Shipment,
        IReadOnlyList<IssuableRow> Candidates, IReadOnlyList<ShipmentLineRow> Lines, string Revision);

    /// <summary>Read only positions affected by a successful shipment write.
    /// If the database revision no longer matches that write, the caller must reload everything.</summary>
    public static ShipmentChanges? ReadChangedRows(string connectionString, long objectId, long shipmentId,
        IReadOnlyList<long> positionIds, string committedRevision)
    {
        if (string.IsNullOrEmpty(committedRevision) || positionIds.Count == 0 ||
            positionIds.Count > MaxIncrementalRows || positionIds.Any(id => id <= 0) ||
            positionIds.Distinct().Count() != positionIds.Count) return null;
        using var conn = SqlServerDatabase.Open(connectionString);
        if (SqlServerDatabase.Revision(conn) != committedRevision) return null;
        var shipment = ShipmentsOf(conn, objectId, shipmentId).SingleOrDefault();
        if (shipment is null) return null;
        var candidates = IssuableRows(conn, objectId, positionIds);
        var lines = LinesOf(conn, shipmentId, positionIds);
        if (SqlServerDatabase.Revision(conn) != committedRevision ||
            candidates.Count != positionIds.Count) return null;
        return new ShipmentChanges(shipment, candidates, lines, committedRevision);
    }

    private static string PositionFilter(IReadOnlyList<long>? ids)
    {
        if (ids is null) return "";
        if (ids.Count == 0 || ids.Count > MaxIncrementalRows)
            throw new ArgumentOutOfRangeException(nameof(ids));
        return " AND p.id IN (" + string.Join(",", Enumerable.Range(0, ids.Count).Select(i => $"@p{i}")) + ")";
    }

    private static void AddPositionParameters(SqlCommand cmd, IReadOnlyList<long>? ids)
    {
        if (ids is null) return;
        for (var i = 0; i < ids.Count; i++)
            cmd.Parameters.Add($"@p{i}", SqlDbType.BigInt).Value = ids[i];
    }

    /// <summary>Все договоры/ДС объекта, в том числе без доступных позиций.</summary>
    public static List<ShipmentDocument> DocumentsOf(SqlConnection conn, long objectId,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var list = new List<ShipmentDocument>();
        using var cmd = new SqlCommand(
            "SELECT id,kind,number,[date],name FROM dbo.contracts " +
            "WHERE object_id=@obj AND kind IN (N'main',N'dop') ORDER BY id", conn);
        cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var reader = SqlReadCancellation.ExecuteReader(cmd, token);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            string Text(int i) => reader.IsDBNull(i) ? "" :
                Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            list.Add(new ShipmentDocument(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                Documents.ContractTitleText(Text(1), Text(2), Text(3), Text(4))));
        }
        token.ThrowIfCancellationRequested();
        return list;
    }

    public static string TitleOf(string number) => $"Ведомость № {number}";

    /// <summary>Ведомости объекта (старые последними, как o.shipments).</summary>
    public static List<ShipmentInfo> ShipmentsOf(SqlConnection conn, long objectId, long? shipmentId = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var list = new List<ShipmentInfo>();
        using var cmd = new SqlCommand(
            "SELECT id,number,[date],recipient,issued_by,fixed,note," +
            "(SELECT COUNT_BIG(*) FROM dbo.shipment_lines sl WHERE sl.shipment_id=sh.id) " +
            "FROM dbo.shipments sh WHERE object_id=@id" +
            (shipmentId.HasValue ? " AND sh.id=@shipment" : "") + " ORDER BY id", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
        if (shipmentId.HasValue) cmd.Parameters.Add("@shipment", SqlDbType.BigInt).Value = shipmentId.Value;
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            list.Add(new ShipmentInfo(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                r.GetString(1),
                r.IsDBNull(2) ? DateTime.Today : Convert.ToDateTime(r.GetValue(2), CultureInfo.InvariantCulture),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? "" : r.GetString(4),
                !r.IsDBNull(5) && Convert.ToBoolean(r.GetValue(5), CultureInfo.InvariantCulture),
                (int)Convert.ToInt64(r.GetValue(7), CultureInfo.InvariantCulture),
                r.IsDBNull(6) ? "" : r.GetString(6)));
        }
        token.ThrowIfCancellationRequested();
        return list;
    }

    /// <summary>Строки ведомости с данными позиций (для окна выдачи).</summary>
    public static List<ShipmentLineRow> LinesOf(SqlConnection conn, long shipmentId, IReadOnlyList<long>? positionIds = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var list = new List<ShipmentLineRow>();
        using var cmd = new SqlCommand(
            "SELECT sl.id, sl.position_id, sl.shipment_id, p.name, p.unit, sl.qty, " +
            "p.qty, p.price, " +
            "ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'out'),0) " +
            "- ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'back'),0) AS issued " +
            "FROM dbo.shipment_lines sl JOIN dbo.positions p ON p.id=sl.position_id " +
            "WHERE sl.shipment_id=@id" + PositionFilter(positionIds) + " ORDER BY sl.id", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = shipmentId;
        AddPositionParameters(cmd, positionIds);
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            var positionId = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
            var qtyByKp = r.IsDBNull(6) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(6), CultureInfo.InvariantCulture));
            var price = r.IsDBNull(7) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(7), CultureInfo.InvariantCulture));
            list.Add(new ShipmentLineRow(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                positionId,
                Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? "" : r.GetString(4),
                Money.FromDouble(Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture)),
                price, qtyByKp, r.IsDBNull(8) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(8), CultureInfo.InvariantCulture))));
        }
        token.ThrowIfCancellationRequested();
        return list;
    }

    /// <summary>Выдано по строке вне ведомостей (movements out − back), для окна выдачи.</summary>
    public static decimal IssuedOf(SqlConnection conn, long positionId,
        SqlTransaction? transaction = null)
    {
        using var cmd = new SqlCommand(
            "SELECT ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=@id AND m.kind=N'out'),0)" +
            " - ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=@id AND m.kind=N'back'),0)", conn, transaction);
        cmd.Parameters.AddWithValue("@id", positionId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0m : Money.FromDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
    }

    /// <summary>Строки объекта, которые можно выдавать (shipments.object_positions):
    /// договоры/ДС, верхние строки и комплектующие первого уровня, не работы; вне КП тоже берутся.</summary>
    public static List<IssuableRow> IssuableRows(SqlConnection conn, long objectId, IReadOnlyList<long>? positionIds = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var list = new List<IssuableRow>();
        using var cmd = new SqlCommand(
            "SELECT sy.contract_id, p.id, p.system_id, sy.name, p.name, p.unit, p.qty, p.price, " +
            "p.out_of_kp, " +
            "ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'in'),0)" +
            " - ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'back_supplier'),0) AS received, " +
            "ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'out'),0)" +
            " - ISNULL((SELECT SUM(m.qty) FROM dbo.movements m WHERE m.position_id=p.id AND m.kind=N'back'),0) AS issued, p.parent_id " +
            "FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "JOIN dbo.contracts c ON c.id=sy.contract_id " +
            "LEFT JOIN dbo.positions parent ON parent.id=p.parent_id AND parent.system_id=p.system_id " +
            "WHERE c.object_id=@obj AND c.kind IN (N'main',N'dop') AND " + IssuableHierarchy + PositionFilter(positionIds) + " " +
            "ORDER BY c.id,sy.[order],sy.id,COALESCE(parent.[order],p.[order]),COALESCE(parent.id,p.id)," +
            "CASE WHEN p.parent_id IS NULL THEN 0 ELSE 1 END,p.[order],p.id", conn);
        cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
        AddPositionParameters(cmd, positionIds);
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            var outOfKp = !r.IsDBNull(8) && Convert.ToBoolean(r.GetValue(8), CultureInfo.InvariantCulture);
            var name = r.IsDBNull(4) ? "" : r.GetString(4);
            if (EstimateStatistics.IsWorkName(name)) continue;
            list.Add(new IssuableRow(
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
                r.IsDBNull(3) ? "" : r.GetString(3),
                name,
                r.IsDBNull(5) ? "" : r.GetString(5),
                r.IsDBNull(6) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(6), CultureInfo.InvariantCulture)),
                r.IsDBNull(7) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(7), CultureInfo.InvariantCulture)),
                r.IsDBNull(9) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(9), CultureInfo.InvariantCulture)),
                r.IsDBNull(10) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(10), CultureInfo.InvariantCulture)),
                outOfKp, !r.IsDBNull(11)));
        }
        token.ThrowIfCancellationRequested();
        return list;
    }

    // builder.ordered_positions: a top-level row followed by its immediate children.
    // A child must belong to the same system as its actual top-level parent.
    private const string IssuableHierarchy =
        "(p.parent_id IS NULL OR (parent.id IS NOT NULL AND parent.parent_id IS NULL))";

    private static bool IsIssuablePosition(SqlConnection conn, SqlTransaction tx, long objectId, long positionId)
    {
        using var cmd = new SqlCommand(
            "SELECT p.name FROM dbo.positions p JOIN dbo.systems s ON s.id=p.system_id " +
            "JOIN dbo.contracts c ON c.id=s.contract_id " +
            "LEFT JOIN dbo.positions parent ON parent.id=p.parent_id AND parent.system_id=p.system_id " +
            "WHERE p.id=@pid AND c.object_id=@obj AND c.kind IN (N'main',N'dop') AND " +
            IssuableHierarchy, conn, tx);
        cmd.Parameters.AddWithValue("@pid", positionId);
        cmd.Parameters.AddWithValue("@obj", objectId);
        var name = cmd.ExecuteScalar();
        return name is string text && !EstimateStatistics.IsWorkName(text);
    }

    /// <summary>Создать ведомость (shipments.create): номер следующий, получатель —
    /// как у предыдущей; журнал shipment/create.</summary>
    public static long Create(string connectionString, long objectId, string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);

        string objectName, lastRecipient = "", lastIssuedBy = "";
        using (var read = new SqlCommand(
            "SELECT name FROM dbo.objects WHERE id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", objectId);
            objectName = Convert.ToString(read.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
        }
        using (var last = new SqlCommand(
            "SELECT TOP(1) recipient,issued_by FROM dbo.shipments WHERE object_id=@id ORDER BY id DESC", conn, tx))
        {
            last.Parameters.AddWithValue("@id", objectId);
            using var r = last.ExecuteReader();
            if (r.Read())
            {
                lastRecipient = r.IsDBNull(0) ? "" : r.GetString(0);
                lastIssuedBy = r.IsDBNull(1) ? "" : r.GetString(1);
            }
        }
        int nextNumber = 1;
        using (var nums = new SqlCommand(
            "SELECT number FROM dbo.shipments WHERE object_id=@id", conn, tx))
        {
            nums.Parameters.AddWithValue("@id", objectId);
            using var r = nums.ExecuteReader();
            while (r.Read())
            {
                var raw = r.GetString(0).Trim('№', ' ');
                if (int.TryParse(raw, out var n) && n >= nextNumber) nextNumber = n + 1;
            }
        }
        long shipmentId;
        using (var insert = new SqlCommand(
            "INSERT dbo.shipments (object_id,number,[date],recipient,issued_by,note,fixed,fixed_by) " +
            "OUTPUT INSERTED.id VALUES (@obj,@number,SYSDATETIME(),@recipient,@issuedBy,N'',0,N'')", conn, tx))
        {
            insert.Parameters.AddWithValue("@obj", objectId);
            insert.Parameters.AddWithValue("@number", nextNumber.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("@recipient", lastRecipient);
            insert.Parameters.AddWithValue("@issuedBy", lastIssuedBy);
            shipmentId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,object_id) " +
            "VALUES (@ts,@user,N'shipment',@id,N'create',@details,@object)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", shipmentId);
            log.Parameters.AddWithValue("@details", $"{objectName}: Ведомость № {nextNumber}");
            log.Parameters.AddWithValue("@object", objectId);
            log.ExecuteNonQuery();
        }
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
        return shipmentId;
    }

    /// <summary>Установить количество строки в ведомости (shipments.set_qty): сверх остатка
    /// нельзя (allow_over — для фиксированных перепроведений); пусто — убрать строку.</summary>
    public static SqlServerDatabase.SaveResult SetQty(string connectionString, long shipmentId,
        long positionId, decimal qty, bool allowOver, string expectedRevision, bool? expectedFixed = null)
    {
        if (!ShipmentQuantityPolicy.IsValidEdit(qty))
            return SqlServerDatabase.SaveResult.Fail("Недопустимое количество: 0 или больше 0,000001, до 1 млрд, точность до 6 знаков.", expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови ведомости.", rev);
        bool fixedList;
        string number = "", objectName = "";
        long objectId;
        using (var read = new SqlCommand(
            "SELECT sh.fixed, sh.number, o.name, o.id FROM dbo.shipments sh " +
            "JOIN dbo.objects o ON o.id=sh.object_id WHERE sh.id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", shipmentId);
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Ведомость не найдена.", rev);
            fixedList = Convert.ToBoolean(r.GetValue(0), CultureInfo.InvariantCulture);
            number = r.GetString(1);
            objectName = r.GetString(2);
            objectId = Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture);
        }

        if (expectedFixed.HasValue && fixedList != expectedFixed.Value)
            return SqlServerDatabase.SaveResult.Fail("Состояние ведомости изменилось. Перечитайте данные.", rev);
        if (qty < -Eps)
            return SqlServerDatabase.SaveResult.Fail("Количество не может быть отрицательным.", rev);
        // Same eligibility rule as the candidate list: individual materials, including children.
        if (!IsIssuablePosition(conn, tx, objectId, positionId))
            return SqlServerDatabase.SaveResult.Fail("Позиция не относится к выдаваемым материалам объекта.", rev);

        var lineNow = LineQty(conn, tx, shipmentId, positionId);
        CheckLinkedLine(conn, tx, shipmentId, positionId, fixedList, lineNow);
        var leftToIssue = ShipmentQuantityPolicy.AvailableForChange(QtyByKp(conn, tx, positionId),
            IssuedOf(conn, positionId, tx), fixedList ? lineNow : 0m);
        if (ShipmentQuantityPolicy.ExceedsRemaining(leftToIssue, qty) && !allowOver)
            return SqlServerDatabase.SaveResult.Fail($"Осталось выдать {leftToIssue:0.######}", rev);

        var old = lineNow;
        if (qty <= Eps)
        {
            Execute(conn, tx, "DELETE FROM dbo.shipment_lines WHERE shipment_id=@sid AND position_id=@pid",
                positionId, shipmentId);
        }
        else if (lineNow == 0m)
        {
            using var insert = new SqlCommand(
                "INSERT dbo.shipment_lines (shipment_id,position_id,qty) VALUES (@sid,@pid,@q)", conn, tx);
            insert.Parameters.AddWithValue("@sid", shipmentId);
            insert.Parameters.AddWithValue("@pid", positionId);
            insert.Parameters.AddWithValue("@q", (double)qty);
            insert.ExecuteNonQuery();
        }
        else
        {
            using var update = new SqlCommand(
                "UPDATE dbo.shipment_lines SET qty=@q WHERE shipment_id=@sid AND position_id=@pid", conn, tx);
            update.Parameters.AddWithValue("@q", (double)qty);
            update.Parameters.AddWithValue("@sid", shipmentId);
            update.Parameters.AddWithValue("@pid", positionId);
            update.ExecuteNonQuery();
        }
        if (fixedList) SyncFixedLine(conn, tx, shipmentId, positionId, Math.Max(qty, 0m));
        if (fixedList && Math.Abs(old - qty) > Eps)
        {
            var positionName = PositionName(conn, tx, positionId);
            using (var log = new SqlCommand(
                "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details) " +
                "VALUES (@ts,@user,N'shipment',@id,N'edit',@details)", conn, tx))
            {
                log.Parameters.AddWithValue("@ts", DateTime.Now);
                log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
                log.Parameters.AddWithValue("@id", shipmentId);
                log.Parameters.AddWithValue("@details",
                    $"{TitleOf(number)}: {positionName[..Math.Min(60, positionName.Length)]} " +
                    $"{old:0.######} → {qty:0.######} (после фиксации)");
                log.ExecuteNonQuery();
            }
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>
    /// Add several rows to a draft shipment atomically. This avoids partially
    /// completed issuing lists when a later selected position is invalid or the
    /// database revision changed. No stock movement is created until Fix().
    /// </summary>
    public static SqlServerDatabase.SaveResult AddRowsBatch(
        string connectionString, long shipmentId,
        IReadOnlyList<(long PositionId, decimal Qty)> rows, string expectedRevision, bool expectedFixed = false)
    {
        if (rows is null || rows.Count == 0)
            return SqlServerDatabase.SaveResult.Fail("Нет строк для добавления.", expectedRevision);
        if (rows.Any(r => r.PositionId <= 0 || r.Qty <= Eps) ||
            rows.Select(r => r.PositionId).Distinct().Count() != rows.Count)
            return SqlServerDatabase.SaveResult.Fail("Список содержит неверные или повторные позиции.", expectedRevision);

        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null)
            return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обновите выдачу.", rev);

        long objectId;
        bool fixedList;
        using (var command = new SqlCommand(
            "SELECT object_id, fixed FROM dbo.shipments WHERE id=@id", conn, tx))
        {
            command.Parameters.AddWithValue("@id", shipmentId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return SqlServerDatabase.SaveResult.Fail("Ведомость не найдена.", rev);
            objectId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            fixedList = Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (fixedList != expectedFixed)
                return SqlServerDatabase.SaveResult.Fail("Состояние ведомости изменилось. Перечитайте данные.", rev);
        }

        foreach (var (positionId, qty) in rows)
        {
            if (!IsIssuablePosition(conn, tx, objectId, positionId))
                return SqlServerDatabase.SaveResult.Fail("Одна из позиций не относится к выдаваемым материалам объекта.", rev);

            var existingQty = LineQty(conn, tx, shipmentId, positionId);
            CheckLinkedLine(conn, tx, shipmentId, positionId, fixedList, existingQty);
            if (existingQty > Eps) return SqlServerDatabase.SaveResult.Fail($"Позиция {positionId} уже есть в ведомости.", rev);
            var remaining = ShipmentQuantityPolicy.Remaining(QtyByKp(conn, tx, positionId),
                IssuedOf(conn, positionId, tx));
            if (qty > remaining + Eps)
                return SqlServerDatabase.SaveResult.Fail(
                    $"По позиции {positionId} осталось выдать {remaining:0.######}, запрошено {qty:0.######}.", rev);

            using var insert = new SqlCommand(
                "INSERT dbo.shipment_lines (shipment_id,position_id,qty) " +
                "SELECT @sid,@pid,@qty WHERE NOT EXISTS " +
                "(SELECT 1 FROM dbo.shipment_lines WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE shipment_id=@sid AND position_id=@pid)", conn, tx);
            insert.Parameters.AddWithValue("@sid", shipmentId);
            insert.Parameters.AddWithValue("@pid", positionId);
            insert.Parameters.AddWithValue("@qty", (double)qty);
            if (insert.ExecuteNonQuery() != 1)
                return SqlServerDatabase.SaveResult.Fail(
                    $"Позиция {positionId} уже есть в ведомости.", rev);
            if (fixedList) SyncFixedLine(conn, tx, shipmentId, positionId, qty);
        }

        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,object_id) " +
            "VALUES (@ts,@user,N'shipment',@id,N'edit',@details,@obj)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", shipmentId);
            log.Parameters.AddWithValue("@details", $"Добавлено строк в {(fixedList ? "выданную ведомость" : "черновик")}: {rows.Count}");
            log.Parameters.AddWithValue("@obj", objectId);
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>Убрать выбранные позиции из черновика одной транзакцией.
    /// Не затрагивает смету и движения; любое нарушение откатывает весь пакет.</summary>
    public static SqlServerDatabase.SaveResult RemoveRowsBatch(
        string connectionString, long shipmentId,
        IReadOnlyList<long> positionIds, string expectedRevision, bool expectedFixed = false)
    {
        if (positionIds is null || positionIds.Count == 0 ||
            positionIds.Any(id => id <= 0) || positionIds.Distinct().Count() != positionIds.Count)
            return SqlServerDatabase.SaveResult.Fail("Нет строк или список содержит неверные/повторные позиции.", expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null)
            return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обновите выдачу.", rev);

        long objectId;
        bool fixedList;
        using (var read = new SqlCommand("SELECT object_id,fixed FROM dbo.shipments WHERE id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", shipmentId);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return SqlServerDatabase.SaveResult.Fail("Ведомость не найдена.", rev);
            objectId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            fixedList = Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
            if (fixedList != expectedFixed)
                return SqlServerDatabase.SaveResult.Fail("Состояние ведомости изменилось. Перечитайте данные.", rev);
        }
        // Черновик не должен содержать складских движений; не скрывать повреждение данных.
        if (!fixedList)
        using (var check = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.movements WHERE shipment_id=@id", conn, tx))
        {
            check.Parameters.AddWithValue("@id", shipmentId);
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                return SqlServerDatabase.SaveResult.Fail("У черновика есть движения. Требуется проверка ведомости.", rev);
        }
        foreach (var positionId in positionIds)
        {
            var oldQty = LineQty(conn, tx, shipmentId, positionId);
            CheckLinkedLine(conn, tx, shipmentId, positionId, fixedList, oldQty);
            using var delete = new SqlCommand(
                "DELETE sl FROM dbo.shipment_lines sl " +
                "JOIN dbo.positions p ON p.id=sl.position_id " +
                "JOIN dbo.systems s ON s.id=p.system_id " +
                "JOIN dbo.contracts c ON c.id=s.contract_id " +
                "WHERE sl.shipment_id=@sid AND sl.position_id=@pid AND c.object_id=@obj " +
                "AND c.kind IN (N'main',N'dop')", conn, tx);
            delete.Parameters.AddWithValue("@sid", shipmentId);
            delete.Parameters.AddWithValue("@pid", positionId);
            delete.Parameters.AddWithValue("@obj", objectId);
            if (delete.ExecuteNonQuery() != 1)
                return SqlServerDatabase.SaveResult.Fail("Одна из строк отсутствует или не принадлежит объекту.", rev);
            if (fixedList) SyncFixedLine(conn, tx, shipmentId, positionId, 0m);
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,object_id) " +
            "VALUES (@ts,@user,N'shipment',@id,N'edit',@details,@obj)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", shipmentId);
            log.Parameters.AddWithValue("@details", $"Убрано строк из {(fixedList ? "выданной ведомости" : "черновика")}: {positionIds.Count}");
            log.Parameters.AddWithValue("@obj", objectId);
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>Провести ведомость (shipments.fix): каждая строка становится «выдано на объект».</summary>
    public static SqlServerDatabase.SaveResult Fix(string connectionString, long shipmentId,
        string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови ведомости.", rev);
        bool alreadyFixed;
        string number = "", objectName = "", recipient = "";
        DateTime date = DateTime.Today;
        long objectId = 0;
        using (var read = new SqlCommand(
            "SELECT sh.fixed, sh.number, sh.[date], ISNULL(sh.recipient,N''), o.name, sh.object_id " +
            "FROM dbo.shipments sh JOIN dbo.objects o ON o.id=sh.object_id WHERE sh.id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", shipmentId);
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Ведомость не найдена.", rev);
            alreadyFixed = Convert.ToBoolean(r.GetValue(0), CultureInfo.InvariantCulture);
            number = r.GetString(1);
            date = r.IsDBNull(2) ? DateTime.Today : Convert.ToDateTime(r.GetValue(2), CultureInfo.InvariantCulture);
            recipient = r.GetString(3);
            objectName = r.GetString(4);
            objectId = Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture);
        }
        if (alreadyFixed)
        {
            if (FixedLedgerProblem(conn, tx, shipmentId) is { } problem)
                return SqlServerDatabase.SaveResult.Fail(problem, rev);
            return new SqlServerDatabase.SaveResult(true, null, rev);
        }
        using (var check = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.movements WHERE shipment_id=@id", conn, tx))
        {
            check.Parameters.AddWithValue("@id", shipmentId);
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                return SqlServerDatabase.SaveResult.Fail("У черновика уже есть движения. Проведение остановлено.", rev);
        }
        var doc = TitleOf(number);
        var lines = new List<(long PositionId, decimal Qty)>();
        using (var read = new SqlCommand(
            "SELECT position_id, qty FROM dbo.shipment_lines WHERE shipment_id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", shipmentId);
            using var r = read.ExecuteReader();
            while (r.Read())
                lines.Add((Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture))));
        }
        if (lines.Count == 0)
            return SqlServerDatabase.SaveResult.Fail("Нельзя провести пустую ведомость.", rev);
        if (lines.Any(l => l.Qty <= Eps) || lines.Select(l => l.PositionId).Distinct().Count() != lines.Count)
            return SqlServerDatabase.SaveResult.Fail("В ведомости есть неверные количества или повторные позиции.", rev);
        using (var check = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.shipment_lines sl JOIN dbo.positions p ON p.id=sl.position_id " +
            "JOIN dbo.systems s ON s.id=p.system_id JOIN dbo.contracts c ON c.id=s.contract_id " +
            "WHERE sl.shipment_id=@id AND c.object_id=@obj AND c.kind IN (N'main',N'dop')", conn, tx))
        {
            check.Parameters.AddWithValue("@id", shipmentId);
            check.Parameters.AddWithValue("@obj", objectId);
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != lines.Count)
                return SqlServerDatabase.SaveResult.Fail("В ведомости есть позиции другого объекта или недоступного документа.", rev);
        }
        // Preserve the existing object check and reject work rows or malformed parent links.
        // All checks happen before creating any movements.
        foreach (var (positionId, _) in lines)
            if (!IsIssuablePosition(conn, tx, objectId, positionId))
                return SqlServerDatabase.SaveResult.Fail("В ведомости есть невыдаваемые позиции.", rev);
        foreach (var (positionId, qty) in lines)
        {
            using var insert = new SqlCommand(
                "INSERT dbo.movements (position_id,kind,qty,[date],doc,note,[user],shipment_id) " +
                "VALUES (@id,N'out',@qty,@date,@doc,@note,@user,@shipment)", conn, tx);
            insert.Parameters.AddWithValue("@id", positionId);
            insert.Parameters.AddWithValue("@qty", (double)qty);
            insert.Parameters.AddWithValue("@date", date);
            insert.Parameters.AddWithValue("@doc", doc);
            insert.Parameters.AddWithValue("@note", recipient);
            insert.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            insert.Parameters.AddWithValue("@shipment", shipmentId);
            insert.ExecuteNonQuery();
        }
        using (var mark = new SqlCommand(
            "UPDATE dbo.shipments SET fixed=1, fixed_at=SYSDATETIME(), fixed_by=@user WHERE id=@id", conn, tx))
        {
            mark.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            mark.Parameters.AddWithValue("@id", shipmentId);
            mark.ExecuteNonQuery();
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,object_id) " +
            "VALUES (@ts,@user,N'shipment',@id,N'fix',@details,@object)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", shipmentId);
            log.Parameters.AddWithValue("@details", $"{objectName}: {doc}, строк {lines.Count}");
            log.Parameters.AddWithValue("@object", objectId);
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>Отменить выдачу (shipments.unfix): движения ведомости удаляются.</summary>
    public static SqlServerDatabase.SaveResult Unfix(string connectionString, long shipmentId,
        string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови ведомости.", rev);
        string number = "", objectName = "";
        using (var read = new SqlCommand(
            "SELECT sh.number,o.name,sh.fixed FROM dbo.shipments sh JOIN dbo.objects o ON o.id=sh.object_id WHERE sh.id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", shipmentId);
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Ведомость не найдена.", rev);
            number = r.GetString(0);
            objectName = r.GetString(1);
            if (!r.GetBoolean(2)) return SqlServerDatabase.SaveResult.Fail("Ведомость уже является черновиком.", rev);
        }
        if (FixedLedgerProblem(conn, tx, shipmentId) is { } problem)
            return SqlServerDatabase.SaveResult.Fail(problem, rev);
        using (var delete = new SqlCommand(
            "DELETE FROM dbo.movements WHERE shipment_id=@id AND kind=N'out'", conn, tx))
        {
            delete.Parameters.AddWithValue("@id", shipmentId);
            delete.ExecuteNonQuery();
        }
        using (var unmark = new SqlCommand(
            "UPDATE dbo.shipments SET fixed=0, fixed_at=NULL,fixed_by=N'' WHERE id=@id AND fixed=1", conn, tx))
        {
            unmark.Parameters.AddWithValue("@id", shipmentId);
            if (unmark.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Состояние ведомости изменилось.");
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,object_id) " +
            "VALUES (@ts,@user,N'shipment',@id,N'unfix',@details,@object)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", shipmentId);
            log.Parameters.AddWithValue("@details", $"{objectName}: {TitleOf(number)}");
            log.Parameters.AddWithValue("@object", ObjectIdOf(conn, tx, shipmentId));
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>Every fixed line must have exactly one matching outgoing movement,
    /// and no extra movement may be deleted by Unfix. Validate in the writer's transaction.</summary>
    private static string? FixedLedgerProblem(SqlConnection conn, SqlTransaction tx, long shipmentId)
    {
        const string message = "Строки проведённой ведомости не соответствуют движениям. Требуется сверка.";
        using var cmd = new SqlCommand(
            @"WITH lines AS (
                SELECT position_id,COUNT_BIG(*) AS n,MIN(qty) AS qty
                FROM dbo.shipment_lines WHERE shipment_id=@id GROUP BY position_id
              ), moves AS (
                SELECT position_id,COUNT_BIG(*) AS n,MIN(qty) AS qty,
                  SUM(CASE WHEN kind=N'out' THEN 0 ELSE 1 END) AS bad
                FROM dbo.movements WHERE shipment_id=@id GROUP BY position_id
              )
              SELECT TOP(1) 1 FROM lines l FULL OUTER JOIN moves m ON m.position_id=l.position_id
              LEFT JOIN dbo.positions p ON p.id=COALESCE(l.position_id,m.position_id)
              LEFT JOIN dbo.systems sy ON sy.id=p.system_id
              LEFT JOIN dbo.contracts c ON c.id=sy.contract_id
              JOIN dbo.shipments sh ON sh.id=@id
              WHERE l.n IS NULL OR m.n IS NULL OR l.n<>1 OR m.n<>1 OR m.bad<>0
                 OR l.qty IS NULL OR m.qty IS NULL OR l.qty<=0.000001
                 OR ABS(l.qty-m.qty)>0.000001 OR c.id IS NULL OR c.object_id<>sh.object_id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = shipmentId;
        if (cmd.ExecuteScalar() is not null) return message;
        using var count = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.shipment_lines WHERE shipment_id=@id", conn, tx);
        count.Parameters.Add("@id", SqlDbType.BigInt).Value = shipmentId;
        return Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) == 0 ? message : null;
    }

    private static long ObjectIdOf(SqlConnection conn, SqlTransaction tx, long shipmentId)
    {
        using var cmd = new SqlCommand("SELECT object_id FROM dbo.shipments WHERE id=@id", conn, tx);
        cmd.Parameters.AddWithValue("@id", shipmentId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    /// <summary>Delete a clean draft only; unexpected linked movements require reconciliation.</summary>
    public static SqlServerDatabase.SaveResult DeleteList(string connectionString, long shipmentId,
        string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови ведомости.", rev);
        bool fixedList;
        using (var read = new SqlCommand("SELECT fixed FROM dbo.shipments WHERE id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", shipmentId);
            var value = read.ExecuteScalar();
            if (value is null or DBNull) return SqlServerDatabase.SaveResult.Fail("Ведомость не найдена.", rev);
            fixedList = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        if (fixedList)
            return SqlServerDatabase.SaveResult.Fail("Ведомость уже выдана. Сначала нажмите «Отменить выдачу».", rev);
        using (var check = new SqlCommand(
            "SELECT TOP(1) id FROM dbo.movements WHERE shipment_id=@id", conn, tx))
        {
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = shipmentId;
            if (check.ExecuteScalar() is not null)
                return SqlServerDatabase.SaveResult.Fail("У черновика есть складские движения. Удаление запрещено до сверки.", rev);
        }
        var objectId = ObjectIdOf(conn, tx, shipmentId);
        using (var delete = new SqlCommand(
            "DELETE FROM dbo.shipment_lines WHERE shipment_id=@id", conn, tx))
        {
            delete.Parameters.AddWithValue("@id", shipmentId);
            delete.ExecuteNonQuery();
        }
        using (var delete = new SqlCommand(
            "DELETE FROM dbo.shipments WHERE id=@id AND fixed=0", conn, tx))
        {
            delete.Parameters.AddWithValue("@id", shipmentId);
            if (delete.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Ведомость изменилась.");
        }
        using (var log = new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,object_id) " +
            "VALUES (SYSDATETIME(),@user,N'shipment',@id,N'delete',N'Удалён черновик ведомости',@object)", conn, tx))
        {
            log.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = shipmentId;
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>Legacy partial-header writer disabled; use atomic SaveDetails.</summary>
    [Obsolete("Use SaveDetails with the full header and expected fixed state.")]
    public static SqlServerDatabase.SaveResult SaveHeader(string connectionString, long shipmentId,
        string field, string value, string expectedRevision) =>
        SqlServerDatabase.SaveResult.Fail(
            "Старый метод изменения шапки отключён. Сохраните все реквизиты ведомости.", expectedRevision);



    /// <summary>Все реквизиты и связанные движения сохраняются атомарно.
    /// Номер и дата имеют типы и пределы исходной SQL Server схемы Python.</summary>
    public static SqlServerDatabase.SaveResult SaveDetails(string connectionString, long shipmentId,
        string number, DateTime date, string recipient, string issuedBy, bool expectedFixed, string expectedRevision)
    {
        number = number.Trim();
        recipient = recipient.Trim();
        issuedBy = issuedBy.Trim();
        if (number.Length == 0 || number.Length > 32 || recipient.Length > 255 || issuedBy.Length > 255)
            return SqlServerDatabase.SaveResult.Fail("Проверьте номер (1–32 знака), получателя и выдавшего (до 255 знаков).", expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обновите ведомость.", rev);
        long objectId;
        using (var read = new SqlCommand("SELECT object_id,fixed FROM dbo.shipments WHERE id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", shipmentId);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return SqlServerDatabase.SaveResult.Fail("Ведомость не найдена.", rev);
            objectId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture) != expectedFixed)
                return SqlServerDatabase.SaveResult.Fail("Состояние ведомости изменилось.", rev);
        }
        using (var update = new SqlCommand(
            "UPDATE dbo.shipments SET number=@number,[date]=@date,recipient=@recipient,issued_by=@issued WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@number", SqlDbType.NVarChar, 32).Value = number;
            update.Parameters.Add("@date", SqlDbType.Date).Value = date.Date;
            update.Parameters.Add("@recipient", SqlDbType.NVarChar, 255).Value = recipient;
            update.Parameters.Add("@issued", SqlDbType.NVarChar, 255).Value = issuedBy;
            update.Parameters.AddWithValue("@id", shipmentId);
            if (update.ExecuteNonQuery() != 1)
                return SqlServerDatabase.SaveResult.Fail("Не удалось сохранить ведомость.", rev);
        }
        if (expectedFixed)
            using (var sync = new SqlCommand(
                "UPDATE m SET m.[date]=sh.[date],m.doc=N'Ведомость № '+sh.number,m.note=sh.recipient " +
                "FROM dbo.movements m JOIN dbo.shipments sh ON sh.id=m.shipment_id " +
                "WHERE m.shipment_id=@id AND m.kind=N'out'", conn, tx))
            {
                sync.Parameters.AddWithValue("@id", shipmentId);
                sync.ExecuteNonQuery();
            }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,object_id) " +
            "VALUES (@ts,@user,N'shipment',@id,N'edit',@details,@obj)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", shipmentId);
            log.Parameters.AddWithValue("@details", $"Реквизиты: {TitleOf(number)} от {date:dd.MM.yyyy}");
            log.Parameters.AddWithValue("@obj", objectId);
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    internal static decimal LineQty(SqlConnection conn, SqlTransaction tx, long shipmentId, long positionId)
    {
        using var cmd = new SqlCommand(
            "SELECT qty FROM dbo.shipment_lines WHERE shipment_id=@sid AND position_id=@pid", conn, tx);
        cmd.Parameters.AddWithValue("@sid", shipmentId);
        cmd.Parameters.AddWithValue("@pid", positionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return 0m;
        var qty = reader.IsDBNull(0) ? 0m : Money.FromDouble(Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture));
        if (reader.Read() || qty <= Eps)
            throw new InvalidOperationException("Повторные строки или неверное количество ведомости.");
        return qty;
    }

    internal static void CheckLinkedLine(SqlConnection conn, SqlTransaction tx, long shipmentId,
        long positionId, bool fixedList, decimal oldQty)
    {
        using var cmd = new SqlCommand(
            "SELECT kind,qty FROM dbo.movements WHERE shipment_id=@sid AND position_id=@pid", conn, tx);
        cmd.Parameters.Add("@sid", SqlDbType.BigInt).Value = shipmentId;
        cmd.Parameters.Add("@pid", SqlDbType.BigInt).Value = positionId;
        using var reader = cmd.ExecuteReader();
        var expectsMovement = fixedList && oldQty > Eps;
        if (!reader.Read())
        {
            if (!expectsMovement) return;
        }
        else if (expectsMovement && !reader.IsDBNull(0) && reader.GetString(0) == "out" &&
            !reader.IsDBNull(1) &&
            Math.Abs(Money.FromDouble(Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture)) - oldQty) <= Eps &&
            !reader.Read()) return;
        throw new InvalidOperationException("Строка ведомости не соответствует движениям. Изменение отменено; требуется сверка.");
    }

    private static decimal QtyByKp(SqlConnection conn, SqlTransaction tx, long positionId)
    {
        using var cmd = new SqlCommand("SELECT qty FROM dbo.positions WHERE id=@id", conn, tx);
        cmd.Parameters.AddWithValue("@id", positionId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0m : Money.FromDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
    }

    private static string PositionName(SqlConnection conn, SqlTransaction tx, long positionId)
    {
        using var cmd = new SqlCommand("SELECT name FROM dbo.positions WHERE id=@id", conn, tx);
        cmd.Parameters.AddWithValue("@id", positionId);
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>Фиксированная ведомость держит закупку в соответствии: движение «выдано»
    /// следует за строкой (shipments._sync_line).</summary>
    internal static void SyncFixedLine(SqlConnection conn, SqlTransaction tx, long shipmentId,
        long positionId, decimal qty)
    {
        // Keep the original movement identity and author on quantity edits (Python _sync_line).
        if (qty <= Eps)
        {
            using var delete = new SqlCommand(
                "DELETE FROM dbo.movements WHERE shipment_id=@sid AND position_id=@pid AND kind=N'out'", conn, tx);
            delete.Parameters.AddWithValue("@sid", shipmentId);
            delete.Parameters.AddWithValue("@pid", positionId);
            delete.ExecuteNonQuery();
            return;
        }
        using (var update = new SqlCommand(
            "UPDATE dbo.movements SET qty=@qty WHERE shipment_id=@sid AND position_id=@pid AND kind=N'out'", conn, tx))
        {
            update.Parameters.AddWithValue("@sid", shipmentId);
            update.Parameters.AddWithValue("@pid", positionId);
            update.Parameters.AddWithValue("@qty", (double)qty);
            var affected = update.ExecuteNonQuery();
            if (affected == 1) return;
            if (affected != 0) throw new InvalidOperationException("Обнаружены повторные движения ведомости.");
        }
        using (var insert = new SqlCommand(
            "INSERT dbo.movements (position_id,kind,qty,[date],doc,note,[user],shipment_id) " +
            "SELECT @pid,N'out',@qty,sh.[date],N'Ведомость № '+sh.number,ISNULL(sh.recipient,N'')," +
            "@user,sh.id FROM dbo.shipments sh WHERE sh.id=@sid", conn, tx))
        {
            insert.Parameters.AddWithValue("@pid", positionId);
            insert.Parameters.AddWithValue("@qty", (double)qty);
            insert.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            insert.Parameters.AddWithValue("@sid", shipmentId);
            if (insert.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Не удалось синхронизировать движение ведомости.");
        }
    }

    private static void Execute(SqlConnection conn, SqlTransaction tx, string sql,
        long positionId, long shipmentId)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@pid", positionId);
        cmd.Parameters.AddWithValue("@sid", shipmentId);
        cmd.ExecuteNonQuery();
    }

    private static void RequirePilotWrite(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.InitialCatalog.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Запись C# разрешена только в отдельную тестовую базу GmcSmetaPilot_*. Рабочая база не изменена.");
    }
}
