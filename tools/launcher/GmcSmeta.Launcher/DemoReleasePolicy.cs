using System.Text.RegularExpressions;

namespace GmcSmeta.Launcher;

internal static class DemoReleasePolicy
{
    internal const string ExecutableName = "GMC-Smeta-CSharp-Test.exe";
    internal const string Prefix = "GmcSmetaPilot_Test_Agreement_Demo_";
    internal const string CapabilityFile = "demo-capability.txt";

    internal static bool IsDemoEntry(string[] args, string? exeName) =>
        string.Equals(exeName, ExecutableName, StringComparison.OrdinalIgnoreCase) ||
        args.Any(a => a.Equals("--demo-launch", StringComparison.OrdinalIgnoreCase) ||
                      a.Equals("--demo", StringComparison.OrdinalIgnoreCase));

    internal static void ValidateDatabase(string database)
    {
        if (!Regex.IsMatch(database, @"\A" + Prefix + @"[0-9a-f]{12}\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Разрешена только сгенерированная тестовая база C#.");
    }

    internal static (string Database, bool NoUpdate) Parse(string[] args)
    {
        if (args.Length is not (2 or 3) || args[0] != "--demo-launch" ||
            (args.Length == 3 && args[2] != "--no-update"))
            throw new InvalidOperationException("Запустите тестовый ярлык: --demo-launch <имя тестовой базы>.");
        ValidateDatabase(args[1]);
        return (args[1], args.Length == 3);
    }

    // The test child must not inherit office SQL, module roots or trace destinations.
    internal static void PrepareChildEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var name in new[] { "GMC_SMETA_SQL_CONNECTION", "GMC_SMETA_SQL_DATABASE",
                     "GMC_SMETA_HOME", "GMC_SMETA_LAUNCHER", "GMC_SMETA_STARTUP_TRACE_DIR",
                     "GMC_SMETA_PTO_ROOT", "GMC_SMETA_LETTERS_ROOT", "GMC_SMETA_PASSPORTS_ROOT" })
            environment.Remove(name);
        environment["GMC_SMETA_NO_UPDATE"] = "1";
        environment["GMC_SMETA_AUTO_RESTART"] = "0";
    }

    // Matches gmcsmeta/tools/install-demo-shortcuts.ps1.
    internal static bool IsSeedFolder(string folder) =>
        Regex.IsMatch(folder, @"\Aseed-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}\z", RegexOptions.CultureInvariant);

    internal static void RequireCandidateMetadata(string directory, string version)
    {
        var declared = File.ReadAllText(Path.Combine(directory, PackageIntegrity.VersionFile)).Trim();
        var schema = File.ReadAllText(Path.Combine(directory, PackageIntegrity.SchemaFile)).Trim();
        if (!string.Equals(declared, version, StringComparison.Ordinal) || schema != "32")
            throw new InvalidOperationException("Метаданные пакета не подтверждают версию релиза и тестовую схему 32.");
    }

    internal static void RequireCapability(string? contents)
    {
        var lines = (contents ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (!lines.SequenceEqual(new[] { "GMC-DEMO-1", "schema=32" }))
            throw new InvalidOperationException("Релиз не подтверждает DEMO схемы 32. Для другой схемы нужен актуальный генератор; прежняя сборка сохранена.");
    }

    // The local installer seed has no release seal. A downloaded release must
    // retain its schema and seal; recovery additionally verifies every file.
    internal static void RequireInstalledIntegrity(InstalledApp app, bool verifyFiles)
    {
        var marker = Path.Combine(app.Directory, CapabilityFile);
        RequireCapability(File.Exists(marker) ? File.ReadAllText(marker) : null);
        if (app.Version == "0.0.0" && app.Flavor == "sc" &&
            IsSeedFolder(Path.GetFileName(app.Directory)))
            return;
        RequireCandidateMetadata(app.Directory, app.Version);
        if (app.PackageSchema != 32 || !PackageIntegrity.IsHash(app.Sha256) ||
            !PackageIntegrity.IsHash(app.ManifestHash) ||
            (verifyFiles && !PackageIntegrity.Verify(app.Directory, app.Version, 32, app.ManifestHash)))
            throw new InvalidOperationException("Установленная тестовая сборка не прошла проверку целостности.");
    }

    internal static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != "." && relative != ".." && !Path.IsPathRooted(relative) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
