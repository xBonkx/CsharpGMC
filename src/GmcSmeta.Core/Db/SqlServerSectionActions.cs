using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// SQL Server-only operations on entire sections and position groups. No SQLite fallback.
/// The lock order is always revision -> contract -> sections -> positions.
/// </summary>
public static partial class SqlServerDatabase
{
    private static List<SectionEntry> LoadContractSections(SqlConnection connection, SqlTransaction transaction, long contractId)
    {
        var result = new List<SectionEntry>();
        using var cmd = new SqlCommand(
            "SELECT id,parent_id,[order],name FROM dbo.systems WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE contract_id=@contract ORDER BY [order],id", connection, transaction);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new SectionEntry(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                r.IsDBNull(1) ? null : Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture), r.GetString(3)));
        return result;
    }

    private static List<SectionEntry> FlattenSectionGroups(IReadOnlyList<SectionEntry> entries)
    {
        var roots = entries.Where(s => s.ParentId is null).OrderBy(s => s.Order).ThenBy(s => s.Id).ToList();
        var children = entries.Where(s => s.ParentId is not null)
            .GroupBy(s => s.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Order).ThenBy(s => s.Id).ToList());
        var result = new List<SectionEntry>(entries.Count);
        foreach (var root in roots)
        {
            result.Add(root);
            if (children.TryGetValue(root.Id, out var subs)) result.AddRange(subs);
        }
        if (result.Count != entries.Count)
            throw new InvalidOperationException("Некорректные связи подразделов; операция отменена.");
        return result;
    }

    private static void RenumberContractSections(SqlConnection conn, SqlTransaction tx, long contractId,
        IReadOnlyList<SectionEntry> sections)
    {
        using (var create = new SqlCommand("CREATE TABLE #new_section_order (id BIGINT PRIMARY KEY, ord INT NOT NULL)", conn, tx))
            create.ExecuteNonQuery();
        for (var offset = 0; offset < sections.Count; offset += 250)
        {
            var chunk = sections.Skip(offset).Take(250).ToList();
            using var insert = new SqlCommand { Connection = conn, Transaction = tx };
            var values = new List<string>();
            for (var i = 0; i < chunk.Count; i++)
            {
                var key = i.ToString(CultureInfo.InvariantCulture);
                values.Add($"(@id{key},@ord{key})");
                insert.Parameters.Add($"@id{key}", SqlDbType.BigInt).Value = chunk[i].Id;
                insert.Parameters.Add($"@ord{key}", SqlDbType.Int).Value = offset + i;
            }
            insert.CommandText = "INSERT INTO #new_section_order(id,ord) VALUES " + string.Join(",", values);
            insert.ExecuteNonQuery();
        }
        using var update = new SqlCommand(
            "UPDATE sy SET [order]=s.ord FROM dbo.systems sy " +
            "JOIN #new_section_order s ON s.id=sy.id WHERE sy.contract_id=@contract", conn, tx);
        update.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        if (update.ExecuteNonQuery() != sections.Count)
            throw new DBConcurrencyException("Порядок разделов изменился во время сохранения.");
    }

    private static void CreateIdTable(SqlConnection conn, SqlTransaction tx, string name, IEnumerable<long> sourceIds)
    {
        // name comes exclusively from hard-coded call sites; never from user data.
        using (var create = new SqlCommand($"CREATE TABLE {name}(id BIGINT NOT NULL PRIMARY KEY)", conn, tx))
            create.ExecuteNonQuery();
        var ids = sourceIds.Distinct().Order().ToList();
        for (var offset = 0; offset < ids.Count; offset += 250)
        {
            var chunk = ids.Skip(offset).Take(250).ToList();
            using var insert = new SqlCommand { Connection = conn, Transaction = tx };
            var placeholders = new List<string>();
            for (var i = 0; i < chunk.Count; i++)
            {
                placeholders.Add($"(@p{i})");
                insert.Parameters.Add($"@p{i}", SqlDbType.BigInt).Value = chunk[i];
            }
            insert.CommandText = $"INSERT INTO {name}(id) VALUES " + string.Join(",", placeholders);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Delete a subsection, or a root together with its direct subsections and every position.
    /// Reject even unsigned KS-2 references and all committed/fixed stock issues.
    /// </summary>
    public static SaveResult DeleteSection(string connectionString, long contractId, long sectionId,
        string expectedRevision)
    {
        if (contractId <= 0 || sectionId <= 0)
            return SaveResult.Fail("Не выбран раздел.", expectedRevision);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = LockRevision(conn, tx);
        if (rev is null) return SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Смета изменена другим сотрудником. Обнови данные.", rev);
        var contract = LoadPositionContract(conn, tx, contractId);
        if (contract is null) return SaveResult.Fail("Документ не найден.", rev);
        if (contract.Value.Locked) return SaveResult.Fail("Подписанный документ изменять нельзя.", rev);
        var sections = LoadContractSections(conn, tx, contractId);
        var chosen = sections.FirstOrDefault(s => s.Id == sectionId);
        if (chosen is null) return SaveResult.Fail("Раздел не принадлежит этому документу.", rev);
        FlattenSectionGroups(sections); // prevent deleting an inconsistent tree
        var removed = sections.Where(s => s.Id == sectionId || s.ParentId == sectionId).ToList();
        CreateIdTable(conn, tx, "#doomed_sections", removed.Select(s => s.Id));
        using (var create = new SqlCommand(
            "SELECT p.id INTO #doomed_rows FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #doomed_sections s ON s.id=p.system_id; " +
            "CREATE UNIQUE CLUSTERED INDEX IX_doomed_rows ON #doomed_rows(id)", conn, tx))
            create.ExecuteNonQuery();

        using (var check = new SqlCommand(
            "SELECT TOP(1) p.name FROM dbo.act_lines al WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #doomed_rows r ON r.id=al.position_id JOIN dbo.positions p ON p.id=r.id", conn, tx))
        {
            var rowName = check.ExecuteScalar();
            if (rowName is not null)
                return SaveResult.Fail($"Нельзя удалить раздел: позиция «{rowName}» присутствует в КС-2.", rev);
        }
        using (var check = new SqlCommand(
            "SELECT TOP(1) m.id FROM dbo.movements m WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #doomed_rows r ON r.id=m.position_id WHERE m.shipment_id IS NOT NULL", conn, tx))
            if (check.ExecuteScalar() is not null)
                return SaveResult.Fail("Нельзя удалить раздел с проведённой выдачей.", rev);
        using (var check = new SqlCommand(
            "SELECT TOP(1) sl.id FROM dbo.shipment_lines sl WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN #doomed_rows r ON r.id=sl.position_id " +
            "JOIN dbo.shipments sh ON sh.id=sl.shipment_id WHERE sh.fixed=1", conn, tx))
            if (check.ExecuteScalar() is not null)
                return SaveResult.Fail("Нельзя удалить раздел с закреплённой ведомостью выдачи.", rev);

        foreach (var table in new[] { "procurement", "movements", "shipment_lines" })
        {
            using var dep = new SqlCommand(
                $"DELETE t FROM dbo.[{table}] t JOIN #doomed_rows r ON r.id=t.position_id", conn, tx);
            dep.ExecuteNonQuery();
        }
        using (var children = new SqlCommand(
            "DELETE p FROM dbo.positions p JOIN #doomed_rows r ON r.id=p.id " +
            "WHERE p.parent_id IS NOT NULL", conn, tx))
            children.ExecuteNonQuery();
        using (var roots = new SqlCommand(
            "DELETE p FROM dbo.positions p JOIN #doomed_rows r ON r.id=p.id " +
            "WHERE p.parent_id IS NULL", conn, tx))
            roots.ExecuteNonQuery();
        // Remove subsections first (SQL Server parent FK is NO ACTION).
        using (var subs = new SqlCommand(
            "DELETE sy FROM dbo.systems sy JOIN #doomed_sections s ON s.id=sy.id " +
            "WHERE sy.parent_id IS NOT NULL AND sy.contract_id=@contract", conn, tx))
        {
            subs.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            subs.ExecuteNonQuery();
        }
        using (var root = new SqlCommand(
            "DELETE sy FROM dbo.systems sy JOIN #doomed_sections s ON s.id=sy.id " +
            "WHERE sy.parent_id IS NULL AND sy.contract_id=@contract", conn, tx))
        {
            root.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            root.ExecuteNonQuery();
        }
        RenumberContractSections(conn, tx, contractId,
            FlattenSectionGroups(sections.Where(s => !removed.Any(d => d.Id == s.Id)).ToList()));
        LogSectionChange(conn, tx, sectionId, contractId, contract.Value.ObjectId, "delete",
            $"{contract.Value.DocumentName}: {chosen.Name} (включая подразделы: {removed.Count - 1})");
        var newRev = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, newRev);
    }

    private static void LogSectionChange(SqlConnection conn, SqlTransaction tx, long sectionId,
        long contractId, long objectId, string action, string details)
    {
        using var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'system',@id,@action,@details,@contract,@object)", conn, tx);
        log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
        log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
        log.Parameters.Add("@id", SqlDbType.BigInt).Value = sectionId;
        log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
        log.Parameters.Add("@action", SqlDbType.NVarChar, 32).Value = action;
        log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        log.ExecuteNonQuery();
    }

    /// <summary>
    /// Move a root position and its components to the end of another section of the SAME contract.
    /// IDs are preserved so procurement/history/acts stay bound to the existing positions.
    /// A standalone component must never be moved to another section.
    /// </summary>
    public static SaveResult MovePositionToSection(string connectionString, long contractId,
        long sourceSectionId, long targetSectionId, long rootPositionId, string expectedRevision)
    {
        if (contractId <= 0 || sourceSectionId <= 0 || targetSectionId <= 0 || rootPositionId <= 0)
            return SaveResult.Fail("Не выбран раздел или позиция.", expectedRevision);
        if (sourceSectionId == targetSectionId)
            return SaveResult.Fail("Выбери другой раздел.", expectedRevision);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = LockRevision(conn, tx);
        if (rev is null) return SaveResult.Fail("Нет meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Данные изменились на сервере. Обнови смету.", rev);
        var contract = LoadPositionContract(conn, tx, contractId);
        if (contract is null) return SaveResult.Fail("Документ не найден.", rev);
        if (contract.Value.Locked) return SaveResult.Fail("Подписанную смету нельзя редактировать.", rev);
        var sections = LoadContractSections(conn, tx, contractId);
        var from = sections.FirstOrDefault(s => s.Id == sourceSectionId);
        var to = sections.FirstOrDefault(s => s.Id == targetSectionId);
        if (from is null || to is null) return SaveResult.Fail("Раздел не принадлежит документу.", rev);
        var original = TreeOrder(LoadOrderedPositions(conn, tx, contractId, sourceSectionId));
        var moving = original.FirstOrDefault(p => p.Id == rootPositionId);
        if (moving is null || moving.ParentId is not null)
            return SaveResult.Fail("Переносить можно только обычную позицию с комплектующими.", rev);
        var group = original.Where(p => p.Id == rootPositionId || p.ParentId == rootPositionId).ToList();
        var destination = TreeOrder(LoadOrderedPositions(conn, tx, contractId, targetSectionId));
        var sourceRemaining = original.Where(p => p.Id != rootPositionId && p.ParentId != rootPositionId).ToList();
        var destinationNew = destination.Concat(group).ToList();

        // Update all members together. Invariant: parent and children always end in the same section.
        CreateIdTable(conn, tx, "#transfer_positions", group.Select(p => p.Id));
        using (var update = new SqlCommand(
            "UPDATE p SET system_id=@to FROM dbo.positions p " +
            "JOIN #transfer_positions r ON r.id=p.id WHERE p.system_id=@from", conn, tx))
        {
            update.Parameters.Add("@to", SqlDbType.BigInt).Value = targetSectionId;
            update.Parameters.Add("@from", SqlDbType.BigInt).Value = sourceSectionId;
            if (update.ExecuteNonQuery() != group.Count)
                throw new DBConcurrencyException("Состав комплектующих изменился во время переноса.");
        }
        WritePositionOrder(conn, tx, sourceSectionId, sourceRemaining);
        // WritePositionOrder uses a session temp table; drop it before its next use in this transaction.
        using (var drop = new SqlCommand("DROP TABLE #position_order", conn, tx)) drop.ExecuteNonQuery();
        WritePositionOrder(conn, tx, targetSectionId, destinationNew);
        LogPositionChange(conn, tx, rootPositionId, contractId, contract.Value.ObjectId,
            "move", $"{moving.Name[..Math.Min(60, moving.Name.Length)]}: {from.Name} → {to.Name}");
        var updated = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, updated);
    }
}
