using System.Data;
using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed class LetterCommitUncertainException(string message, Exception inner)
    : InvalidOperationException(message, inner) { }

/// <summary>
/// Independent SQL Server letter archive. No SQLite connection, provider or migration.
/// All writes, including DDL, are forbidden outside a verified GmcSmetaPilot_* catalog.
/// </summary>
public sealed partial class SqlServerLetters
{
    private readonly string _connectionString;

    public SqlServerLetters(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        _connectionString = connectionString;
        EnsureSchema();
    }

    private SqlConnection Open() => RequirePilotConnection(SqlServerDatabase.Open(_connectionString));

    private async Task<SqlConnection> OpenReadAsync(CancellationToken token) =>
        RequirePilotConnection(await SqlServerDatabase.OpenAsync(_connectionString, token).ConfigureAwait(false));

    private static SqlConnection RequirePilotConnection(SqlConnection connection)
    {
        if (!connection.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
        {
            connection.Dispose();
            throw new InvalidOperationException("Реальная SQL Server база не GmcSmetaPilot_*; запись запрещена.");
        }
        return connection;
    }

    /// <summary>Idempotent pilot-only migration, serialized between application instances.</summary>
    public void EnsureSchema()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Lock(conn, tx, "GMC.Letters.Schema");
        using var command = new SqlCommand(Schema, conn, tx);
        command.CommandTimeout = 30;
        command.ExecuteNonQuery();
        tx.Commit();
    }

    private const string Schema = """
IF OBJECT_ID(N'dbo.gmc_letters', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_letters(
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_gmc_letters PRIMARY KEY,
        outgoing_number NVARCHAR(128) NOT NULL DEFAULT N'',
        letter_date DATE NOT NULL,
        letter_year AS DATEPART(year, letter_date) PERSISTED,
        normalized_number AS UPPER(LTRIM(RTRIM(outgoing_number))) PERSISTED,
        subject NVARCHAR(400) NOT NULL DEFAULT N'',
        object_name NVARCHAR(400) NOT NULL DEFAULT N'',
        recipient NVARCHAR(4000) NOT NULL DEFAULT N'',
        status NVARCHAR(20) NOT NULL CONSTRAINT CK_gmc_letters_status CHECK(status IN (N'Черновик', N'Отправлено')),
        payload_json NVARCHAR(MAX) NOT NULL,
        revision BIGINT NOT NULL CONSTRAINT DF_gmc_letters_revision DEFAULT(1),
        created_utc DATETIME2(7) NOT NULL CONSTRAINT DF_gmc_letters_created DEFAULT SYSUTCDATETIME(),
        updated_utc DATETIME2(7) NOT NULL CONSTRAINT DF_gmc_letters_updated DEFAULT SYSUTCDATETIME()
    );
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.gmc_letters') AND name=N'UX_gmc_letters_year_number')
    CREATE UNIQUE INDEX UX_gmc_letters_year_number
        ON dbo.gmc_letters(letter_year, normalized_number)
        WHERE outgoing_number <> N'';
IF OBJECT_ID(N'dbo.gmc_letter_files', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_letter_files(
        id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_gmc_letter_files PRIMARY KEY,
        letter_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.gmc_letters(id),
        title NVARCHAR(255) NOT NULL,
        kind NVARCHAR(64) NOT NULL,
        relative_path NVARCHAR(450) NOT NULL,
        pages INT NOT NULL,
        sha256 CHAR(64) NOT NULL,
        included BIT NOT NULL,
        position INT NOT NULL
    );
END;
IF OBJECT_ID(N'dbo.gmc_letter_history', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_letter_history(
        id BIGINT IDENTITY PRIMARY KEY,
        letter_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.gmc_letters(id),
        revision BIGINT NOT NULL,
        payload_json NVARCHAR(MAX) NOT NULL,
        saved_utc DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF OBJECT_ID(N'dbo.gmc_letter_releases', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_letter_releases(
        id BIGINT IDENTITY PRIMARY KEY,
        letter_id UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.gmc_letters(id),
        relative_path NVARCHAR(450) NOT NULL UNIQUE,
        fingerprint CHAR(64) NOT NULL,
        manifest_sha256 CHAR(64) NOT NULL,
        combined BIT NOT NULL,
        created_utc DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
IF OBJECT_ID(N'dbo.gmc_letter_sending', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.gmc_letter_sending(
        letter_id UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_gmc_letter_sending PRIMARY KEY
            REFERENCES dbo.gmc_letters(id),
        date_sent DATE NOT NULL,
        channel NVARCHAR(120) NOT NULL,
        destination NVARCHAR(4000) NOT NULL,
        release_path NVARCHAR(450) NOT NULL,
        recorded_utc DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME()
    );
END;
""";

