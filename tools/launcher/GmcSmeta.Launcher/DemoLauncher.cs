using System.Diagnostics;

namespace GmcSmeta.Launcher;

internal static class DemoLauncher
{
    public static int Run(string[] args)
    {
        using var ui = new ConsoleUi();
        DemoUpdateJournal? journal = null;
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
                journal = new DemoUpdateJournal(paths.Root);
                journal.Write("Запуск", $"База: {database}");
                var installed = Load(paths, journal, out var recovered);
                journal.Write("Текущая версия", installed?.Version ?? "не установлена");
                if (!recovered && !noUpdate && Environment.GetEnvironmentVariable("GMC_SMETA_NO_UPDATE") != "1")
                {
                    try
                    {
                        journal.Write("Проверка релиза", "GitHub Releases");
                        ui.ShowUpdate("ГМЦ-Смета C# — ТЕСТ", "Проверяем GitHub Releases. Рабочая программа не меняется.");
                        // Public repository only; never resolve office credentials or alternate repository.
                        using var releases = new GitHubReleases(GitHubReleases.DefaultRepository, null, ui);
                        var release = releases.TryGetLatest(null, out _, out _, out var error);
                        if (release is null)
                            throw new InvalidOperationException(error ?? "Нет опубликованного релиза.");
                        journal.Write("Найден релиз", release.Version);
                        if (AppVersion.CanOfferRelease(installed?.Version, release.Version) &&
                            (installed is null || AppVersion.IsOlder(installed.Version, release.Version)))
                        {
                            ui.UpdatePhase("Тестовый режим: обновление " + release.Version,
                                "Проверяем контрольную сумму, поддержку DEMO и открытие сметы.");
                            journal.Write("Установка кандидата", release.Version + " · скачивание, SHA-256, распаковка");
                            installed = new AppInstaller(releases, paths, ui).Install(release,
                                directory => CheckCandidate(directory, release.Version, database, paths.Root, journal));
                            journal.Write("Обновление применено", installed.Version);
                        }
                        else journal.Write("Обновление не требуется", release.Version);
                    }
                    catch (Exception ex)
                    {
                        // Do not lose the previous verified state on any download,
                        // unpack, capability, or smoke failure. No fallback to office.
                        try
                        {
                            File.WriteAllText(Path.Combine(paths.Root, "last-update-error.txt"),
                                DateTime.UtcNow.ToString("O") + " " + ex.Message);
                        }
                        catch { /* diagnostics must not prevent the verified rollback */ }
                        journal.Write("Обновление не применено", ex.GetType().Name + ": " + ex.Message);
                        if (installed is null) throw;
                        journal.Write("Откат", "Запускаем предыдущую версию " + installed.Version);
                        ui.ShowUpdate("ГМЦ-Смета C# — ТЕСТ", "Обновление не применено: " + ex.Message +
                            "\nЖурнал: " + journal.FilePath);
                        ui.FinishUpdate("Запускаем предыдущую тестовую версию");
                    }
                }
                else journal.Write("Автообновление отключено", recovered ? "Восстановлена предыдущая сборка" : "Запуск установленной сборки");
                if (installed is null)
                    throw new InvalidOperationException("Нет проверенной тестовой сборки. Запустите установочный скрипт DEMO.");
                var start = StartInfo(installed.Exe, database, smoke: false);
                try
                {
                    using var process = Process.Start(start) ??
                        throw new InvalidOperationException("Не удалось запустить тестовую программу.");
                }
                catch (Exception ex) when (!recovered &&
                    (ex is System.ComponentModel.Win32Exception or InvalidOperationException))
                {
                    journal.Write("Ошибка запуска текущей версии", ex.Message);
                    installed = InstalledApp.RecoverDemoPrevious(paths.Apps, paths.StateFile, paths.PreviousStateFile);
                    using var previousProcess = Process.Start(StartInfo(installed.Exe, database, smoke: false)) ??
                        throw new InvalidOperationException("Не удалось запустить предыдущую тестовую сборку.");
                    journal.Write("Восстановление указателя", installed.Version);
                }
                journal.Write("Программа запущена", installed.Version);
                return 0;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            journal?.Write("Ошибка запуска", ex.GetType().Name + ": " + ex.Message);
            ui.Failure(ex.Message);
            return 1;
        }
    }

    private static InstalledApp? Load(AppPaths paths, DemoUpdateJournal journal, out bool recovered)
    {
        recovered = false;
        try
        {
            var installed = InstalledApp.Load(paths);
            if (installed is null)
            {
                if (!File.Exists(paths.StateFile) && !File.Exists(paths.PreviousStateFile)) return null;
                throw new InvalidOperationException("Повреждён указатель тестовой установки.");
            }
            DemoReleasePolicy.RequireInstalledIntegrity(installed, verifyFiles: false);
            return installed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            journal.Write("Ошибка установленной сборки", ex.Message);
            var previous = InstalledApp.RecoverDemoPrevious(paths.Apps, paths.StateFile, paths.PreviousStateFile);
            recovered = true;
            journal.Write("Восстановление указателя", previous.Version);
            return previous;
        }
    }

    private static void RequireCapability(string directory)
    {
        var marker = Path.Combine(directory, DemoReleasePolicy.CapabilityFile);
        DemoReleasePolicy.RequireCapability(File.Exists(marker) ? File.ReadAllText(marker) : null);
    }

    private static void CheckCandidate(string directory, string version, string database, string launcherRoot, DemoUpdateJournal journal)
    {
        journal.Write("Проверка совместимости", "DEMO capability и схема");
        RequireCapability(directory); // Old releases may ignore --demo: never execute them.
        DemoReleasePolicy.RequireCandidateMetadata(directory, version); // Reject mismatches before executing.
        var report = Path.Combine(Path.GetDirectoryName(launcherRoot)!, "smoke-error.txt");
        var freshReport = false;
        try { File.Delete(report); freshReport = true; }
        catch { /* do not attribute an old report to this candidate */ }
        var exe = Path.Combine(directory, AppInstaller.AppExeName);
        journal.Write("Проверка сметы", "Запуск --demo --smoke-test");
        using var process = Process.Start(StartInfo(exe, database, smoke: true)) ??
            throw new InvalidOperationException("Не удалось запустить тест кандидата.");
        if (!process.WaitForExit(180000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10000);
            }
            catch (InvalidOperationException) { }
            journal.Write("Проверка сметы: таймаут", "180 секунд");
            journal.IncludeSmokeReport(report, freshReport);
            throw new InvalidOperationException("Проверка новой тестовой сборки не завершилась." +
                ReportLocation(report, freshReport));
        }
        journal.Write("Проверка сметы: завершение", "Код " + process.ExitCode);
        if (process.ExitCode != 0)
        {
            journal.IncludeSmokeReport(report, freshReport);
            throw new InvalidOperationException("Новая сборка не открыла тестовую смету (код " + process.ExitCode + ")." +
                ReportLocation(report, freshReport));
        }
    }

    private static string ReportLocation(string path, bool fresh) =>
        fresh && File.Exists(path) ? " Подробности: " + path : "";

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
        DemoReleasePolicy.PrepareChildEnvironment(start.Environment);
        return start;
    }
}
