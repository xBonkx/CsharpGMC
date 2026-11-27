using System.Diagnostics;
using GmcSmeta.Launcher;
using GmcSmeta.Core;

StartupTrace.Mark("launcher_start");

// Скачивает готовую WPF-сборку из GitHub Releases и запускает её.
// Если релиза нет или GitHub недоступен — собирает локальный git-клон (запасной путь).
var options = LaunchOptions.Parse(args);
using var ui = new ConsoleUi(options);

try
{
    if (options.Help)
    {
        LaunchOptions.PrintHelp();
        return;
    }

    var office = OfficeLauncher.IsOfficeExecutable;
    var paths = AppPaths.Create(office);
    if (office && OfficeLauncher.InstallLauncher(paths.Root, args))
        return;
    var installed = InstalledApp.Load(paths);
    StartupTrace.Mark("launcher_installed_state_read");

    if (options.Check)
    {
        Console.WriteLine("Запускающий EXE:      " + AppVersion.CurrentLauncher());
        Console.WriteLine("Установленная версия: " + (installed is null ? "нет" : installed.Version + " (" + installed.Flavor + ")"));
        Console.WriteLine("Сборка для этого ПК:  " + (RuntimeProbe.HasDesktopRuntime10() ? "fx — .NET 10 Desktop найден" : "sc — автономная"));
        Console.WriteLine("Каталог установки:    " + paths.Apps);
        return;
    }

    if (options.Dev)
    {
        if (office) throw new InvalidOperationException("Рабочий запуск использует сборки GitHub Releases.");
        RunDevBuild();
        return;
    }

    // Fast path must precede credential resolution, GitHub and runtime probing.
    // The installed version is already validated by InstalledApp.Load.
    if (options.NoUpdate)
    {
        if (options.UpdateOnly)
            throw new InvalidOperationException("--no-update несовместим с --update-only.");
        if (installed is null)
            throw new InvalidOperationException("Программа ещё не установлена. Запустите лаунчер без --no-update для установки.");
        ui.Info("Запускаю установленную версию " + installed.Version + " без проверки GitHub.");
        StartupTrace.Mark("launcher_app_launch_begin");
        installed.Launch(office ? OfficeLauncher.ConnectionString : null);
        StartupTrace.Mark("launcher_app_launch_returned");
        return;
    }

    // The installed, validated build must not wait for GitHub, GCM, or runtime probing.
    // A detached worker prepares the next release without restarting this process.
    if (installed is not null && !options.UpdateOnly)
    {
        ui.Info("Запускаю установленную версию " + installed.Version + "; обновления проверятся в фоне.");
        StartupTrace.Mark("launcher_app_launch_begin");
        installed.Launch(office ? OfficeLauncher.ConnectionString : null, autoRestart: true);
        StartupTrace.Mark("launcher_app_launch_returned");
        if (!TryStartBackgroundUpdate())
            ui.Warn("Фоновая проверка не запущена. Проверка повторится при следующем запуске.");
        return;
    }

    // Install/state files must never be modified by two launcher instances at once.
    using var updateLock = LauncherUpdateLock.TryAcquire();
    if (updateLock is null)
    {
        if (options.BackgroundUpdate) return;
        throw new InvalidOperationException("Обновление уже выполняется. Повторите --update-only позже.");
    }

    // Another worker may have finished between the fast-path state read
    // and lock acquisition. Never make install decisions using that old state.
    installed = InstalledApp.Load(paths);

    LauncherSelfUpdate.CleanupOld();

    var repository = Environment.GetEnvironmentVariable("GMC_SMETA_RELEASES");
    if (office || string.IsNullOrWhiteSpace(repository)) repository = GitHubReleases.DefaultRepository;

    StartupTrace.Mark("launcher_auth_begin");
    var tokenSource = "";
    var token = office ? null : GitHubAuth.Resolve(out tokenSource);
    StartupTrace.Mark("launcher_auth_end");
    if (token is not null)
        ui.Info("Доступ к GitHub: " + tokenSource);

    var releases = new GitHubReleases(repository, token, ui);
    ReleaseInfo? release = null;
    string? updateError = null;
    if (!options.NoUpdate)
    {
        if (installed is null)
            ui.ShowUpdate("Проверяем доступность программы", "Получаем сведения о выпуске.");
        ui.Step($"1/3  Проверяю обновления на GitHub ({repository})…");
        StartupTrace.Mark("launcher_release_check_begin");
        release = releases.TryGetLatest(out updateError);
        StartupTrace.Mark("launcher_release_check_end");
    }

    if (release is not null && !AppVersion.CanOfferRelease(installed?.Version, release.Version))
    {
        updateError = $"Версия выпуска {release.Version} некорректна или старше установленной версии {installed?.Version ?? "—"}. Установка отменена.";
        release = null;
    }

    if (options.UpdateOnly && release is null && updateError is not null)
        throw new InvalidOperationException("Не удалось проверить обновления: " + updateError);

    if (release is not null)
    {
        if (office && !OfficeLauncher.SupportsOfficeRelease(release.Version))
            throw new InvalidOperationException("Рабочая сборка ожидает опубликованный выпуск v0.1.6 или новее.");
        var installer = new AppInstaller(releases, paths, ui);
        var preferredAsset = AppInstaller.AssetNameFor(installer.PreferredFlavor);
        var flavorMismatch = release.Assets.Any(a =>
                string.Equals(a.Name, preferredAsset, StringComparison.OrdinalIgnoreCase)) &&
            installed is not null &&
            !string.Equals(installed.Flavor, installer.PreferredFlavor, StringComparison.OrdinalIgnoreCase);
        var appUpdated = installed is null ||
            AppVersion.IsOlder(installed.Version, release.Version) ||
            flavorMismatch ||
            !File.Exists(installed.Exe);
        if (appUpdated)
        {
            ui.ShowUpdate($"Загружаем версию {release.Version}", options.BackgroundUpdate
                ? "Программа продолжит работать до завершения загрузки и текущих операций."
                : "Проверяем пакет обновления.");
            ui.Step($"2/3  Устанавливаю версию {release.Version}…");
            StartupTrace.Mark("launcher_install_begin");
            installed = installer.Install(release);
            StartupTrace.Mark("launcher_install_end");
            ui.Info("Установлено в " + installed.Directory);
        }
        else
        {
            ui.Step($"2/3  Установлена последняя версия {release.Version}.");
        }

        StartupTrace.Mark("launcher_self_update_begin");
        LauncherSelfUpdate.TryUpdate(releases, release, paths, ui);
        StartupTrace.Mark("launcher_self_update_end");
        if (options.BackgroundUpdate && appUpdated)
            ui.FinishUpdate($"Версия {release.Version} готова", "Программа с поддержкой автоперезапуска откроет новую версию автоматически после завершения операций.");
    }
    else if (installed is not null)
    {
        ui.Warn("Обновление не проверено: " + (options.NoUpdate ? "проверка отключена (--no-update)" : updateError));
        ui.Warn("Запускаю установленную версию " + installed.Version + ".");
    }

    if (installed is not null && File.Exists(installed.Exe))
    {
        if (options.UpdateOnly)
        {
            ui.Done($"Готово — установлена версия {installed.Version}. Запуск пропущен (--update-only).");
            return;
        }
        ui.ShowUpdate("Открываем программу", $"Версия {installed.Version}");
        ui.Step("3/3  Запускаю программу…");
        StartupTrace.Mark("launcher_app_launch_begin");
        installed.Launch(office ? OfficeLauncher.ConnectionString : null, autoRestart: true);
        StartupTrace.Mark("launcher_app_launch_returned");
        Console.WriteLine();
        ui.Done($"Готово — версия {installed.Version} запущена. Это окно можно закрыть.");
        return;
    }

    // Ни релиза, ни установленной копии — пробуем локальную сборку.
    if (office || options.UpdateOnly)
        throw new InvalidOperationException("Не удалось установить готовую сборку из GitHub Releases. " + updateError);
    ui.Warn("Готовой сборки нет. Пробую собрать локальный git-клон.");
    RunDevBuild();
}
catch (Exception ex)
{
    StartupTrace.Mark("launcher_error");
    ui.Failure(ex.Message);
    Environment.ExitCode = 1;
}
return;

