using System.Data;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Logical SQL Server backup/restore operations. RESTORE is create-only and pilot-only.</summary>
public static class SqlServerLettersBackup
{
    public sealed record HistoryRecord(Guid LetterId, long Revision, string PayloadJson, DateTime SavedUtc);
    public sealed record ReleaseRecord(Guid LetterId, string Path, string Fingerprint, string ManifestSha256, bool Combined);

    public static IReadOnlyList<HistoryRecord> ExportHistory(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        if (!conn.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Нельзя выгружать письма из рабочей базы.");
        using var cmd = new SqlCommand(
            "SELECT letter_id,revision,payload_json,saved_utc " +
            "FROM dbo.gmc_letter_history ORDER BY letter_id,revision,id", conn);
        var result = new List<HistoryRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(new HistoryRecord(r.GetGuid(0), SqlInteger.ReadInt64(r, 1), r.GetString(2), r.GetDateTime(3)));
        return result;
    }

    public static void Restore(string connectionString, IReadOnlyList<LetterDocument> letters,
        IReadOnlyList<HistoryRecord> history, IReadOnlyList<ReleaseRecord> releases)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        // Constructor performs the pilot-only, idempotent migration first.
        _ = new SqlServerLetters(connectionString);
        if (letters.Select(d => d.Id).Distinct().Count() != letters.Count)
            throw new InvalidDataException("В архиве повторяются идентификаторы писем.");
        foreach (var doc in letters)
        {
            LettersRules.Validate(doc);
            if (doc.Releases.Any(path => !releases.Any(r => r.LetterId == doc.Id && r.Path == path)))
                throw new InvalidDataException("Не все выпуски представлены в описи.");
            if (doc.Sent is not null && !doc.Releases.Contains(doc.Sent.Release))
                throw new InvalidDataException("У отправленного письма отсутствует выпуск.");
        }
        using var conn = SqlServerDatabase.Open(connectionString);
        if (!conn.Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Восстановление допустимо только в GmcSmetaPilot_*.");
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        // No deletion or overwrite; protects against interleaving with concurrent clients.
        using (var guard = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo.gmc_letters WITH(TABLOCKX,HOLDLOCK)", conn, tx))
        {
            if (SqlInteger.RequireInt64(guard.ExecuteScalar()) > 0)
                throw new InvalidOperationException(
                    "SQL Server уже содержит письма. Восстановление допускается только в пустой тестовый архив SQL Server.");
        }
        foreach (var d in letters)
        {
            using var cmd = new SqlCommand(
                "INSERT dbo.gmc_letters(id,outgoing_number,letter_date,subject,object_name,recipient," +
                "status,payload_json,revision) VALUES(@id,@number,@date,@subject,@object,@recipient,@status,@json,@rev)", conn, tx);
            cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = d.Id;
            cmd.Parameters.Add("@number", SqlDbType.NVarChar, 128).Value = d.Number.Trim();
            cmd.Parameters.Add("@date", SqlDbType.Date).Value = LettersRules.ParseDate(d.Date);
            cmd.Parameters.Add("@subject", SqlDbType.NVarChar, 400).Value = d.Subject;
            cmd.Parameters.Add("@object", SqlDbType.NVarChar, 400).Value = d.Object;
            cmd.Parameters.Add("@recipient", SqlDbType.NVarChar, 4000).Value = d.Recipient;
            cmd.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value = d.Status;
            cmd.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value =
                JsonSerializer.Serialize(d, LettersRules.Json);
            cmd.Parameters.Add("@rev", SqlDbType.BigInt).Value = Math.Max(1, d.Revision);
            cmd.ExecuteNonQuery();
            for (var i = 0; i < d.Files.Count; i++)
            {
                var f = d.Files[i];
                using var insert = new SqlCommand(
                    "INSERT dbo.gmc_letter_files(id,letter_id,title,kind,relative_path,pages,sha256,included,position) " +
                    "VALUES(@id,@letter,@title,@kind,@path,@pages,@sha,@included,@position)", conn, tx);
                insert.Parameters.AddWithValue("@id", f.Id);
                insert.Parameters.AddWithValue("@letter", d.Id);
                insert.Parameters.AddWithValue("@title", f.Title);
                insert.Parameters.AddWithValue("@kind", f.Kind);
                insert.Parameters.AddWithValue("@path", f.Path);
                insert.Parameters.AddWithValue("@pages", f.Pages);
                insert.Parameters.AddWithValue("@sha", f.Sha256);
                insert.Parameters.AddWithValue("@included", f.Included);
                insert.Parameters.AddWithValue("@position", i);
                insert.ExecuteNonQuery();
            }
            if (d.Sent is { } sending)
            {
                using var sent = new SqlCommand(
                    "INSERT dbo.gmc_letter_sending(letter_id,date_sent,channel,destination,release_path) " +
                    "VALUES(@id,@date,@channel,@destination,@release)", conn, tx);
                sent.Parameters.AddWithValue("@id", d.Id);
                sent.Parameters.Add("@date", SqlDbType.Date).Value = LettersRules.ParseDate(sending.Date);
                sent.Parameters.AddWithValue("@channel", sending.Channel);
                sent.Parameters.AddWithValue("@destination", sending.Destination);
                sent.Parameters.AddWithValue("@release", sending.Release);
                sent.ExecuteNonQuery();
            }
        }
        foreach (var h in history)
        {
            if (!letters.Any(l => l.Id == h.LetterId))
                throw new InvalidDataException("История относится к отсутствующему письму.");
            using var cmd = new SqlCommand(
                "INSERT dbo.gmc_letter_history(letter_id,revision,payload_json,saved_utc) " +
                "VALUES(@id,@rev,@json,@time)", conn, tx);
            cmd.Parameters.AddWithValue("@id", h.LetterId);
            cmd.Parameters.AddWithValue("@rev", h.Revision);
            cmd.Parameters.AddWithValue("@json", h.PayloadJson);
            cmd.Parameters.AddWithValue("@time", h.SavedUtc);
            cmd.ExecuteNonQuery();
        }
        foreach (var rel in releases)
        {
            if (!letters.Any(l => l.Id == rel.LetterId))
                throw new InvalidDataException("Выпуск относится к отсутствующему письму.");
            LettersRules.SafeArchiveRelative(rel.Path, "exports");
            using var cmd = new SqlCommand(
                "INSERT dbo.gmc_letter_releases(letter_id,relative_path,fingerprint,manifest_sha256,combined) " +
                "VALUES(@id,@path,@fingerprint,@hash,@combined)", conn, tx);
            cmd.Parameters.AddWithValue("@id", rel.LetterId);
            cmd.Parameters.AddWithValue("@path", rel.Path);
            cmd.Parameters.AddWithValue("@fingerprint", rel.Fingerprint);
            cmd.Parameters.AddWithValue("@hash", rel.ManifestSha256);
            cmd.Parameters.AddWithValue("@combined", rel.Combined);
            cmd.ExecuteNonQuery();
        }
        SqlServerLetters.CommitPreservingArchive(tx);
    }
}
