using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed record PilotBackupEntry(string Name, long Length, string Sha256);
public sealed record PilotBackupManifest(int FormatVersion, string SourceDatabase, string Kind,
    DateTimeOffset CreatedUtc, List<PilotBackupEntry> Files);

/// <summary>
/// Disposable PILOT attachment package, not an SQL Server database backup.
/// No RESTORE DATABASE command is ever exposed. Production database names and
/// filesystem destinations are rejected before any write.
/// </summary>
public static class BackupPilotPackage
{
    private const string Kind = "pilot-attachments-only";
    private const int MaxFiles = 10000;
    private const long MaxFileBytes = 128L * 1024 * 1024;
    private const long MaxTotalBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string RequirePilot(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase) ||
            database.Length <= "GmcSmetaPilot_".Length)
            throw new InvalidOperationException("Резервирование разрешено только на пилотной базе.");
        return database;
    }

    /// <summary>
    /// Package files under an explicitly disposable GmcSmetaPilot_* directory.
    /// Does not read, export, copy or mutate SQL database tables.
    /// </summary>
    public static PilotBackupManifest Create(string connectionString, string sourceDirectory,
        string zipPath)
    {
        var db = RequirePilot(connectionString);
        var source = RequireDisposableDirectory(sourceDirectory, mustExist: true);
        var destination = Path.GetFullPath(zipPath);
        RejectReparseAncestors(destination);
        if (Inside(destination, source))
            throw new InvalidOperationException("Архив нельзя помещать внутрь исходного каталога.");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("Архив уже существует; перезапись запрещена.");

        var paths = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (paths.Count > MaxFiles) throw new InvalidDataException("Слишком много вложений.");
        foreach (var path in paths) RejectReparseAncestors(path);

        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var files = new List<PilotBackupEntry>();
            PilotBackupManifest? writtenManifest = null;
            long total = 0;
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                {
                    var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
                    ValidateName(relative);
                    if (!seen.Add(relative)) throw new InvalidDataException("Повторяющийся путь вложения.");
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length > MaxFileBytes || total + input.Length > MaxTotalBytes)
                        throw new InvalidDataException("Превышен лимит размера вложений.");
                    var entry = zip.CreateEntry("files/" + relative, CompressionLevel.Optimal);
                    using var output = entry.Open();
                    var (bytes, digest) = CopyAndHash(input, output, MaxFileBytes);
                    total += bytes;
                    if (total > MaxTotalBytes)
                        throw new InvalidDataException("Превышен общий лимит вложений.");
                    files.Add(new PilotBackupEntry(relative, bytes, digest));
                }
                writtenManifest = new PilotBackupManifest(1, db, Kind,
                    DateTimeOffset.UtcNow, files);
                var item = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using (var output = item.Open())
                    JsonSerializer.Serialize(output, writtenManifest, JsonOptions);
            }
            File.Move(temp, destination); // never overwrite an existing backup
            return writtenManifest!;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Verify names, entry count, sizes and SHA-256 without extracting.</summary>
    public static PilotBackupManifest Verify(string connectionString, string zipPath)
    {
        var db = RequirePilot(connectionString);
        using var zip = ZipFile.OpenRead(zipPath);
        return CheckArchive(zip, db);
    }

    /// <summary>
    /// Extract only into a NEW directory whose final component starts with
    /// GmcSmetaPilot_. Does not restore SQL, replace an existing directory or
    /// write any existing user archive/template.
    /// </summary>
    public static PilotBackupManifest ExtractToNewPilotDirectory(string connectionString,
        string zipPath, string targetDirectory)
    {
        var db = RequirePilot(connectionString);
        var target = RequireDisposableDirectory(targetDirectory, mustExist: false);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException("Папка восстановления уже существует.");
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, "." + Path.GetFileName(target) +
            "." + Guid.NewGuid().ToString("N") + ".tmp");
        using var zip = ZipFile.OpenRead(zipPath);
        var manifest = CheckArchive(zip, db);
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var file in manifest.Files)
            {
                var entry = zip.GetEntry("files/" + file.Name)!;
                var relative = file.Name.Replace('/', Path.DirectorySeparatorChar);
                var path = Path.GetFullPath(Path.Combine(staging, relative));
                if (!Inside(path, staging) || path == staging)
                    throw new InvalidDataException("Выход за пределы пилотного каталога.");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough);
                var (size, sha) = CopyAndHash(input, output, MaxFileBytes);
                output.Flush(flushToDisk: true);
                if (size != file.Length ||
                    !sha.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Вложение повреждено при восстановлении.");
            }
            Directory.Move(staging, target); // atomic publish after complete verification
            return manifest;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static PilotBackupManifest CheckArchive(ZipArchive zip, string db)
    {
        var manifestEntry = zip.GetEntry("manifest.json")
            ?? throw new InvalidDataException("В архиве нет manifest.json.");
        if (manifestEntry.Length is <= 0 or > 1048576)
            throw new InvalidDataException("Недопустимый размер манифеста.");
        PilotBackupManifest manifest;
        using (var input = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<PilotBackupManifest>(input)
                ?? throw new InvalidDataException("Пустой манифест.");

        if (manifest.FormatVersion != 1 || manifest.Kind != Kind ||
            !string.Equals(manifest.SourceDatabase, db, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Архив не соответствует пилотной базе или формату.");
        if (manifest.Files is null || manifest.Files.Count > MaxFiles ||
            zip.Entries.Count != manifest.Files.Count + 1 ||
            zip.Entries.Count(e => e.FullName == "manifest.json") != 1)
            throw new InvalidDataException("Несоответствие состава архива.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            ValidateName(file.Name);
            if (!seen.Add(file.Name) || file.Length < 0 ||
                file.Length > MaxFileBytes || total + file.Length > MaxTotalBytes ||
                file.Sha256 is null || file.Sha256.Length != 64 ||
                !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Некорректный manifest.json.");
            total += file.Length;
            var entry = zip.GetEntry("files/" + file.Name)
                ?? throw new InvalidDataException("Отсутствует вложение из манифеста.");
            if (entry.Length != file.Length || IsSymbolicLink(entry))
                throw new InvalidDataException("Недопустимый тип или размер вложения.");
            using var input = entry.Open();
            var (length, digest) = CopyAndHash(input, Stream.Null, MaxFileBytes);
            if (length != file.Length ||
                !digest.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 вложения не совпадает.");
        }

        // No extra entries, duplicate case-insensitive names, or traversal aliases.
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName == "manifest.json") continue;
            if (!entry.FullName.StartsWith("files/", StringComparison.Ordinal) ||
                !seen.Contains(entry.FullName["files/".Length..]) ||
                IsSymbolicLink(entry))
                throw new InvalidDataException("В архиве есть посторонние записи.");
        }
        return manifest;
    }

    private static (long Length, string Sha256) CopyAndHash(Stream input, Stream output, long max)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            total += count;
            if (total > max) throw new InvalidDataException("Превышен лимит распаковки.");
            hash.AppendData(buffer, 0, count);
            output.Write(buffer, 0, count);
        }
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static string RequireDisposableDirectory(string path, bool mustExist)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(full);
        if (!name.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase) ||
            name.Length <= "GmcSmetaPilot_".Length ||
            (mustExist && !Directory.Exists(full)))
            throw new InvalidOperationException("Требуется отдельная папка GmcSmetaPilot_*.");
        RejectReparseAncestors(full);
        return full;
    }

    private static void ValidateName(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 ||
            path.StartsWith('/') || path.Contains('\\') ||
            path.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0 ||
            path.Any(char.IsControl) ||
            path.Split('/').Any(segment => segment is "" or "." or ".." ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Length > 255 || IsReservedWindowsName(segment)))
            throw new InvalidDataException("Опасный путь в архиве.");
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                      stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] is >= '1' and <= '9');
    }

    private static bool Inside(string path, string root) =>
        Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool IsSymbolicLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static void RejectReparseAncestors(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Символические ссылки запрещены.");
        for (var dir = new DirectoryInfo(full); dir is not null; dir = dir.Parent)
            if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Каталог содержит символическую ссылку.");
    }
}
