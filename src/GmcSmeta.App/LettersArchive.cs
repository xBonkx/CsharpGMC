using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core;
using GmcSmeta.Core.Domain;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Windows.Media.Imaging;

namespace GmcSmeta.App;

/// <summary>
/// Immutable original files, staged releases and checksummed backups.
/// All DB metadata is in SQL Server; this archive never creates a local DB.
/// Share Root over an appropriately secured network drive for multi-client operation.
/// </summary>
public sealed class LettersArchive
{
    public SqlServerLetters Sql { get; }
    private readonly string _connectionString;
    public string Root { get; }

    public LettersArchive(string connectionString, string? root = null)
    {
        Root = Path.GetFullPath(root ?? Environment.GetEnvironmentVariable("GMC_SMETA_LETTERS_ROOT")
            ?? Path.Combine(GmcSmeta.Core.Demo.DemoMode.LocalData, "GMC-Smeta-Letters-Next"));
        var program = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (Root.Equals(program.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            Root.StartsWith(program, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Архив не должен находиться внутри каталога программы.");
        _connectionString = connectionString;
        Sql = new SqlServerLetters(connectionString);
        Directory.CreateDirectory(Path.Combine(Root, "files"));
        Directory.CreateDirectory(Path.Combine(Root, "exports"));
    }

    public string Resolve(string relative, string folder)
    {
        relative = LettersRules.SafeArchiveRelative(relative, folder);
        var basePath = Path.GetFullPath(Path.Combine(Root, folder)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь выходит за пределы архива.");
        return path;
    }

    public string FilePath(LetterAttachment file)
    {
        var path = Resolve(file.Path, "files");
        if (!File.Exists(path))
            throw new FileNotFoundException("Вложение отсутствует: " + file.Title, path);
        using var stream = File.OpenRead(path);
        var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!digest.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма вложения не совпадает: " + file.Title);
        return path;
    }

    private static int Pages(string path)
    {
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            if (pdf.PageCount < 1) throw new InvalidDataException("PDF не содержит страниц.");
            return pdf.PageCount;
        }
        var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("Изображение не распознано.");
        return 1;
    }

    public LetterAttachment AddFile(LetterDocument letter, string input, string kind)
    {
        if (letter.Sent is not null && kind != "Подтверждение отправки")
            throw new InvalidOperationException("После отправки можно добавлять только подтверждение.");
        if (kind is not ("Приложение" or "Скан письма" or "Подтверждение отправки"))
            throw new ArgumentException("Неизвестное назначение вложения.");
        var ext = Path.GetExtension(input).ToLowerInvariant();
        if (ext is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
            throw new InvalidDataException("Допустимы PDF, PNG и JPEG.");
        var pages = Pages(input);
        var id = Guid.NewGuid();
        var relative = $"files/{id:N}{ext}";
        var dest = Resolve(relative, "files");
        var temporary = dest + ".tmp-" + Guid.NewGuid().ToString("N");
        var hash = "";
        var ownsTemporary = false;
        var published = false;
        try
        {
            using (var source = File.OpenRead(input))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                source.CopyTo(output);
                output.Flush(true);
            }
            using (var stream = File.OpenRead(temporary))
                hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (letter.Files.Any(f => f.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Этот файл уже добавлен.");
            File.Move(temporary, dest); // unique target; publication precedes SQL commit
            published = true;
            var item = new LetterAttachment
            {
                Id = id, Title = Path.GetFileNameWithoutExtension(input), Kind = kind,
                Path = relative, Pages = pages, Sha256 = hash, Included = kind == "Приложение"
            };
            letter.Files.Add(item);
            try { Sql.Save(letter); }
            catch (LetterCommitUncertainException) { throw; }
            catch { letter.Files.Remove(item); throw; }
            return item;
        }
        catch (LetterCommitUncertainException ex)
        {
            throw new LetterCommitUncertainException(ex.Message + "\nФайл сохранён: " + dest, ex);
        }
        catch
        {
            if (published && File.Exists(dest)) File.Delete(dest);
            throw;
        }
        finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Required(LetterDocument d)
    {
        foreach (var (value, caption) in new[]
        {
            (d.Number, "исходящий номер"), (d.Subject, "тему"), (d.Recipient, "адресата")
        })
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Заполните " + caption + ".");
        if (!d.UseScan && (string.IsNullOrWhiteSpace(d.Sender) || string.IsNullOrWhiteSpace(d.Signer)))
            throw new ArgumentException("Заполните реквизиты отправителя и подписанта.");
        if (!d.UseScan && string.IsNullOrWhiteSpace(LettersPdf.BodyText(d.Body)))
            throw new ArgumentException("Введите текст письма.");
        if (d.UseScan && d.Files.All(x => x.Kind != "Скан письма"))
            throw new ArgumentException("Добавьте скан письма.");
    }

    public string Preview(LetterDocument letter)
    {
        Sql.Save(letter);
        var dir = Path.Combine(Root, "exports", "preview");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, letter.Id.ToString("N") + ".pdf");
        LettersPdf.Render(letter, path, FilePath);
        return path;
    }

    public string Build(LetterDocument letter, bool combine)
    {
        if (letter.Sent is not null) throw new InvalidOperationException("Отправленное письмо нельзя перевыпустить.");
        Sql.Save(letter);
        Required(letter);
        var included = letter.Files.Where(f => f.Kind == "Приложение" && f.Included).ToArray();
        var originals = included.Select(FilePath).ToArray();
        var relative = $"exports/{letter.Id:N}/{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}";
        var destination = Resolve(relative, "exports");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var published = false;
        var releaseCount = letter.Releases.Count;
        try
        {
            LettersPdf.Render(letter, Path.Combine(staging, "Письмо.pdf"), FilePath);
            File.WriteAllText(Path.Combine(staging, "Письмо.html"), LettersPdf.Html(letter), new UTF8Encoding(false));
            var appDir = Path.Combine(staging, "Приложения");
            Directory.CreateDirectory(appDir);
            var parts = new List<string> { Path.Combine(staging, "Письмо.pdf") };
            for (var i = 0; i < included.Length; i++)
            {
                var item = included[i];
                var title = $"{i + 1:00}_{LettersRules.SafeName(item.Title)}";
                var copy = Path.Combine(appDir, title + Path.GetExtension(originals[i]).ToLowerInvariant());
                File.Copy(originals[i], copy, overwrite: false);
                using (var stream = File.OpenRead(copy))
                {
                    if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Оригинал изменился при сборке: " + item.Title);
                }
                if (combine)
                {
                    if (Path.GetExtension(copy).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                        parts.Add(copy);
                    else
                    {
                        var imagePdf = Path.Combine(appDir, title + ".pdf");
                        LettersPdf.ImagePage(copy, imagePdf);
                        parts.Add(imagePdf);
                    }
                }
            }
            if (combine) LettersPdf.Merge(parts, Path.Combine(staging, "Письмо_с_приложениями.pdf"));
            var snapshot = JsonSerializer.Deserialize<LetterDocument>(
                JsonSerializer.Serialize(letter, LettersRules.Json), LettersRules.Json)!;
            snapshot.Releases.Add(relative);
            var list = new List<object>();
            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                using var stream = File.OpenRead(file);
                list.Add(new
                {
                    file = Path.GetRelativePath(staging, file).Replace('\\', '/'),
                    sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()
                });
            }
            var manifest = JsonSerializer.Serialize(new
            {
                snapshot,
                fingerprint = LettersRules.Fingerprint(letter),
                combined = combine,
                files = list
            }, new JsonSerializerOptions(LettersRules.Json) { WriteIndented = true });
            var manifestPath = Path.Combine(staging, "Состав.json");
            File.WriteAllText(manifestPath, manifest, new UTF8Encoding(false));
            Directory.Move(staging, destination);
            published = true;
            using var ms = File.OpenRead(Path.Combine(destination, "Состав.json"));
            var manifestHash = Convert.ToHexString(SHA256.HashData(ms)).ToLowerInvariant();
            Sql.Save(letter, new LetterRelease(relative, LettersRules.Fingerprint(letter), combine, manifestHash));
            return destination;
        }
        catch (LetterCommitUncertainException ex)
        {
            throw new LetterCommitUncertainException(ex.Message + "\nПапка выпуска сохранена: " + destination, ex);
        }
        catch
        {
            while (letter.Releases.Count > releaseCount) letter.Releases.RemoveAt(letter.Releases.Count - 1);
            if (published && Directory.Exists(destination)) Directory.Delete(destination, true);
            throw;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public string ReleasePath(string relative)
    {
        var folder = Resolve(relative, "exports");
        var manifest = Path.Combine(folder, "Состав.json");
        if (!File.Exists(manifest)) throw new InvalidDataException("Опись выпуска отсутствует.");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest, Encoding.UTF8));
        foreach (var entry in doc.RootElement.GetProperty("files").EnumerateArray())
        {
            var path = entry.GetProperty("file").GetString() ?? "";
            var expected = entry.GetProperty("sha256").GetString() ?? "";
            var normalized = path.Replace('\\', '/');
            var components = normalized.Split('/');
            if (Path.IsPathRooted(normalized) || normalized.Contains(':') ||
                components.Any(s => s is "" or "." or ".."))
                throw new InvalidDataException("Недопустимый путь в описи.");
            var file = Path.GetFullPath(Path.Combine(folder, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Опись выходит из выпуска.");
            using var stream = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Файл выпуска изменён: " + path);
        }
        return folder;
    }

    /// <summary>Portable logical backup, not a SQL Server .bak and not a SQLite database.</summary>
    public void Backup(string zipPath)
    {
        zipPath = Path.GetFullPath(zipPath);
        if (zipPath.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Копия должна находиться вне действующего архива.");
        ExportFilePublication.Write(zipPath, stream => WriteBackup(stream));
    }

    private void WriteBackup(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var letters = Sql.List();
        static void AddJson(ZipArchive archive, string name, object value)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(JsonSerializer.Serialize(value, LettersRules.Json));
        }
        AddJson(zip, "letters.json", letters);
        AddJson(zip, "revisions.json", letters.ToDictionary(d => d.Id.ToString("N"), d => d.Revision));
        AddJson(zip, "history.json", SqlServerLettersBackup.ExportHistory(_connectionString));
        foreach (var folder in new[] { "files", "exports" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, folder), "*", SearchOption.AllDirectories))
            {
                var name = Path.GetRelativePath(Root, file).Replace('\\', '/');
                if (name.Split('/').Any(x => x.StartsWith(".stage-", StringComparison.Ordinal) ||
                                            x.Contains(".tmp-", StringComparison.Ordinal)))
                    continue;
                zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
            }
        }
    }

    public static void ValidateRestoreZip(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.GetEntry("letters.json") is null) throw new InvalidDataException("Отсутствует letters.json.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            var s = e.FullName;
            if (s.Contains('\\') || s.StartsWith('/') || s.Contains(':') ||
                s.Split('/').Any(x => x is "." or "..") ||
                !(s is "letters.json" or "revisions.json" or "history.json" || s.StartsWith("files/", StringComparison.Ordinal) ||
                  s.StartsWith("exports/", StringComparison.Ordinal)) ||
                !names.Add(s))
                throw new InvalidDataException("Недопустимый ZIP path: " + s);
            if (((e.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Символические ссылки в ZIP запрещены.");
            if (s.Any(char.IsControl)) throw new InvalidDataException("Недопустимый символ ZIP path.");
            total += e.Length;
            if (total > 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("Резервная копия превышает безопасный лимит 4 ГБ.");
        }
    }

    /// <summary>
    /// Restore to a new empty folder and an empty GmcSmetaPilot_* SQL letter store.
    /// Files are validated before any SQL INSERT; on failure, the newly published folder
    /// is removed and the SQL transaction is rolled back by the restore service.
    /// </summary>
    public static LettersArchive RestoreBackup(string connectionString, string zipPath, string destination)
    {
        ValidateRestoreZip(zipPath);
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
        var executableDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (destination.Equals(executableDir, StringComparison.OrdinalIgnoreCase) ||
            destination.StartsWith(executableDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Восстановление внутри каталога программы запрещено.");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException("Выберите пустую папку для восстановления.");
        if (Directory.Exists(destination)) Directory.Delete(destination);
        var staging = Path.Combine(Path.GetDirectoryName(destination)!,
            ".gmc-letters-restore-" + Guid.NewGuid().ToString("N"));
        var published = false;
        var restoredSql = false;
        try
        {
            Directory.CreateDirectory(staging);
            using var zip = ZipFile.OpenRead(zipPath);
            // Validation above rejected traversal, absolute paths, NTFS drive paths and links.
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(staging, relative));
                if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Путь ZIP выходит из каталога.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }
            var docs = JsonSerializer.Deserialize<List<LetterDocument>>(
                File.ReadAllText(Path.Combine(staging, "letters.json")), LettersRules.Json)
                ?? throw new InvalidDataException("Некорректная опись писем.");
            var revisionsFile = Path.Combine(staging, "revisions.json");
            var revisions = File.Exists(revisionsFile)
                ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(revisionsFile))
                : null;
            foreach (var doc in docs)
            {
                if (revisions?.TryGetValue(doc.Id.ToString("N"), out var revision) == true)
                    doc.Revision = revision;
                LettersRules.Validate(doc);
                foreach (var attachment in doc.Files)
                {
                    var path = Path.Combine(staging, attachment.Path.Replace('/', Path.DirectorySeparatorChar));
                    using var file = File.OpenRead(path);
                    if (!Convert.ToHexString(SHA256.HashData(file)).Equals(
                        attachment.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Контрольная сумма оригинала не совпадает: " + attachment.Title);
                }
            }
            var historyFile = Path.Combine(staging, "history.json");
            var history = File.Exists(historyFile)
                ? JsonSerializer.Deserialize<List<SqlServerLettersBackup.HistoryRecord>>(
                    File.ReadAllText(historyFile), LettersRules.Json)
                : null;
            history ??= new();
            var releases = new List<SqlServerLettersBackup.ReleaseRecord>();
            foreach (var doc in docs)
            {
                foreach (var relative in doc.Releases)
                {
                    LettersRules.SafeArchiveRelative(relative, "exports");
                    var releaseDir = Path.GetFullPath(Path.Combine(staging,
                        relative.Replace('/', Path.DirectorySeparatorChar)));
                    var manifest = Path.Combine(releaseDir, "Состав.json");
                    using var document = JsonDocument.Parse(File.ReadAllText(manifest));
                    foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
                    {
                        var item = file.GetProperty("file").GetString() ?? "";
                        var expected = file.GetProperty("sha256").GetString() ?? "";
                        if (item.Contains('\\') || item.Contains(':') || item.StartsWith('/') ||
                            item.Split('/').Any(x => x is "." or ".." or ""))
                            throw new InvalidDataException("Опись выпуска содержит небезопасный путь.");
                        var path = Path.GetFullPath(Path.Combine(releaseDir,
                            item.Replace('/', Path.DirectorySeparatorChar)));
                        if (!path.StartsWith(releaseDir + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Файл выпуска вне каталога.");
                        using var original = File.OpenRead(path);
                        if (!Convert.ToHexString(SHA256.HashData(original)).Equals(
                            expected, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Контрольная сумма выпуска не совпадает: " + item);
                    }
                    var fingerprint = document.RootElement.TryGetProperty("fingerprint", out var field)
                        ? field.GetString() ?? "" : LettersRules.Fingerprint(doc);
                    var combined = document.RootElement.GetProperty("combined").GetBoolean();
                    using var manifestStream = File.OpenRead(manifest);
                    releases.Add(new SqlServerLettersBackup.ReleaseRecord(doc.Id, relative,
                        fingerprint, Convert.ToHexString(SHA256.HashData(manifestStream)).ToLowerInvariant(), combined));
                }
            }
            Directory.Move(staging, destination);
            published = true;
            SqlServerLettersBackup.Restore(connectionString, docs, history, releases);
            restoredSql = true;
            return new LettersArchive(connectionString, destination);
        }
        catch (LetterCommitUncertainException ex)
        {
            throw new LetterCommitUncertainException(ex.Message + "\nВосстановленные файлы сохранены: " + destination, ex);
        }
        catch (Exception ex) when (restoredSql)
        {
            throw new InvalidOperationException("SQL восстановлен, но открыть архив не удалось. Файлы сохранены: " + destination, ex);
        }
        catch
        {
            if (published && Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.CreateDirectory(destination);
            throw;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

}