void RunDevBuild()
{
    var repo = DevBuild.FindRepository();
    if (repo is null)
        throw new InvalidOperationException(
            "Не найден ни релиз на GitHub, ни локальный репозиторий с GmcSmeta.slnx. " +
            "Проверьте интернет и токен GitHub либо запустите EXE из папки репозитория.");
    ui.Info("Токен и GitHub не используются: сборка из " + repo);
    DevBuild.Run(ui, repo);
    Console.WriteLine();
    ui.Done("Готово — программа запущена из локальной сборки. Это окно можно закрыть.");
    Thread.Sleep(1200);
}

bool TryStartBackgroundUpdate()
{
    var executable = Environment.ProcessPath;
    if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        return false;
    try
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--background-update");
        using var worker = Process.Start(start);
        return worker is not null;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
    {
        return false;
    }
}

internal sealed class LaunchOptions
{
    public bool NoUpdate { get; private init; }
    public bool Dev { get; private init; }
    public bool Check { get; private init; }
    public bool Help { get; private init; }
    public bool UpdateOnly { get; private init; }
    public bool BackgroundUpdate { get; private init; }

    public static LaunchOptions Parse(string[] args)
    {
        var noUpdate = Environment.GetEnvironmentVariable("GMC_SMETA_NO_UPDATE") == "1";
        var dev = false;
        var check = false;
        var help = false;
        var updateOnly = false;
        var backgroundUpdate = false;
        foreach (var argument in args)
        {
            switch (argument.Trim().ToLowerInvariant())
            {
                case "--no-update":
                case "-n":
                    noUpdate = true;
                    break;
                case "--update-only":
                    updateOnly = true;
                    break;
                case "--background-update":
                    updateOnly = true;
                    backgroundUpdate = true;
                    break;
                case "--dev":
                    dev = true;
                    break;
                case "--check":
                    check = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    help = true;
                    break;
            }
        }
        return new LaunchOptions { NoUpdate = noUpdate, Dev = dev, Check = check, Help = help, UpdateOnly = updateOnly, BackgroundUpdate = backgroundUpdate };
    }

