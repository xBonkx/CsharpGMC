using System.Diagnostics;

namespace GmcSmeta.Launcher;

internal static class DemoLauncher
{
    public static int Run(string[] args)
    {
        using var ui = new ConsoleUi();
        try
        {
            if (OfficeLauncher.IsOfficeExecutable)
                throw new InvalidOperationException("Рабочий лаунчер не используется для тестов.");
            var (database, noUpdate) = DemoReleasePolicy.Parse(args);
            var paths = AppPaths.CreateDemo(database);
            using var mutex = new Mutex(false, @"Local\GmcSmetaDemoUpdate_" + database);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new InvalidOperationException("Тестовый лаунчер уже проверяет обновление.");
            try
            {
                var installed = Load(paths);
                if (!noUpdate && Environment.GetEnvironmentVariable("GMC_SMETA_NO_UPDATE") != "1")
                {
                    try
                    {
                        ui.ShowUpdate("ГМЦ-Смета C# — ТЕСТ", "Проверяем GitHub Releases. Рабочая программа не меняется.");
                        // Public repository only; never resolve office credentials or alternate repository.
                        using var releases = new GitHubReleases(GitHubReleases.DefaultRepository, null, ui);
                        var release = releases.TryGetLatest(null, out _, out _, out var error);
                        if (release is null)
                            throw new InvalidOperationException(error ?? "Нет опубликованного релиза.");
                        if (AppVersion.CanOfferRelease(installed?.Version, release.Version) &&
                            (installed is null || AppVersion.IsOlder(installed.Version, release.Version)))
                        {
                            ui.UpdatePhase("Тестовый режим: обновление " + release.Version,
                                "Проверяем контрольную сумму, поддержку DEMO и открытие сметы.");
                            installed = new AppInstaller(releases, paths, ui).Install(release,
                                directory => CheckCandidate(directory, database));
                        }
                    }
                    catch (Exception ex)
                    {
                        // Do not lose the previous verified state on any download,
                        // unpack, capability, or smoke failure. No fallback to office.
                        File.WriteAllText(Path.Combine(paths.Root, "last-update-error.txt"),
                            DateTime.UtcNow.ToString("O") + " " + ex.Message);
                        if (installed is null) throw;
                        ui.ShowUpdate("ГМЦ-Смета C# — ТЕСТ", "Обновление не применено: " + ex.Message);
                        ui.FinishUpdate("Запускаем предыдущую тестовую версию");
                    }
                }
                installed ??= Load(paths);
                if (installed is null)
                    throw new InvalidOperationException("Нет проверенной тестовой сборки. Запустите установочный скрипт DEMO.");
                var start = StartInfo(installed.Exe, database, smoke: false);
                using var process = Process.Start(start) ??
                    throw new InvalidOperationException("Не удалось запустить тестовую программу.");
                return 0;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            ui.Failure(ex.Message);
            return 1;
        }
    }

    private static InstalledApp? Load(AppPaths paths)
    {
        var installed = InstalledApp.Load(paths);
        if (installed is null) return null;
        if (!DemoReleasePolicy.IsInside(paths.Apps, installed.Directory) ||
            !string.Equals(Path.GetFullPath(installed.Exe),
                Path.GetFullPath(Path.Combine(installed.Directory, AppInstaller.AppExeName)),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Путь тестовой сборки выходит за пределы её каталога.");
        RequireCapability(installed.Directory);
        return installed;
    }

    private static void RequireCapability(string directory)
    {
        var marker = Path.Combine(directory, DemoReleasePolicy.CapabilityFile);
        DemoReleasePolicy.RequireCapability(File.Exists(marker) ? File.ReadAllText(marker) : null);
    }

    private static void CheckCandidate(string directory, string database)
    {
        RequireCapability(directory); // Old releases may ignore --demo: never execute them.
        var exe = Path.Combine(directory, AppInstaller.AppExeName);
        using var process = Process.Start(StartInfo(exe, database, smoke: true)) ??
            throw new InvalidOperationException("Не удалось запустить тест кандидата.");
        if (!process.WaitForExit(180000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("Проверка новой тестовой сборки не завершилась.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Новая сборка не открыла тестовую смету (код " + process.ExitCode + ").");
    }

    private static ProcessStartInfo StartInfo(string exe, string database, bool smoke)
    {
        DemoReleasePolicy.ValidateDatabase(database);
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        start.ArgumentList.Add("--demo");
        start.ArgumentList.Add(database);
        if (smoke) start.ArgumentList.Add("--smoke-test");
        foreach (var name in new[] { "GMC_SMETA_SQL_CONNECTION", "GMC_SMETA_SQL_DATABASE",
                     "GMC_SMETA_HOME", "GMC_SMETA_LAUNCHER", "GMC_SMETA_STARTUP_TRACE_DIR" })
            start.Environment.Remove(name);
        start.Environment["GMC_SMETA_NO_UPDATE"] = "1";
        start.Environment["GMC_SMETA_AUTO_RESTART"] = "0";
        return start;
    }
}
