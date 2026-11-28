using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace GmcSmeta.Launcher;

/// <summary>Package metadata is inside the hash-verified archive. Seal the
/// extracted files once and recheck all of them only on an explicit rollback.</summary>
internal static class PackageIntegrity
{
    internal const string VersionFile = "gmc-package-version.txt";
    internal const string SchemaFile = "gmc-package-schema.txt";
    internal const string ManifestFile = "gmc-install-files.json";

    internal static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    internal static (int? Schema, string? ManifestHash) Seal(string directory, string version)
    {
        var versionFile = Path.Combine(directory, VersionFile);
        var schemaFile = Path.Combine(directory, SchemaFile);
        var manifest = Path.Combine(directory, ManifestFile);
        if (File.Exists(manifest)) throw new InvalidOperationException("Архив содержит служебный локальный манифест.");
        // Older published packages remain installable; they cannot be rollback targets.
        if (!File.Exists(versionFile) && !File.Exists(schemaFile)) return (null, null);
        var schema = ReadSchema(directory, version);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(HashFiles(directory));
        using (var stream = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        return (schema, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    internal static bool Verify(string directory, string version, int schema, string? manifestHash)
    {
        if (!IsHash(manifestHash)) return false;
        try
        {
            if (ReadSchema(directory, version) != schema) return false;
            var manifest = Path.Combine(directory, ManifestFile);
            if ((File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0) return false;
            var bytes = File.ReadAllBytes(manifest);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), manifestHash,
                    StringComparison.OrdinalIgnoreCase)) return false;
            var actual = JsonSerializer.SerializeToUtf8Bytes(HashFiles(directory));
            return bytes.AsSpan().SequenceEqual(actual);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static int ReadSchema(string directory, string version)
    {
        var declared = File.ReadAllText(Path.Combine(directory, VersionFile)).Trim();
        if (!string.Equals(declared, version, StringComparison.Ordinal) ||
            !int.TryParse(File.ReadAllText(Path.Combine(directory, SchemaFile)).Trim(), out var schema) || schema <= 0)
            throw new InvalidOperationException("Версия или схема в архиве не соответствует выпуску.");
        return schema;
    }

    private static SortedDictionary<string, string> HashFiles(string directory)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        // Do not traverse or accept junctions/reparse entries in an installation.
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var folder))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("В пакете найден перенаправленный путь.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                var relative = Path.GetRelativePath(directory, entry).Replace(Path.DirectorySeparatorChar, '/');
                if (relative == ManifestFile) continue;
                using var stream = File.OpenRead(entry);
                hashes.Add(relative, Convert.ToHexString(SHA256.HashData(stream)));
            }
        }
        if (!hashes.ContainsKey("GMC-Smeta-Next.exe"))
            throw new InvalidOperationException("В пакете отсутствует EXE программы.");
        return hashes;
    }
}
