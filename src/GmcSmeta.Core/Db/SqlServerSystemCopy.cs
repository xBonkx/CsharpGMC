using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Копия раздела «П2 like П1» (builder.copy_system): раздел копируется со всеми
/// позициями и подразделами; количества правятся потом. Подраздел копируется как
/// подраздел той же системы. suggest_system_name: «П1» → «П2» (первый свободный номер).
/// </summary>
public static class SqlServerSystemCopy
{
    private static readonly Regex NumberTail = new("^(.*?)(\\d+)(\\D*)$", RegexOptions.Compiled);

    private static void RequirePilotWrite(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!builder.InitialCatalog.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Запись C# разрешена только в отдельную тестовую базу GmcSmetaPilot_*. Рабочая база не изменена.");
    }

    /// <summary>«П1» → «П2» (первый свободный номер); иначе «имя (копия)».</summary>
    public static string SuggestName(string name, IReadOnlyList<string> existing)
    {
        var match = NumberTail.Match((name ?? "").Trim());
        if (!match.Success) return $"{name} (копия)";
        var used = existing.Select(x => x.Trim()).ToHashSet();
        var n = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) + 1;
        string candidate;
        do
        {
            candidate = $"{match.Groups[1].Value}{n}{match.Groups[3].Value}";
            n++;
        }
        while (used.Contains(candidate));
        return candidate;
    }

    /// <summary>Скопировать раздел со всеми позициями и подразделами (builder.copy_system).</summary>
    public static SqlServerDatabase.SaveResult CopySystem(string connectionString, long contractId,
        long sourceSystemId, string newName, string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови смету.", rev);

        // UI permissions are not authoritative: recheck the destination under the same transaction.
        using (var document = new SqlCommand(
            "SELECT c.locked,o.archived FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id WHERE c.id=@id", conn, tx))
        {
            document.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var reader = document.ExecuteReader();
            if (!reader.Read())
                return SqlServerDatabase.SaveResult.Fail("Документ не найден.", rev);
            if (Convert.ToBoolean(reader.GetValue(0), CultureInfo.InvariantCulture) ||
                Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture))
                return SqlServerDatabase.SaveResult.Fail("Подписанный или архивный документ нельзя менять.", rev);
        }

        var (srcId, srcParent, srcOrder, srcName, srcLabel, srcSmr) = ReadSystem(conn, tx, sourceSystemId, contractId);
        if (srcId < 0) return SqlServerDatabase.SaveResult.Fail("Раздел не найден в этом документе.", rev);

        long newId;
        if (srcParent is not null)
        {
            // Подраздел → новый подраздел той же системы, сразу после источника.
            newId = InsertSystem(conn, tx, contractId, srcParent, newName, srcOrder + 1, srcSmr);
        }
        else
        {
            // Верхний раздел → новый верхний раздел сразу после источника (с уплотнением).
            ShiftOrders(conn, tx, contractId, srcOrder + 1, 1 + SubCount(conn, tx, sourceSystemId));
            newId = InsertSystem(conn, tx, contractId, null, newName, srcOrder + 1, srcSmr);
            foreach (var (subId, subOrder, subName, subSmr, _) in SubsectionsOf(conn, tx, sourceSystemId))
            {
                var subCopy = InsertSystem(conn, tx, contractId, newId, subName, subOrder, subSmr);
                CopyPositions(conn, tx, subId, subCopy);
            }
        }
        CopyPositions(conn, tx, sourceSystemId, newId);

        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (@ts,@user,N'system',@id,N'copy',@details,@contract)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", newId);
            log.Parameters.AddWithValue("@details", $"«{srcLabel}» → «{newName}»");
            log.Parameters.AddWithValue("@contract", contractId);
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    // ---------------------------------------------------------------- внутреннее

    private static (long Id, long? Parent, int Order, string Name, string Label, decimal? Smr) ReadSystem(
        SqlConnection conn, SqlTransaction tx, long systemId, long contractId)
    {
        using var cmd = new SqlCommand(
            "SELECT sy.id, sy.parent_id, sy.[order], sy.name, sy.smr_amount, c.name " +
            "FROM dbo.systems sy JOIN dbo.contracts c ON c.id=sy.contract_id " +
            "WHERE sy.id=@id AND sy.contract_id=@cid", conn, tx);
        cmd.Parameters.AddWithValue("@id", systemId);
        cmd.Parameters.AddWithValue("@cid", contractId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (-1, null, 0, "", "", null);
        var name = r.IsDBNull(3) ? "" : r.GetString(3);
        var parent = r.IsDBNull(1) ? (long?)null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
        var label = parent is not null ? "" : name;
        return (
            Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
            parent,
            Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture),
            name,
            parent is null ? name : name, // label уточняется родителем в окне; для журнала достаточно имени
            r.IsDBNull(4) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture)));
    }

    private static List<(long Id, int Order, string Name, decimal? Smr, string Label)> SubsectionsOf(
        SqlConnection conn, SqlTransaction tx, long systemId)
    {
        var list = new List<(long, int, string, decimal?, string)>();
        using var cmd = new SqlCommand(
            "SELECT id,[order],name,smr_amount FROM dbo.systems WHERE parent_id=@id ORDER BY [order],id", conn, tx);
        cmd.Parameters.AddWithValue("@id", systemId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((
                Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture)),
                ""));
        return list;
    }

    private static int SubCount(SqlConnection conn, SqlTransaction tx, long systemId)
    {
        using var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.systems WHERE parent_id=@id", conn, tx);
        cmd.Parameters.AddWithValue("@id", systemId);
        return (int)Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static void ShiftOrders(SqlConnection conn, SqlTransaction tx, long contractId,
        int fromOrder, int count)
    {
        using var cmd = new SqlCommand(
            "UPDATE dbo.systems SET [order]=[order]+@count " +
            "WHERE contract_id=@cid AND parent_id IS NULL AND [order]>=@from", conn, tx);
        cmd.Parameters.AddWithValue("@count", count);
        cmd.Parameters.AddWithValue("@cid", contractId);
        cmd.Parameters.AddWithValue("@from", fromOrder);
        cmd.ExecuteNonQuery();
    }

    private static long InsertSystem(SqlConnection conn, SqlTransaction tx, long contractId,
        long? parentId, string name, int order, decimal? smrAmount)
    {
        using var insert = new SqlCommand(
            "INSERT dbo.systems (contract_id,parent_id,name,[order],smr_amount,heading,source_num) " +
            "OUTPUT INSERTED.id VALUES (@cid,@parent,@name,@order,@smr,N'',N'')", conn, tx);
        insert.Parameters.AddWithValue("@cid", contractId);
        insert.Parameters.AddWithValue("@parent", parentId is null ? DBNull.Value : parentId.Value);
        insert.Parameters.AddWithValue("@name", name);
        insert.Parameters.AddWithValue("@order", order);
        insert.Parameters.AddWithValue("@smr", smrAmount is null ? DBNull.Value : (object)(double)smrAmount.Value);
        return Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Позиции и комплектующие источника → новый раздел (ссылки parent локальны).</summary>
    private static void CopyPositions(SqlConnection conn, SqlTransaction tx, long srcSystemId, long dstSystemId)
    {
        var rows = new List<(long Id, long? ParentId, decimal? WorkCost, decimal? WorkSell, decimal? Purchase)>();
        using (var read = new SqlCommand(
            "SELECT id, parent_id, qty, work_cost, work_sell, purchase_price FROM dbo.positions " +
            "WHERE system_id=@sys ORDER BY [order], id", conn, tx))
        {
            read.Parameters.AddWithValue("@sys", srcSystemId);
            using var r = read.ExecuteReader();
            while (r.Read())
                rows.Add((
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    r.IsDBNull(1) ? null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                    r.IsDBNull(3) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture)),
                    r.IsDBNull(4) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture)),
                    r.IsDBNull(5) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture))));
        }
        var made = new Dictionary<long, long>();
        // Верхние строки, затем дети (двумя проходами: ссылки parent локальны).
        foreach (var pass in new[] { 0, 1 })
            foreach (var row in rows)
            {
                if (pass == 0 && row.ParentId is not null) continue;
                if (pass == 1 && (row.ParentId is null || !made.ContainsKey(row.ParentId.Value))) continue;
                using var insert = new SqlCommand(
                    @"INSERT dbo.positions (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,
                       purchase_price,note,extra_json,source_row,[order],out_of_kp,formulas,area_manual,
                       work_price,cost_note,cost_color,work_sell,work_cost)
                      SELECT @dst,
                             CASE WHEN p.parent_id IS NULL THEN NULL ELSE @mappedParent END,
                             p.catalog_item_id,p.num,p.basis,p.name,p.unit,p.qty,p.price,p.purchase_price,
                             p.note,p.extra_json,NULL,p.[order],p.out_of_kp,p.formulas,p.area_manual,
                             p.work_price,p.cost_note,p.cost_color,p.work_sell,p.work_cost
                      FROM dbo.positions p WHERE p.id=@src
                      OUTPUT INSERTED.id", conn, tx);
                insert.Parameters.AddWithValue("@dst", dstSystemId);
                insert.Parameters.AddWithValue("@src", row.Id);
                insert.Parameters.AddWithValue("@mappedParent",
                    pass == 0 ? DBNull.Value : made[row.ParentId!.Value]);
                var newId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
                made[row.Id] = newId;
            }
    }
}
