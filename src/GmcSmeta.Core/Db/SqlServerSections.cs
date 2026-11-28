using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Server-side equivalent of builder.add_system / add_subsection. The *entire* change
/// (ordering, history, revision) is atomic; a competing editor must reload first.
/// </summary>
public static partial class SqlServerDatabase
{
    public sealed record InsertSectionResult(bool Saved, long? SystemId, string? Error, string Revision)
    {
        public static InsertSectionResult Fail(string error, string revision) =>
            new(false, null, error, revision);
    }

    private sealed record SectionEntry(long Id, long? ParentId, int Order, string Name);

    /// <param name="anchorSystemId">For a top-level section, insert after its whole group.
    /// For a subsection, identifies the parent group and, when itself a subsection, the position.</param>
    public static InsertSectionResult AddSection(string connectionString, long contractId,
        string name, bool subsection, long? anchorSystemId, string expectedRevision)
    {
        var cleanName = (name ?? "").Trim();
        if (cleanName.Length == 0) return InsertSectionResult.Fail("Название раздела не может быть пустым.", expectedRevision);
        if (cleanName.Length > 255) return InsertSectionResult.Fail("Название раздела длиннее 255 символов.", expectedRevision);
        if (contractId <= 0) return InsertSectionResult.Fail("Не указан документ.", expectedRevision);
        if (subsection && anchorSystemId is null)
            return InsertSectionResult.Fail("Для подраздела выбери существующий раздел.", expectedRevision);

        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);