    private static void Lock(SqlConnection conn, SqlTransaction tx, string name)
    {
        using var cmd = new SqlCommand(
            "DECLARE @result int; EXEC @result=sys.sp_getapplock " +
            "@Resource=@name,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=12000; SELECT @result;", conn, tx);
        cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = name;
        if (SqlInteger.RequireInt64(cmd.ExecuteScalar()) < 0)
            throw new TimeoutException("Не удалось получить блокировку нумерации писем.");
    }

    private static LetterDocument? Read(SqlConnection conn, SqlTransaction? tx, Guid id, bool forUpdate)
    {
        using var cmd = new SqlCommand(
            "SELECT payload_json,revision FROM dbo.gmc_letters " +
            (forUpdate ? "WITH (UPDLOCK,HOLDLOCK) " : "") + "WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var data = JsonSerializer.Deserialize<LetterDocument>(reader.GetString(0), LettersRules.Json)
            ?? throw new InvalidDataException("Повреждён JSON письма.");
        data.Revision = SqlInteger.ReadInt64(reader, 1);
        return data;
    }

    public LetterDocument Get(Guid id)
    {
        using var conn = Open();
        return Read(conn, null, id, false) ?? throw new KeyNotFoundException("Письмо не найдено.");
    }

    public IReadOnlyList<LetterDocument> List(string query = "", string status = "Все")
    {
        using var conn = Open();
        using var cmd = new SqlCommand(
            "SELECT payload_json,revision FROM dbo.gmc_letters ORDER BY updated_utc DESC,id DESC", conn);
        using var reader = cmd.ExecuteReader();
        var list = new List<LetterDocument>();
        while (reader.Read())
        {
            var item = JsonSerializer.Deserialize<LetterDocument>(reader.GetString(0), LettersRules.Json)!;
            item.Revision = SqlInteger.ReadInt64(reader, 1);
            if (MatchesList(item, query, status)) list.Add(item);
        }
        return list;
    }

    // Keep the original payload and culture-based filtering semantics. Scope is applied
    // while reading rather than building a second full list on the caller's thread.
    public async Task<IReadOnlyList<LetterDocument>> ListAsync(string query, string status,
        CancellationToken token, Func<LetterDocument, bool>? belongs = null)
    {
        token.ThrowIfCancellationRequested();
        using var conn = await OpenReadAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using var cmd = new SqlCommand(
            "SELECT payload_json,revision FROM dbo.gmc_letters ORDER BY updated_utc DESC,id DESC", conn);
        using var reader = await cmd.ExecuteReaderAsync(token).ConfigureAwait(false);
        var list = new List<LetterDocument>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            var item = JsonSerializer.Deserialize<LetterDocument>(reader.GetString(0), LettersRules.Json)!;
            item.Revision = SqlInteger.ReadInt64(reader, 1);
            if (MatchesList(item, query, status) && (belongs is null || belongs(item)))
                list.Add(item);
        }
        token.ThrowIfCancellationRequested();
        return list;
    }

    private static bool MatchesList(LetterDocument item, string query, string status) =>
        (status == "Все" || item.Status == status) &&
        (string.IsNullOrWhiteSpace(query) ||
            item.Number.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Subject.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Object.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            item.Recipient.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    private static string Allocate(SqlConnection conn, SqlTransaction tx, DateTime date, Guid exclude)
    {
        var prefix = date.ToString("dd.MM-", CultureInfo.InvariantCulture);
        using var cmd = new SqlCommand(
            "SELECT outgoing_number FROM dbo.gmc_letters WHERE letter_year=@year " +
            "AND outgoing_number LIKE @pattern AND id<>@exclude", conn, tx);
        cmd.Parameters.Add("@year", SqlDbType.Int).Value = date.Year;
        cmd.Parameters.Add("@pattern", SqlDbType.NVarChar, 140).Value = prefix + "%";
        cmd.Parameters.Add("@exclude", SqlDbType.UniqueIdentifier).Value = exclude;
        var existing = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) existing.Add(reader.GetString(0));
        return LettersRules.NextNumber(date, existing);
    }

