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

    public sealed record CopyResult(SqlServerDatabase.SaveResult Save, long? SystemId);

    /// <summary>«П1» → «П2» (первый свободный номер); иначе «имя (копия)».</summary>
    public static string SuggestName(string name, IReadOnlyList<string> existing)
    {
        var match = NumberTail.Match((name ?? "").Trim());
        if (!match.Success) return $"{name} (копия)";
        var used = existing.Select(x => x.Trim()).ToHashSet();
        if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var previous) ||
            previous == int.MaxValue) return $"{name} (копия)";
        var n = previous + 1;
        string candidate;
        do
        {
            candidate = $"{match.Groups[1].Value}{n}{match.Groups[3].Value}";
            if (n == int.MaxValue && used.Contains(candidate)) return $"{name} (копия)";
            if (n < int.MaxValue) n++;
        }
        while (used.Contains(candidate));
        return candidate;
    }

    /// <summary>Скопировать раздел со всеми позициями и подразделами (builder.copy_system).</summary>
    public static SqlServerDatabase.SaveResult CopySystem(string connectionString, long contractId,
        long sourceSystemId, string newName, string expectedRevision) =>
        CopySystemTo(connectionString, contractId, sourceSystemId, contractId, newName, expectedRevision).Save;

    public static CopyResult CopySystemTo(string connectionString, long sourceContractId,
        long sourceSystemId, long targetContractId, string newName, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        newName = (newName ?? "").Trim();
        if (sourceContractId <= 0 || targetContractId <= 0 || sourceSystemId <= 0 ||
            newName.Length is < 1 or > 255 || string.IsNullOrWhiteSpace(expectedRevision))
            return new(SqlServerDatabase.SaveResult.Fail("Некорректная система или название.", expectedRevision), null);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        CopyResult Fail(string message) => new(SqlServerDatabase.SaveResult.Fail(message, rev ?? expectedRevision), null);
        if (string.IsNullOrWhiteSpace(rev) || !string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return Fail("Данные изменились. Обновите смету.");

        // One database and one object only. Destination stages are managed by
        // their allocation editor, not by inserting unlinked copied positions.
        using (var document = new SqlCommand(
            "SELECT target.id FROM dbo.contracts target WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.contracts source WITH (HOLDLOCK) ON source.object_id=target.object_id " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=target.object_id " +
            "WHERE target.id=@target AND source.id=@source AND ISNULL(o.archived,0)=0 " +
            "AND ISNULL(target.locked,0)=0 AND target.stage_of IS NULL AND ISNULL(target.stage_rest,0)=0 " +
            "AND ISNULL(source.stage_rest,0)=0 AND target.kind IN (N'kp',N'main',N'dop')", conn, tx))
        {
            document.Parameters.Add("@target", SqlDbType.BigInt).Value = targetContractId;
            document.Parameters.Add("@source", SqlDbType.BigInt).Value = sourceContractId;
            if (document.ExecuteScalar() is null)
                return Fail("Назначение недоступно: другой объект, архив, подпись или этап.");
        }

        var (srcId, srcParent, _, _, srcLabel, srcSmr) = ReadSystem(conn, tx, sourceSystemId, sourceContractId);
        if (srcId < 0) return Fail("Исходный раздел не найден в выбранном документе.");
        var same = sourceContractId == targetContractId;
        var subsection = same && srcParent is not null;
        var existing = ReadSectionOrder(conn, tx, targetContractId);
        var children = srcParent is null ? SubsectionsOf(conn, tx, sourceSystemId, sourceContractId) : new();
        var newId = InsertSystem(conn, tx, targetContractId, subsection ? srcParent : null,
            newName, 0, srcSmr);
        CopyPositions(conn, tx, sourceSystemId, newId);
        var newChildren = new List<long>();
        foreach (var (subId, _, subName, subSmr, _) in children)
        {
            var subCopy = InsertSystem(conn, tx, targetContractId, newId, subName, 0, subSmr);
            newChildren.Add(subCopy);
            CopyPositions(conn, tx, subId, subCopy);
        }
        var order = SystemCopySource.InsertCopyOrder(existing, same ? sourceSystemId : null,
            newId, newChildren, subsection);
        using (var update = new SqlCommand(
            "UPDATE dbo.systems SET [order]=@order WHERE id=@id AND contract_id=@contract", conn, tx))
        {
            update.Parameters.Add("@contract", SqlDbType.BigInt).Value = targetContractId;
            var orderParam = update.Parameters.Add("@order", SqlDbType.Int);
            var idParam = update.Parameters.Add("@id", SqlDbType.BigInt);
            for (var i = 0; i < order.Count; i++)
            {
                orderParam.Value = i;
                idParam.Value = order[i];
                if (update.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Раздел изменился при пересортировке.");
            }
        }

        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'system',@id,N'copy',@details,@contract,(SELECT object_id FROM dbo.contracts WHERE id=@contract))", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", newId);
            log.Parameters.AddWithValue("@details", $"«{srcLabel}» → «{newName}» (из документа #{sourceContractId})");
            log.Parameters.AddWithValue("@contract", targetContractId);
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new(new SqlServerDatabase.SaveResult(true, null, updated), newId);
    }

    private static List<SystemCopySource.Section> ReadSectionOrder(SqlConnection conn, SqlTransaction tx, long contractId)
    {
        var rows = new List<SystemCopySource.Section>();
        using var cmd = new SqlCommand(
            "SELECT id,parent_id FROM dbo.systems WITH (UPDLOCK,HOLDLOCK) WHERE contract_id=@id ORDER BY [order],id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                reader.IsDBNull(1) ? null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)));
        return rows;
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
        var parent = r.IsDBNull(1) ? (long?)null : SqlInteger.ReadInt64(r, 1);
        var label = parent is not null ? "" : name;
        return (
            SqlInteger.ReadInt64(r, 0),
            parent,
            SqlInteger.ReadInt32(r, 2),
            name,
            parent is null ? name : name, // label уточняется родителем в окне; для журнала достаточно имени
            r.IsDBNull(4) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture)));
    }

    private static List<(long Id, int Order, string Name, decimal? Smr, string Label)> SubsectionsOf(
        SqlConnection conn, SqlTransaction tx, long systemId, long contractId)
    {
        var list = new List<(long, int, string, decimal?, string)>();
        using var cmd = new SqlCommand(
            "SELECT id,[order],name,smr_amount FROM dbo.systems WHERE parent_id=@id AND contract_id=@contract ORDER BY [order],id", conn, tx);
        cmd.Parameters.AddWithValue("@id", systemId);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((
                SqlInteger.ReadInt64(r, 0),
                SqlInteger.ReadInt32(r, 1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture)),
                ""));
        return list;
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
        var rows = new List<(long Id, long? ParentId, bool Outside)>();
        using (var read = new SqlCommand(
            "SELECT id,parent_id,ISNULL(out_of_kp,0) FROM dbo.positions " +
            "WHERE system_id=@sys ORDER BY [order],id", conn, tx))
        {
            read.Parameters.Add("@sys", SqlDbType.BigInt).Value = srcSystemId;
            using var r = read.ExecuteReader();
            while (r.Read())
                rows.Add((SqlInteger.ReadInt64(r, 0),
                    r.IsDBNull(1) ? null : SqlInteger.ReadInt64(r, 1),
                    SqlFlag.Read(r, 2)));
        }
        var roots = rows.Where(r => r.ParentId is null).ToDictionary(r => r.Id);
        if (rows.Any(r => r.ParentId is long parent && !roots.ContainsKey(parent)))
            throw new InvalidOperationException("Нарушена структура комплектующих исходной системы.");
        var made = new Dictionary<long, long>();
        // Copy only estimate rows, not purchase-only extras or their children.
        // Preserve specification source_row, formulas and all Python copy fields.
        foreach (var row in rows.Where(r => r.ParentId is null && !r.Outside)
            .Concat(rows.Where(r => r.ParentId is long parent && !r.Outside && !roots[parent].Outside)))
        {
            using var insert = new SqlCommand(
                @"INSERT dbo.positions (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,
                   purchase_price,note,extra_json,source_row,[order],out_of_kp,formulas,area_manual,
                   work_price,cost_note,cost_color,work_sell,work_cost)
                  OUTPUT INSERTED.id
                  SELECT @dst,@mappedParent,
                         p.catalog_item_id,p.num,p.basis,p.name,p.unit,p.qty,p.price,p.purchase_price,
                         p.note,p.extra_json,p.source_row,p.[order],0,p.formulas,p.area_manual,
                         p.work_price,p.cost_note,p.cost_color,p.work_sell,p.work_cost
                  FROM dbo.positions p WHERE p.id=@src AND p.system_id=@source", conn, tx);
            insert.Parameters.Add("@dst", SqlDbType.BigInt).Value = dstSystemId;
            insert.Parameters.Add("@src", SqlDbType.BigInt).Value = row.Id;
            insert.Parameters.Add("@source", SqlDbType.BigInt).Value = srcSystemId;
            insert.Parameters.Add("@mappedParent", SqlDbType.BigInt).Value =
                row.ParentId is long parentId ? (object)made[parentId] : DBNull.Value;
            var inserted = insert.ExecuteScalar();
            if (inserted is null or DBNull) throw new DBConcurrencyException("Исходная позиция исчезла.");
            made[row.Id] = Convert.ToInt64(inserted, CultureInfo.InvariantCulture);
        }
    }
}
