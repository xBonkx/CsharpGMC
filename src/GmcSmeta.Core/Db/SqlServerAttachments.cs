using System.Data;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Existing Python attachments table. Metadata writes are isolated to Pilot.</summary>
public static class SqlServerAttachments
{
    public sealed record Entry(long Id, string Name, string Path, long Size, string AddedBy,
        DateTime AddedAt, string Note, string Role, bool IsPrimary);
    public sealed record Snapshot(string Revision, string Folder, IReadOnlyList<Entry> Entries)
    {
        public bool Archived { get; init; }
    }
    public sealed record NewFile(string Name, string Path, long Size);

    public static Snapshot Read(string connectionString, long contractId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(connectionString);
        string Revision() => token.CanBeCanceled
            ? SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Revision(conn);
        var revision = Revision();
        var context = ReadContext(conn, null, contractId, token);
        var entries = new List<Entry>();
        using (var cmd = new SqlCommand(
            "SELECT id,name,path,size,COALESCE(added_by,''),COALESCE(added_at,GETDATE()),COALESCE(note,'')," +
            "COALESCE(role,''),COALESCE(is_primary,0) FROM dbo.attachments WHERE contract_id=@c ORDER BY added_at,id", conn))
        {
            cmd.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
            using var registration = SqlReadCancellation.Register(cmd, token);
            using var reader = SqlReadCancellation.ExecuteReader(cmd, token);
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                entries.Add(new Entry(Convert.ToInt64(reader.GetValue(0)), reader.GetString(1), reader.GetString(2),
                    Convert.ToInt64(reader.GetValue(3)), reader.GetString(4), reader.GetDateTime(5),
                    reader.GetString(6), reader.GetString(7), Convert.ToBoolean(reader.GetValue(8))));
            }
        }
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(revision) || revision != Revision())
            throw new DBConcurrencyException("Вложения изменились при чтении. Обновите список.");
        token.ThrowIfCancellationRequested();
        return new Snapshot(revision, context.Folder, entries) { Archived = context.Archived };
    }

    private static (string Folder, bool Archived) ReadContext(SqlConnection conn, SqlTransaction? tx,
        long contractId, CancellationToken token = default)
    {
        var hint = tx is null ? "" : " WITH (HOLDLOCK)";
        using var cmd = new SqlCommand(
            "SELECT COALESCE(o.folder,N''),o.archived FROM dbo.contracts c" + hint +
            " JOIN dbo.objects o" + hint + " ON o.id=c.object_id WHERE c.id=@c", conn, tx);
        cmd.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
        token.ThrowIfCancellationRequested();
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var reader = SqlReadCancellation.ExecuteReader(cmd, token);
        if (!reader.Read()) throw new InvalidOperationException("Документ не найден.");
        token.ThrowIfCancellationRequested();
        return (reader.GetString(0), reader.IsDBNull(1) || Convert.ToBoolean(reader.GetValue(1)));
    }

    private static string Check(SqlConnection conn, SqlTransaction tx, long contractId, string expected)
    {
        var revision = SqlServerDatabase.LockRevision(conn, tx);
        if (string.IsNullOrWhiteSpace(revision) || revision != expected)
            throw new DBConcurrencyException("Данные изменились. Обновите вложения.");
        var context = ReadContext(conn, tx, contractId);
        if (context.Archived)
            throw new InvalidOperationException("Вложения архивной карточки доступны только для просмотра.");
        return context.Folder;
    }

    private static void Log(SqlConnection conn, SqlTransaction tx, long contractId, string action, string details)
    {
        using var cmd = new SqlCommand(
            "INSERT dbo.change_log(ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES(SYSDATETIME(),@user,N'contract',@c,@action,@details,@c,(SELECT object_id FROM dbo.contracts WHERE id=@c))", conn, tx);
        cmd.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
        cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
        cmd.Parameters.Add("@action", SqlDbType.NVarChar, 32).Value = action;
        cmd.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        cmd.ExecuteNonQuery();
    }

    public static string StorageFolder(string connectionString, long contractId, string objectFolder)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        var db = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (contractId <= 0 || string.IsNullOrWhiteSpace(objectFolder) ||
            !Path.IsPathFullyQualified(objectFolder) ||
            !objectFolder.StartsWith(@"\\", StringComparison.Ordinal) ||
            objectFolder.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            objectFolder.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            db.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_')))
            throw new InvalidOperationException("Укажите существующую общую UNC-папку объекта. Локальный архив не используется.");
        return Path.Combine(Path.GetFullPath(objectFolder), "Договоры", db, contractId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static string Register(string connectionString, long contractId, IReadOnlyList<NewFile> files,
        bool signed, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (files.Count == 0 || files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count || files.Any(f => f.Size < 0 || f.Name.Length == 0 || f.Name.Length > 255 ||
            f.Name != Path.GetFileName(f.Name) || !Path.IsPathFullyQualified(f.Path) ||
            (signed && !Path.GetExtension(f.Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Некорректные вложения.");
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var folder = StorageFolder(connectionString, contractId, Check(conn, tx, contractId, expectedRevision));
        long? firstNew = null;
        foreach (var file in files)
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file.Path)), folder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Папка объекта изменилась или файл находится вне тестового архива.");
            using var cmd = new SqlCommand(
                "INSERT dbo.attachments(contract_id,name,path,size,role,is_primary,note,added_by,added_at) OUTPUT INSERTED.id " +
                "SELECT @c,@n,@p,@s,@role,0,N'',@u,SYSDATETIME() WHERE NOT EXISTS " +
                "(SELECT 1 FROM dbo.attachments WITH(UPDLOCK,HOLDLOCK) WHERE contract_id=@c AND path=@p)", conn, tx);
            cmd.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
            cmd.Parameters.Add("@n", SqlDbType.NVarChar, 255).Value = file.Name;
            cmd.Parameters.Add("@p", SqlDbType.NVarChar, -1).Value = file.Path;
            cmd.Parameters.Add("@s", SqlDbType.BigInt).Value = file.Size;
            cmd.Parameters.Add("@role", SqlDbType.NVarChar, 32).Value = signed ? "signed_contract" : "";
            cmd.Parameters.Add("@u", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            var inserted = cmd.ExecuteScalar();
            if (inserted is null or DBNull) throw new DBConcurrencyException("Вложение не добавлено (возможно, путь уже зарегистрирован).");
            firstNew ??= Convert.ToInt64(inserted);
            Log(conn, tx, contractId, "attach", file.Name);
        }
        if (signed && firstNew is long id) EnsurePrimary(conn, tx, contractId, id);
        var revision = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return revision;
    }

    // Preserve any existing primary; the first explicitly classified PDF becomes primary.
    private static void EnsurePrimary(SqlConnection conn, SqlTransaction tx, long contractId, long id)
    {
        using var cmd = new SqlCommand(
            "UPDATE dbo.attachments SET is_primary=1 WHERE id=@id AND contract_id=@c AND role=N'signed_contract' AND NOT EXISTS " +
            "(SELECT 1 FROM dbo.attachments WHERE contract_id=@c AND role=N'signed_contract' AND is_primary=1)", conn, tx);
        cmd.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
        cmd.ExecuteNonQuery();
    }

    public static string SetSigned(string connectionString, long contractId, long id, string expectedPath,
        bool signed, bool primary, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (id <= 0 || primary && !signed) throw new ArgumentException("Некорректная отметка.");
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Check(conn, tx, contractId, expectedRevision);
        using (var read = new SqlCommand("SELECT path FROM dbo.attachments WHERE id=@id AND contract_id=@c", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            read.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
            if (read.ExecuteScalar() is not string path || path != expectedPath)
                throw new DBConcurrencyException("Выбранный файл изменился.");
            if (signed && !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Выберите PDF.");
        }
        // Python marks the selected file primary when no signed primary exists.
        if (signed && !primary)
        {
            using var existing = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.attachments WHERE contract_id=@c AND role=N'signed_contract' AND is_primary=1", conn, tx);
            existing.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
            primary = Convert.ToInt64(existing.ExecuteScalar()) == 0;
        }
        if (primary)
        {
            using var reset = new SqlCommand("UPDATE dbo.attachments SET is_primary=0 WHERE contract_id=@c AND is_primary=1", conn, tx);
            reset.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
            reset.ExecuteNonQuery();
        }
        using (var update = new SqlCommand(
            "UPDATE dbo.attachments SET role=@role,is_primary=CASE WHEN @signed=0 THEN 0 WHEN @primary=1 THEN 1 WHEN role=N'signed_contract' THEN is_primary ELSE 0 END " +
            "WHERE id=@id AND contract_id=@c", conn, tx))
        {
            update.Parameters.Add("@role", SqlDbType.NVarChar, 32).Value = signed ? "signed_contract" : "";
            update.Parameters.Add("@signed", SqlDbType.Bit).Value = signed;
            update.Parameters.Add("@primary", SqlDbType.Bit).Value = primary;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            update.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
            if (update.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Файл не найден.");
        }
        Log(conn, tx, contractId, "attach", signed ? $"Подписанный PDF #{id}" : $"Снята отметка PDF #{id}");
        var revision = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return revision;
    }

    public static string Delete(string connectionString, long contractId, IReadOnlyList<long> ids, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        if (ids.Count == 0 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Некорректный выбор файлов.");
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Check(conn, tx, contractId, expectedRevision);
        foreach (var id in ids)
        {
            using var cmd = new SqlCommand("DELETE FROM dbo.attachments WHERE id=@id AND contract_id=@c", conn, tx);
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            cmd.Parameters.Add("@c", SqlDbType.BigInt).Value = contractId;
            if (cmd.ExecuteNonQuery() != 1) throw new DBConcurrencyException("Файл уже удалён или принадлежит другому документу.");
            Log(conn, tx, contractId, "detach", $"Вложение #{id}");
        }
        var revision = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return revision;
    }
}