    /// <summary>A suggested number is not a reservation; Save atomically assigns it.</summary>
    public string SuggestNumber(string date, Guid exclude)
    {
        var day = LettersRules.ParseDate(date);
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        Lock(conn, tx, "GMC.Letters.Number." + day.Year);
        var result = Allocate(conn, tx, day, exclude);
        tx.Commit();
        return result;
    }

    public LetterDocument Create(LetterDocument? source = null)
    {
        var result = source is null ? new LetterDocument() :
            LettersRules.CopyDraft(source, DateTime.Today);
        Save(result);
        return result;
    }

    public LetterDocument Copy(Guid id) => Create(Get(id));

    private static bool SameFilesExceptNewReceipts(LetterDocument old, LetterDocument current)
    {
        if (current.Files.Count < old.Files.Count) return false;
        for (var i = 0; i < old.Files.Count; i++)
        {
            if (JsonSerializer.Serialize(old.Files[i], LettersRules.Json) !=
                JsonSerializer.Serialize(current.Files[i], LettersRules.Json)) return false;
        }
        return current.Files.Skip(old.Files.Count).All(x => x.Kind == "Подтверждение отправки");
    }

    /// <summary>
    /// Serializable, per-year SQL application lock and a unique index enforce race-free allocation.
    /// A release and/or sending record is committed in the same transaction as the letter revision.
    /// </summary>
    internal static void CommitPreservingArchive(SqlTransaction tx)
    {
        try { tx.Commit(); }
        catch (Exception ex)
        {
            throw new LetterCommitUncertainException(
                "Исход COMMIT архива писем неизвестен. Не повторяйте запись до сверки SQL и файлов.", ex);
        }
    }