    public static void PrintHelp()
    {
        Console.WriteLine("ГМЦ-Смета — обновление и запуск");
        Console.WriteLine();
        Console.WriteLine("Без параметров: сразу запускает установленную версию, а обновление готовит в фоне.");
        Console.WriteLine("Если программа не установлена, скачивает релиз GitHub Releases.");
        Console.WriteLine();
        Console.WriteLine("  --check       показать версии и ничего не запускать");
        Console.WriteLine("  --no-update   запустить уже установленную версию без обращения к GitHub");
        Console.WriteLine("  --update-only скачать/обновить версию и не запускать программу");
        Console.WriteLine("  --dev         собрать локальный git-клон через .NET SDK вместо релиза");
        Console.WriteLine("  --help        эта справка");
        Console.WriteLine();
        Console.WriteLine("Переменные среды:");
        Console.WriteLine("  GMC_SMETA_RELEASES=owner/repo  источник релизов (по умолчанию xBonkx/CsharpGMC)");
        Console.WriteLine("  GMC_SMETA_TOKEN=...            токен GitHub для закрытого репозитория");
        Console.WriteLine("  GMC_SMETA_NO_UPDATE=1          не проверять обновления");
        Console.WriteLine("  GMC_SMETA_NO_SELF_UPDATE=1     не обновлять сам запускающий EXE");
        Console.WriteLine("  GMC_SMETA_STARTUP_TRACE_DIR=...  каталог CSV замеров запуска (необязательно)");
    }
}

internal sealed class LauncherUpdateLock : IDisposable
{
    private readonly Mutex _mutex;
    private LauncherUpdateLock(Mutex mutex) => _mutex = mutex;

    public static LauncherUpdateLock? TryAcquire()
    {
        var mutex = new Mutex(false, @"Local\GmcSmetaNextLauncherUpdate");
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }
        return new LauncherUpdateLock(mutex);
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
