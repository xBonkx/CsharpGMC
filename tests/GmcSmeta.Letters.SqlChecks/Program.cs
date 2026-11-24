using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

/// <summary>
/// Creates and removes ONLY uniquely named GmcSmetaPilot_* databases on the ephemeral CI server.
/// Never connects to or modifies a production database. Linux Core-only test, no WPF needed.
/// </summary>
internal static class Program
{
    private static void Check(bool result, string reason)
    {
        if (!result) throw new Exception("SQL pilot regression: " + reason);
    }

    private static void Expect<T>(Action action, string reason) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name + ": " + reason);
    }

    private static string CreatePilot(SqlConnectionStringBuilder master, string name)
    {
        if (!name.StartsWith("GmcSmetaPilot_", StringComparison.Ordinal) ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new InvalidOperationException("Illegal CI database name.");
        using (var conn = new SqlConnection(master.ConnectionString))
        {
            conn.Open();
            using var cmd = new SqlCommand($"CREATE DATABASE [{name}]", conn);
            cmd.CommandTimeout = 45;
            cmd.ExecuteNonQuery();
        }
        var builder = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = name,
            ConnectTimeout = 10
        };
        using (var conn = new SqlConnection(builder.ConnectionString))
        {
            conn.Open();
            using var cmd = new SqlCommand(
                "CREATE TABLE dbo.meta([key] nvarchar(128) NOT NULL PRIMARY KEY, " +
                "[value] nvarchar(128) NOT NULL); " +
                "INSERT INTO dbo.meta ([key],[value]) VALUES (N'schema_version',N'28')", conn);
            cmd.ExecuteNonQuery();
        }
        return builder.ConnectionString;
    }

    private static void DropPilot(SqlConnectionStringBuilder master, string name)
    {
        if (!name.StartsWith("GmcSmetaPilot_", StringComparison.Ordinal)) return;
        try
        {
            SqlConnection.ClearAllPools();
            using var conn = new SqlConnection(master.ConnectionString);
            conn.Open();
            using var cmd = new SqlCommand(
                $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];", conn);
            cmd.CommandTimeout = 45;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { Console.Error.WriteLine("CI cleanup warning: " + ex.Message); }
    }

    private static int Main()
    {
        var source = Environment.GetEnvironmentVariable("GMC_LETTERS_CI_SQL_SERVER");
        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("GMC_LETTERS_CI_SQL_SERVER is required (isolated CI SQL Server only).");
            return 2;
        }
        var builder = new SqlConnectionStringBuilder(source)
        {
            InitialCatalog = "master", ConnectTimeout = 5
        };
        var ready = false;
        for (var i = 0; i < 35; i++)
        {
            try { using var probe = new SqlConnection(builder.ConnectionString); probe.Open(); ready = true; break; }
            catch (SqlException) { Thread.Sleep(2000); }
        }
        if (!ready) throw new TimeoutException("Ephemeral SQL Server container did not start.");
        var root = Guid.NewGuid().ToString("N")[..9];
        var firstName = "GmcSmetaPilot_LettersCI_" + root;
        var secondName = "GmcSmetaPilot_RestoreCI_" + root;
        var createdFirst = false;
        var createdSecond = false;
        var passed = 0;
        try
        {
            var connection = CreatePilot(builder, firstName);
            createdFirst = true;
            var sql = new SqlServerLetters(connection);
            _ = new SqlServerLetters(connection); // repeated migration must be safe
            Check(sql.List().Count == 0, "new schema is not empty");
            passed++;

            var concurrent = new ConcurrentBag<LetterDocument>();
            Parallel.For(0, 10, _ =>
            {
                // Two+ independent clients allocate the same calendar day in parallel.
                concurrent.Add(new SqlServerLetters(connection).Create());
            });
            var all = concurrent.ToArray();
            Check(all.Length == 10 &&
                  all.Select(x => x.Number).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 10,
                  "two or more clients received duplicate outgoing numbers");
            passed++;

            var primary = sql.Get(all[0].Id);
            primary.Subject = "Согласование инженерных систем";
            primary.Body = "<p>Просим согласовать изменения.</p>";
            primary.Recipient = "ООО Заказчик";
            primary.Sender = "ООО ГМЦ";
            primary.Signer = "Руководитель — Иванов";
            sql.Save(primary);
            var reopened = new SqlServerLetters(connection).Get(primary.Id);
            Check(reopened.Subject == primary.Subject && reopened.Revision == primary.Revision,
                "roundtrip or revision lost after reopening");
            passed++;

            var stale = sql.Get(primary.Id);
            reopened.Subject = "Версия A";
            sql.Save(reopened);
            stale.Subject = "Версия B";
            Expect<DBConcurrencyException>(() => sql.Save(stale), "stale writer must not overwrite changes");
            Check(sql.Get(primary.Id).Subject == "Версия A", "failed conflict rolled back valid data");
            passed++;

            var manual = sql.Get(all[1].Id);
            manual.NumberAuto = false;
            manual.Number = "Мой-123";
            sql.Save(manual);
            var duplicate = sql.Get(all[2].Id);
            duplicate.NumberAuto = false;
            duplicate.Number = "мой-123";
            Expect<InvalidOperationException>(() => sql.Save(duplicate), "duplicate manual number");
            Check(sql.Get(duplicate.Id).Number != duplicate.Number, "duplicate transaction did not roll back");
            passed++;

            var auto = sql.Get(all[3].Id);
            var date = LettersRules.ParseDate(auto.Date).AddDays(1);
            auto.Date = date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            sql.Save(auto);
            Check(auto.Number.StartsWith(date.ToString("dd.MM-", CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "date change did not allocate a new daily number");
            passed++;

            var releaseDocument = sql.Get(primary.Id);
            var snapshot = LettersRules.Fingerprint(releaseDocument);
            var relPath = $"exports/{primary.Id:N}/sql-integration-test";
            var release = new LetterRelease(relPath, snapshot, false, new string('a', 64));
            sql.Save(releaseDocument, release);
            sql.MarkSent(releaseDocument, releaseDocument.Date, "ЭДО", "Заказчик", relPath);
            Check(sql.Get(primary.Id).Status == "Отправлено", "sending status was not committed");
            passed++;

            var sent = sql.Get(primary.Id);
            sent.Subject += " CHANGED";
            Expect<InvalidOperationException>(() => sql.Save(sent), "sent letter must be immutable");
            Check(sql.Get(primary.Id).Subject == "Версия A", "sent mutation reached DB");
            passed++;

            var copy = sql.Copy(primary.Id);
            Check(copy.Id != primary.Id && copy.Sent is null && copy.Files.Count == 0 &&
                  copy.Releases.Count == 0 && copy.Number != sent.Number &&
                  copy.Status == "Черновик", "sent copy inherited protected state");
            passed++;

            var history = sql.ReadHistory(primary.Id);
            Check(history.Count >= 3 && history.All(h => h.Revision > 0),
                "history snapshots were not preserved");
            passed++;

            var restoreConnection = CreatePilot(builder, secondName);
            createdSecond = true;
            var archive = sql.List();
            var historyEntries = SqlServerLettersBackup.ExportHistory(connection);
            var releases = new[] { new SqlServerLettersBackup.ReleaseRecord(primary.Id, relPath,
                snapshot, new string('a', 64), false) };
            SqlServerLettersBackup.Restore(restoreConnection, archive, historyEntries, releases);
            var restored = new SqlServerLetters(restoreConnection);
            Check(restored.List().Count == archive.Count &&
                  restored.Get(primary.Id).Status == "Отправлено" &&
                  restored.ReadHistory(primary.Id).Count == history.Count,
                  "restore lost sending, history or letters");
            Expect<InvalidOperationException>(
                () => SqlServerLettersBackup.Restore(restoreConnection, archive, historyEntries, releases),
                "restore must refuse non-empty SQL archive");
            passed++;

            Console.WriteLine($"LETTERS REAL SQL SERVER PILOT CHECKS PASSED: {passed}/{passed}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("LETTERS REAL SQL SERVER PILOT CHECK FAILED: " + ex);
            Console.Error.WriteLine($"Successful SQL checks before failure: {passed}");
            return 1;
        }
        finally
        {
            if (createdSecond) DropPilot(builder, secondName);
            if (createdFirst) DropPilot(builder, firstName);
        }
    }
}