    public void Save(LetterDocument document, LetterRelease? release = null, bool validateContext = false,
        bool useAgreementRecipient = false)
    {
        LettersRules.Validate(document);
        using var conn = Open();
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var day = LettersRules.ParseDate(document.Date);
        Lock(conn, tx, "GMC.Letters.Number." + day.Year);
        var existing = Read(conn, tx, document.Id, true);
        if (existing is not null && existing.Revision != document.Revision)
            throw new DBConcurrencyException("Письмо изменено другим сотрудником. Откройте его заново.");
        if (existing is null && document.Revision != 0)
            throw new DBConcurrencyException("Не найдено сохранённое письмо.");
        if (existing?.Sent is not null &&
            (LettersRules.Fingerprint(existing) != LettersRules.Fingerprint(document) ||
             !SameFilesExceptNewReceipts(existing, document) ||
             document.Status != "Отправлено" ||
             JsonSerializer.Serialize(existing.Sent, LettersRules.Json) !=
             JsonSerializer.Serialize(document.Sent, LettersRules.Json)))
            throw new InvalidOperationException("Отправленное письмо нельзя изменять. Создайте копию.");
        // The scoped document-picker path validates the association in the
        // very same transaction as the letter revision and history update.
        if (useAgreementRecipient && (!validateContext || existing is not null || release is not null))
            throw new InvalidOperationException("Автозаполнение получателя допустимо только для нового письма.");
        if (validateContext)
        {
            if (document.ObjectId is not long objectId || objectId <= 0)
                throw new InvalidOperationException("Не указан объект письма.");
            if (existing is not null && !LetterContext.CanAttach(existing))
                throw new InvalidOperationException("Письмо уже привязано или опубликовано.");
            using var scope = new SqlCommand(
                "SELECT COUNT_BIG(*) FROM dbo.objects WITH(HOLDLOCK) WHERE id=@obj AND " +
                "(@contract IS NULL OR EXISTS(SELECT 1 FROM dbo.contracts WITH(HOLDLOCK) " +
                "WHERE id=@contract AND object_id=@obj AND kind IN(N'main',N'dop')))", conn, tx);
            scope.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
            scope.Parameters.Add("@contract", SqlDbType.BigInt).Value = document.ContractId is long contractId ? contractId : DBNull.Value;
            if (SqlInteger.RequireInt64(scope.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Объект или договор больше не существует либо не соответствует привязке.");
            ApplyAgreementContext(conn, tx, document, objectId, useAgreementRecipient);
        }
        if (existing is not null && !existing.Releases.SequenceEqual(document.Releases))
            throw new InvalidOperationException("Историю выпусков нельзя редактировать.");
        if (existing?.Sent is not null && release is not null)
            throw new InvalidOperationException("Для отправленного письма выпуск уже закреплён.");
        if (document.NumberAuto && document.Sent is null &&
            (existing is null || existing.Date != document.Date ||
             !string.Equals(existing.Number, document.Number, StringComparison.Ordinal)))
            document.Number = Allocate(conn, tx, day, document.Id);
        // Applying release is an internal operation, never an editor-side arbitrary list edit.
        if (release is not null)
        {
            LettersRules.SafeArchiveRelative(release.Path, "exports");
            if (release.Fingerprint != LettersRules.Fingerprint(document))
                throw new InvalidOperationException("Письмо изменилось после формирования выпуска.");
            document.Releases.Add(release.Path);
        }
        LettersRules.Validate(document);
        var json = JsonSerializer.Serialize(document, LettersRules.Json);
        if (existing is not null &&
            JsonSerializer.Serialize(existing, LettersRules.Json) == json && release is null)
        {
            tx.Commit();
            return;
        }
        var rev = (existing?.Revision ?? 0) + 1;
        using (var cmd = new SqlCommand(existing is null
            ? """
              INSERT INTO dbo.gmc_letters
              (id,outgoing_number,letter_date,subject,object_name,recipient,status,payload_json,revision)
              VALUES (@id,@number,@date,@subject,@object,@recipient,@status,@json,@rev)
              """
            : """
              UPDATE dbo.gmc_letters
              SET outgoing_number=@number,letter_date=@date,subject=@subject,
                  object_name=@object,recipient=@recipient,status=@status,
                  payload_json=@json,revision=@rev,updated_utc=SYSUTCDATETIME()
              WHERE id=@id AND revision=@previous
              """, conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = document.Id;
            cmd.Parameters.Add("@number", SqlDbType.NVarChar, 128).Value = document.Number.Trim();
            cmd.Parameters.Add("@date", SqlDbType.Date).Value = day;
            cmd.Parameters.Add("@subject", SqlDbType.NVarChar, 400).Value = document.Subject;
            cmd.Parameters.Add("@object", SqlDbType.NVarChar, 400).Value = document.Object;
            cmd.Parameters.Add("@recipient", SqlDbType.NVarChar, 4000).Value = document.Recipient;
            cmd.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value = document.Status;
            cmd.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = json;
            cmd.Parameters.Add("@rev", SqlDbType.BigInt).Value = rev;
            if (existing is not null)
                cmd.Parameters.Add("@previous", SqlDbType.BigInt).Value = existing.Revision;
            try
            {
                if (cmd.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Письмо уже изменено другим клиентом.");
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                throw new InvalidOperationException("Этот исходящий номер уже используется в выбранном году.", ex);
            }
        }
        using (var history = new SqlCommand(
            "INSERT INTO dbo.gmc_letter_history(letter_id,revision,payload_json) VALUES(@id,@rev,@json)", conn, tx))
        {
            history.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = document.Id;
            history.Parameters.Add("@rev", SqlDbType.BigInt).Value = rev;
            history.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = json;
            history.ExecuteNonQuery();
        }
        using (var purge = new SqlCommand("DELETE FROM dbo.gmc_letter_files WHERE letter_id=@id", conn, tx))
        {
            purge.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = document.Id;
            purge.ExecuteNonQuery();
        }
        for (var i = 0; i < document.Files.Count; i++)
        {
            var file = document.Files[i];
            using var cmd = new SqlCommand(
                "INSERT INTO dbo.gmc_letter_files(id,letter_id,title,kind,relative_path,pages,sha256,included,position) " +
                "VALUES(@id,@letter,@title,@kind,@path,@pages,@hash,@included,@position)", conn, tx);
            cmd.Parameters.AddWithValue("@id", file.Id);
            cmd.Parameters.AddWithValue("@letter", document.Id);
            cmd.Parameters.AddWithValue("@title", file.Title);
            cmd.Parameters.AddWithValue("@kind", file.Kind);
            cmd.Parameters.AddWithValue("@path", file.Path);
            cmd.Parameters.AddWithValue("@pages", file.Pages);
            cmd.Parameters.AddWithValue("@hash", file.Sha256);
            cmd.Parameters.AddWithValue("@included", file.Included);
            cmd.Parameters.AddWithValue("@position", i);
            cmd.ExecuteNonQuery();
        }
        if (release is not null)
        {
            using var cmd = new SqlCommand("INSERT INTO dbo.gmc_letter_releases " +
                "(letter_id,relative_path,fingerprint,manifest_sha256,combined) " +
                "VALUES (@id,@path,@hash,@manifest,@combined)", conn, tx);
            cmd.Parameters.AddWithValue("@id", document.Id);
            cmd.Parameters.AddWithValue("@path", release.Path);
            cmd.Parameters.AddWithValue("@hash", release.Fingerprint);
            cmd.Parameters.AddWithValue("@manifest", release.ManifestSha256);
            cmd.Parameters.AddWithValue("@combined", release.Combined);
            cmd.ExecuteNonQuery();
        }
        if (existing?.Sent is null && document.Sent is not null)
        {
            if (release is not null || !document.Releases.Contains(document.Sent.Release))
                throw new InvalidOperationException("Нельзя отметить отправку без ранее собранного выпуска.");
            using var cmd = new SqlCommand("INSERT INTO dbo.gmc_letter_sending " +
                "(letter_id,date_sent,channel,destination,release_path) " +
                "VALUES (@id,@date,@channel,@destination,@release)", conn, tx);
            cmd.Parameters.AddWithValue("@id", document.Id);
            cmd.Parameters.Add("@date", SqlDbType.Date).Value = LettersRules.ParseDate(document.Sent.Date);
            cmd.Parameters.AddWithValue("@channel", document.Sent.Channel);
            cmd.Parameters.AddWithValue("@destination", document.Sent.Destination);
            cmd.Parameters.AddWithValue("@release", document.Sent.Release);
            cmd.ExecuteNonQuery();
        }
        CommitPreservingArchive(tx);
        document.Revision = rev;
    }

    public void MarkSent(LetterDocument document, string date, string channel, string destination, string releasePath)
    {
        if (document.Sent is not null) throw new InvalidOperationException("Письмо уже отправлено.");
        LettersRules.ParseDate(date);
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Укажите способ отправки и получателя.");
        using (var conn = Open())
        using (var cmd = new SqlCommand(
            "SELECT fingerprint FROM dbo.gmc_letter_releases WHERE letter_id=@id AND relative_path=@path", conn))
        {
            cmd.Parameters.AddWithValue("@id", document.Id);
            cmd.Parameters.AddWithValue("@path", releasePath);
            var fingerprint = cmd.ExecuteScalar() as string;
            if (fingerprint is null || fingerprint.TrimEnd() != LettersRules.Fingerprint(document))
                throw new InvalidOperationException("Письмо изменилось после выпуска. Соберите его заново.");
        }
        var previousStatus = document.Status;
        document.Status = "Отправлено";
        document.Sent = new LetterSending
        {
            Date = date, Channel = channel.Trim(), Destination = destination.Trim(), Release = releasePath
        };
        try { Save(document); }
        catch (LetterCommitUncertainException) { throw; }
        catch { document.Status = previousStatus; document.Sent = null; throw; }
    }


    public sealed record HistoricalVersion(long Revision, DateTime SavedUtc, LetterDocument Snapshot);

    /// <summary>Read-only history browser: every saved snapshot remains available.</summary>
    public IReadOnlyList<HistoricalVersion> ReadHistory(Guid id)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(
            "SELECT revision,saved_utc,payload_json FROM dbo.gmc_letter_history " +
            "WHERE letter_id=@id ORDER BY revision DESC,id DESC", conn);
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        var list = new List<HistoricalVersion>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var snapshot = JsonSerializer.Deserialize<LetterDocument>(reader.GetString(2), LettersRules.Json)
                ?? throw new InvalidDataException("Повреждён снимок в истории письма.");
            snapshot.Revision = SqlInteger.ReadInt64(reader, 0);
            list.Add(new HistoricalVersion(SqlInteger.ReadInt64(reader, 0), reader.GetDateTime(1), snapshot));
        }
        return list;
    }

    public IReadOnlyList<(long Revision, DateTime SavedUtc)> History(Guid id)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(
            "SELECT revision,saved_utc FROM dbo.gmc_letter_history WHERE letter_id=@id ORDER BY id DESC", conn);
        cmd.Parameters.AddWithValue("@id", id);
        var result = new List<(long, DateTime)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add((SqlInteger.ReadInt64(reader, 0), reader.GetDateTime(1)));
        return result;
    }
}
