using System.Text.RegularExpressions;

namespace GmcSmeta.Launcher;

internal static class DemoReleasePolicy
{
    internal const string ExecutableName = "GMC-Smeta-CSharp-Test.exe";
    internal const string Prefix = "GmcSmetaPilot_Test_Agreement_Demo_";
    internal const string CapabilityFile = "demo-capability.txt";

    internal static bool IsDemoEntry(string[] args, string? exeName) =>
        string.Equals(exeName, ExecutableName, StringComparison.OrdinalIgnoreCase) ||
        args.Any(a => a.Equals("--demo-launch", StringComparison.OrdinalIgnoreCase));

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

    internal static void RequireCapability(string? contents)
    {
        var lines = (contents ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (!lines.SequenceEqual(new[] { "GMC-DEMO-1", "schema=32" }))
            throw new InvalidOperationException("Релиз не подтверждает DEMO схемы 32. Для другой схемы нужен актуальный генератор; прежняя сборка сохранена.");
    }

    internal static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != "." && relative != ".." && !Path.IsPathRooted(relative) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
