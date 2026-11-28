using System.IO;
using System.Text;

namespace GmcSmeta.Launcher;

/// <summary>Atomically published installation pointer. Legacy states can launch,
/// but only a verified package with known schema is eligible for rollback.</summary>
internal sealed partial record InstalledApp
{
    public required string Version { get; init; }
    public required string Flavor { get; init; }
    public required string Directory { get; init; }
    public required string Exe { get; init; }
    public string? Sha256 { get; init; }
    public string InstalledUtc { get; init; } = "";
    public int? PackageSchema { get; init; }
    public string? ManifestHash { get; init; }
    public string? SkipRelease { get; init; }

    public static InstalledApp? Load(AppPaths paths) => Read(paths.Apps, paths.StateFile, allowDemoSeed: paths.IsDemo);

    internal static InstalledApp? Read(string apps, string stateFile, bool requireExe = true, bool allowDemoSeed = false)
    {
        try
        {
            if (!File.Exists(stateFile)) return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using var stream = new FileStream(stateFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } raw)
            {
                var index = raw.IndexOf('=');
                if (index <= 0) continue;
                if (!values.TryAdd(raw[..index].Trim(), raw[(index + 1)..].Trim())) return null;
            }
            if (!values.TryGetValue("version", out var version) ||
                !AppVersion.CanOfferRelease(null, version) ||
                !values.TryGetValue("flavor", out var flavor) || flavor is not ("fx" or "sc") ||
                !values.TryGetValue("directory", out var directory) ||
                !values.TryGetValue("exe", out var exe) ||
                !Path.IsPathFullyQualified(directory) || !Path.IsPathFullyQualified(exe))
                return null;
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(apps));
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            exe = Path.GetFullPath(exe);
            var expected = version + "-" + flavor;
            var folder = Path.GetFileName(directory);
            var suffixed = folder.StartsWith(expected + "-", StringComparison.OrdinalIgnoreCase) &&
                folder.Length == expected.Length + 7 && folder[(expected.Length + 1)..].All(char.IsAsciiDigit);
            var demoSeed = allowDemoSeed && version == "0.0.0" && flavor == "sc" &&
                DemoReleasePolicy.IsSeedFolder(folder);
            if (!Same(Path.GetDirectoryName(directory), root) ||
                !(Same(folder, expected) || suffixed || demoSeed) ||
                !Same(exe, Path.Combine(directory, "GMC-Smeta-Next.exe")) ||
                (requireExe && !File.Exists(exe)) ||
                (System.IO.Directory.Exists(directory) && IsReparse(directory)) ||
                (File.Exists(exe) && IsReparse(exe)))
                return null;
            var sha = values.GetValueOrDefault("sha256");
            var manifest = values.GetValueOrDefault("manifestHash");
            var schemaText = values.GetValueOrDefault("packageSchema");
            var skip = values.GetValueOrDefault("skipRelease");
            int? schema = null;
            if (!string.IsNullOrEmpty(schemaText))
            {
                if (!int.TryParse(schemaText, out var number) || number <= 0) return null;
                schema = number;
            }
            if (demoSeed && (!string.IsNullOrEmpty(sha) || !string.IsNullOrEmpty(manifest) ||
                schema.HasValue || !string.IsNullOrEmpty(skip))) return null;
            if ((!string.IsNullOrEmpty(sha) && !PackageIntegrity.IsHash(sha)) ||
                (!string.IsNullOrEmpty(manifest) && !PackageIntegrity.IsHash(manifest)) ||
                (!string.IsNullOrEmpty(skip) && !AppVersion.CanOfferRelease(null, skip)) ||
                (schema.HasValue != !string.IsNullOrEmpty(manifest))) return null;
            return new InstalledApp
            {
                Version = version, Flavor = flavor, Directory = directory, Exe = exe,
                Sha256 = sha, InstalledUtc = values.GetValueOrDefault("installedUtc") ?? "",
                PackageSchema = schema, ManifestHash = manifest, SkipRelease = skip
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public void Save(AppPaths paths) => Publish(paths.Apps, paths.StateFile, paths.PreviousStateFile, paths.IsDemo);

    internal void Publish(string apps, string stateFile, string previousStateFile, bool allowDemoSeed = false)
    {
        var old = Read(apps, stateFile, allowDemoSeed: allowDemoSeed);
        // Keep the previous pointer before switching. If either write fails,
        // the old current pointer remains usable. Never remove its files.
        if (old is not null && !Same(old.Exe, Exe)) old.Write(previousStateFile);
        Write(stateFile);
    }

    internal static InstalledApp Rollback(string apps, string stateFile, string previousStateFile)
    {
        // The current EXE may have disappeared, but its recorded schema must
        // still be known. An unknown or different schema is not safe to roll back.
        var current = Read(apps, stateFile, requireExe: false);
        var previous = Read(apps, previousStateFile);
        if (current is null || previous is null || Same(current.Exe, previous.Exe) ||
            current.PackageSchema is not int schema || previous.PackageSchema != schema ||
            !AppVersion.CanOfferRelease(previous.Version, current.Version) ||
            !PackageIntegrity.IsHash(previous.Sha256) ||
            !PackageIntegrity.Verify(previous.Directory, previous.Version, schema, previous.ManifestHash))
            throw new InvalidOperationException("Нет проверенной предыдущей версии с той же схемой БД. Возврат отменён.");
        var rollback = previous with
        {
            SkipRelease = current.Version,
            InstalledUtc = DateTime.UtcNow.ToString("O")
        };
        // One atomic pointer write includes the rejected release, so a detached
        // worker cannot immediately undo a successful rollback.
        rollback.Write(stateFile);
        return rollback;
    }

    internal bool IsBlockedRelease(string offered) =>
        !string.IsNullOrEmpty(SkipRelease) &&
        AppVersion.TryParse(SkipRelease, out var blocked) &&
        AppVersion.TryParse(offered, out var candidate) && candidate <= blocked;

    private void Write(string path)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new[]
        {
            "version=" + Version, "flavor=" + Flavor, "directory=" + Directory, "exe=" + Exe,
            "sha256=" + (Sha256 ?? ""), "installedUtc=" + InstalledUtc,
            "packageSchema=" + (PackageSchema?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""),
            "manifestHash=" + (ManifestHash ?? ""), "skipRelease=" + (SkipRelease ?? "")
        };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                foreach (var line in lines) writer.WriteLine(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