        // Consistent lock order with SaveCell and AddPosition: meta.rev -> contract -> sections.
        using var rev = new SqlCommand(
            "SELECT [value] FROM dbo.meta WITH (UPDLOCK,HOLDLOCK) WHERE [key]=N'rev'", conn, tx);
        var currentRev = Convert.ToString(rev.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (currentRev is null)
            return InsertSectionResult.Fail("На сервере отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(currentRev, expectedRevision, StringComparison.Ordinal))
            return InsertSectionResult.Fail("Документ на сервере изменился. Обнови смету.", currentRev);

        long objectId;
        string title;
        using (var doc = new SqlCommand(
            "SELECT c.locked,c.object_id,c.kind,c.number,c.[date],c.name FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id " +
            "WHERE c.id=@contract AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0", conn, tx))
        {
            doc.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var r = doc.ExecuteReader();
            if (!r.Read()) return InsertSectionResult.Fail("Документ не найден.", currentRev);
            if (SqlFlag.Read(r, 0))
                return InsertSectionResult.Fail("Подписанную смету менять нельзя.", currentRev);
            objectId = SqlInteger.ReadInt64(r, 1);
            title = Documents.ContractTitleText(
                r.IsDBNull(2) ? "kp" : r.GetString(2),
                r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? "" : Convert.ToString(r.GetValue(4), CultureInfo.InvariantCulture) ?? "",
                r.IsDBNull(5) ? "" : r.GetString(5));
        }

        var original = new List<SectionEntry>();
        using (var list = new SqlCommand(
            "SELECT id,parent_id,[order],name FROM dbo.systems WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE contract_id=@contract ORDER BY [order],id", conn, tx))
        {
            list.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var r = list.ExecuteReader();
            while (r.Read()) original.Add(new SectionEntry(
                SqlInteger.ReadInt64(r, 0),
                r.IsDBNull(1) ? null : SqlInteger.ReadInt64(r, 1),
                SqlInteger.ReadInt32(r, 2),
                r.IsDBNull(3) ? "" : r.GetString(3)));
        }

        // Reconstruct the same group order as builder._groups (parent followed by its children).
        var groups = original.Where(s => s.ParentId is null)
            .Select(root => new List<SectionEntry> { root }).ToList();
        foreach (var entry in original.Where(s => s.ParentId is not null))
        {
            var group = groups.FirstOrDefault(g => g[0].Id == entry.ParentId);
            if (group is null)
                return InsertSectionResult.Fail("Некорректная структура подразделов. Сначала исправь базу.", currentRev);
            group.Add(entry);
        }

        var parentId = (long?)null;
        int groupIndex;
        int insertIndex;
        string parentName = "";
        if (subsection)
        {
            var anchor = original.FirstOrDefault(s => s.Id == anchorSystemId);
            if (anchor is null)
                return InsertSectionResult.Fail("Выбранный раздел отсутствует в документе.", currentRev);
            parentId = anchor.ParentId ?? anchor.Id;
            groupIndex = groups.FindIndex(g => g[0].Id == parentId);
            if (groupIndex < 0)
                return InsertSectionResult.Fail("Не найден корневой раздел.", currentRev);
            var group = groups[groupIndex];
            parentName = group[0].Name;
            var selectedIndex = group.FindIndex(s => s.Id == anchor.Id);
            insertIndex = anchor.ParentId is null ? group.Count : selectedIndex + 1;
        }
        else
        {
            groupIndex = groups.Count;
            if (anchorSystemId is long anchorId)
            {
                var anchor = original.FirstOrDefault(s => s.Id == anchorId);
                if (anchor is null)
                    return InsertSectionResult.Fail("Выбранный раздел отсутствует в документе.", currentRev);
                var rootId = anchor.ParentId ?? anchor.Id;
                var after = groups.FindIndex(g => g[0].Id == rootId);
                if (after < 0)
                    return InsertSectionResult.Fail("Не найден корневой раздел.", currentRev);
                groupIndex = after + 1;
            }
            insertIndex = 0;
        }

        long id;
        using (var insert = new SqlCommand(
            "INSERT INTO dbo.systems (contract_id,name,[order],parent_id,smr_amount,heading,source_num,stage_sys_src) " +
            "OUTPUT INSERTED.id VALUES (@contract,@name,@order,@parent,NULL,N'',N'',NULL)", conn, tx))
        {
            insert.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            insert.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = cleanName;
            insert.Parameters.Add("@order", SqlDbType.Int).Value = original.Count;
            insert.Parameters.Add("@parent", SqlDbType.BigInt).Value = parentId is null ? DBNull.Value : (object)parentId.Value;
            id = SqlInteger.RequireInt64(insert.ExecuteScalar());
        }

        var created = new SectionEntry(id, parentId, original.Count, cleanName);
        if (subsection) groups[groupIndex].Insert(insertIndex, created);
        else groups.Insert(groupIndex, new List<SectionEntry> { created });

        // Renumber as one set-based update: no intermediate ordering visible to other editors.
        // A temporary table avoids the 2100-parameter limit for unusually long estimates.
        using (var temp = new SqlCommand("CREATE TABLE #section_order (id BIGINT PRIMARY KEY, ord INT NOT NULL)", conn, tx))
            temp.ExecuteNonQuery();
        var ordered = groups.SelectMany(g => g).ToList();
        for (var offset = 0; offset < ordered.Count; offset += 250)
        {
            var batch = ordered.Skip(offset).Take(250).ToList();
            var values = new List<string>(batch.Count);
            using var fill = new SqlCommand { Connection = conn, Transaction = tx };
            for (var i = 0; i < batch.Count; i++)
            {
                var suffix = i.ToString(CultureInfo.InvariantCulture);
                values.Add($"(@id{suffix},@ord{suffix})");
                fill.Parameters.Add($"@id{suffix}", SqlDbType.BigInt).Value = batch[i].Id;
                fill.Parameters.Add($"@ord{suffix}", SqlDbType.Int).Value = offset + i;
            }
            fill.CommandText = "INSERT INTO #section_order (id,ord) VALUES " + string.Join(",", values);
            fill.ExecuteNonQuery();
        }
        using (var renumber = new SqlCommand(
            "UPDATE sy SET [order]=o.ord FROM dbo.systems AS sy " +
            "JOIN #section_order AS o ON o.id=sy.id WHERE sy.contract_id=@contract", conn, tx))
        {
            renumber.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (renumber.ExecuteNonQuery() != ordered.Count)
                throw new DBConcurrencyException("Порядок разделов изменился во время записи.");
        }

        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@time,@user,N'system',@id,N'create',@details,@contract,@object)", conn, tx))
        {
            log.Parameters.Add("@time", SqlDbType.DateTime2).Value = DateTime.Now;
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = subsection
                ? $"{title}: {parentName} › {cleanName}" : $"{title}: {cleanName}";
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            log.ExecuteNonQuery();
        }

        var identity = $"{Environment.UserName}@{Environment.MachineName}#{Environment.ProcessId}";
        var newRev = $"{identity}|{Guid.NewGuid():N}|{DateTime.Now:HH:mm:ss}";
        using (var update = new SqlCommand(
            "UPDATE dbo.meta SET [value]=@rev WHERE [key]=N'rev'", conn, tx))
        {
            update.Parameters.Add("@rev", SqlDbType.NVarChar, -1).Value = newRev;
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Не удалось обновить meta.rev.");
        }
        tx.Commit();
        return new InsertSectionResult(true, id, null, newRev);
    }
    /// <summary>Port of builder.rename_system: unchanged names do not alter revision or history.</summary>
    public static SaveResult RenameSection(string connectionString, long contractId,
        long systemId, string name, string expectedRevision)
    {
        var next = (name ?? "").Trim();
        if (next.Length == 0)
            return SaveResult.Fail("Название раздела не может быть пустым.", expectedRevision);
        if (next.Length > 255)
            return SaveResult.Fail("Название раздела длиннее 255 символов.", expectedRevision);
        if (contractId <= 0 || systemId <= 0)
            return SaveResult.Fail("Не выбран раздел сметы.", expectedRevision);

        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        using var rev = new SqlCommand(
            "SELECT [value] FROM dbo.meta WITH (UPDLOCK,HOLDLOCK) WHERE [key]=N'rev'", conn, tx);
        var currentRev = Convert.ToString(rev.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (currentRev is null)
            return SaveResult.Fail("На сервере отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(currentRev, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Документ на сервере изменился. Обнови смету.", currentRev);

        long objectId;
        string previous;
        using (var find = new SqlCommand(
            "SELECT sy.name,c.object_id,c.locked FROM dbo.systems sy WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.contracts c WITH (UPDLOCK,HOLDLOCK) ON c.id=sy.contract_id " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id AND ISNULL(o.archived,0)=0 " +
            "AND ISNULL(c.stage_rest,0)=0 " +
            "WHERE sy.id=@id AND c.id=@contract", conn, tx))
        {
            find.Parameters.Add("@id", SqlDbType.BigInt).Value = systemId;
            find.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var r = find.ExecuteReader();
            if (!r.Read()) return SaveResult.Fail("Раздел не найден в документе.", currentRev);
            previous = r.IsDBNull(0) ? "" : r.GetString(0);
            objectId = SqlInteger.ReadInt64(r, 1);
            if (SqlFlag.Read(r, 2))
                return SaveResult.Fail("Подписанную смету менять нельзя.", currentRev);
        }
        if (string.Equals(previous, next, StringComparison.Ordinal))
        {
            tx.Commit();
            return new SaveResult(true, null, currentRev);
        }
        using (var update = new SqlCommand(
            "UPDATE dbo.systems SET name=@name WHERE id=@id AND contract_id=@contract", conn, tx))
        {
            update.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = next;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = systemId;
            update.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Раздел исчез при переименовании.");
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@time,@user,N'system',@id,N'rename',@details,@contract,@object)", conn, tx))
        {
            log.Parameters.Add("@time", SqlDbType.DateTime2).Value = DateTime.Now;
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = systemId;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = $"«{previous}» → «{next}»";
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            log.ExecuteNonQuery();
        }
        var identity = $"{Environment.UserName}@{Environment.MachineName}#{Environment.ProcessId}";
        var newRev = $"{identity}|{Guid.NewGuid():N}|{DateTime.Now:HH:mm:ss}";
        using (var revUpdate = new SqlCommand(
            "UPDATE dbo.meta SET [value]=@rev WHERE [key]=N'rev'", conn, tx))
        {
            revUpdate.Parameters.Add("@rev", SqlDbType.NVarChar, -1).Value = newRev;
            if (revUpdate.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Не удалось обновить meta.rev.");
        }
        tx.Commit();
        return new SaveResult(true, null, newRev);
    }

}
