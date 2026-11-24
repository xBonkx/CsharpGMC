using System.Diagnostics;
using GmcSmeta.Launcher;
using GmcSmeta.Core;

StartupTrace.Mark("launcher_start");

// Скачивает готовую WPF-сборку из GitHub Releases и запускает её.
// Если релиза нет или GitHub недоступен — собирает локальный git-клон (запасной путь).
var options = LaunchOptions.Parse(args);
using var ui = new ConsoleUi(options.BackgroundUpdate, options.Check || options.Help);

try
{
    if (options.Help)
    {
        ui.Show();
        LaunchOptions.PrintHelp(ui);
        ui.Done("Справка по запуску");
        ui.WaitForClose();
        return;
    }

    var paths = AppPaths.Create();
    var installed = InstalledApp.Load(paths);
    StartupTrace.Mark("launcher_installed_state_read");

    if (options.Check)
    {
        ui.Show();
        ui.Info("Запускающий EXE: " + AppVersion.CurrentLauncher());
        ui.Info("Установленная версия: " + (installed is null ? "нет" : installed.Version + " (" + installed.Flavor + ")"));
        ui.Info("Сборка для этого ПК: " + (RuntimeProbe.HasDesktopRuntime10() ? "fx — .NET 10 Desktop найден" : "sc — автономная"));
        ui.Info("Каталог установки: " + paths.Apps);
        ui.Done("Сведения об установке");
        ui.WaitForClose();
        return;
    }

    if (options.Dev)
    {
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
        installed.Launch();
        StartupTrace.Mark("launcher_app_launch_returned");
        return;
    }

    // The installed, validated build must not wait for GitHub, GCM, or runtime probing.
    // A detached worker prepares the next release without restarting this process.
    if (installed is not null && !options.UpdateOnly)
    {
        ui.Info("Запускаю установленную версию " + installed.Version + "; обновления проверятся в фоне.");
        StartupTrace.Mark("launcher_app_launch_begin");
        installed.Launch();
        StartupTrace.Mark("launcher_app_launch_returned");
        if (!TryStartBackgroundUpdate())
            ui.Warn("Фоновая проверка не запущена. Используйте --update-only для ручного обновления.");
        return;
    }

    ui.Show();
    ui.Versions(installed?.Version ?? "Не установлена", "Проверяется…");

    // Install/state files must never be modified by two launcher instances at once.
    using var updateLock = LauncherUpdateLock.TryAcquire();
    if (updateLock is null)
    {
        if (options.BackgroundUpdate) return;
        throw new InvalidOperationException("Обновление уже выполняется. Повторите --update-only позже.");
    }

    LauncherSelfUpdate.CleanupOld();
    var repository = Environment.GetEnvironmentVariable("GMC_SMETA_RELEASES");
    if (string.IsNullOrWhiteSpace(repository)) repository = GitHubReleases.DefaultRepository;

    StartupTrace.Mark("launcher_auth_begin");
    var token = GitHubAuth.Resolve(out var tokenSource);
    StartupTrace.Mark("launcher_auth_end");
    if (token is null)
        ui.Warn("Токен GitHub не найден: закрытый репозиторий не отдаст релиз без авторизации.");
    else
        ui.Info("Доступ к GitHub: " + tokenSource);

    var releases = new GitHubReleases(repository, token, ui);
    ReleaseInfo? release = null;
    string? updateError = null;
    if (!options.NoUpdate)
    {
        ui.Step($"1/3  Проверяю обновления на GitHub ({repository})…");
        StartupTrace.Mark("launcher_release_check_begin");
        release = releases.TryGetLatest(out updateError);
        StartupTrace.Mark("launcher_release_check_end");
    }

    if (options.UpdateOnly && release is null && updateError is not null)
        throw new InvalidOperationException("Не удалось проверить обновления: " + updateError);

    ui.Token.ThrowIfCancellationRequested();
    if (release is not null)
    {
        ui.Versions(installed?.Version ?? "Не установлена", release.Version);
        var installer = new AppInstaller(releases, paths, ui);
        var preferredAsset = AppInstaller.AssetNameFor(installer.PreferredFlavor);
        var flavorMismatch = release.Assets.Any(a =>
                string.Equals(a.Name, preferredAsset, StringComparison.OrdinalIgnoreCase)) &&
            installed is not null &&
            !string.Equals(installed.Flavor, installer.PreferredFlavor, StringComparison.OrdinalIgnoreCase);
        if (installed is null ||
            AppVersion.IsOlder(installed.Version, release.Version) ||
            (flavorMismatch && !AppVersion.IsOlder(release.Version, installed.Version)) ||
            !File.Exists(installed.Exe))
        {
            ui.Step($"2/3  Устанавливаю версию {release.Version}…");
            StartupTrace.Mark("launcher_install_begin");
            installed = installer.Install(release, installed);
            StartupTrace.Mark("launcher_install_end");
            ui.Info("Установлено в " + installed.Directory);
        }
        else
        {
            ui.Step($"2/3  Обновление не требуется: установлена версия {installed.Version}.");
        }

        ui.BeginCommit("Завершаю обновление…");
        StartupTrace.Mark("launcher_self_update_begin");
        LauncherSelfUpdate.TryUpdate(releases, release, paths, ui);
        StartupTrace.Mark("launcher_self_update_end");
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
            ui.Done($"Установлена версия {installed.Version}. Программа не перезапускалась.");
            ui.WaitForClose();
            return;
        }
        ui.Step("3/3  Запускаю программу…");
        StartupTrace.Mark("launcher_app_launch_begin");
        installed.Launch();
        StartupTrace.Mark("launcher_app_launch_returned");
        ui.Done($"Версия {installed.Version} запущена.");
        Thread.Sleep(1200);
        return;
    }

    // Ни релиза, ни установленной копии — пробуем локальную сборку.
    if (options.UpdateOnly)
        throw new InvalidOperationException("Не удалось установить релиз; --update-only не запускает локальную сборку.");
    ui.Warn("Готовой сборки нет. Пробую собрать локальный git-клон.");
    RunDevBuild();
}
catch (Exception ex)
{
    StartupTrace.Mark("launcher_error");
    if (options.BackgroundUpdate)
    {
        // A hidden updater must never block on an invisible ReadKey prompt.
        Environment.ExitCode = 1;
        return;
    }
    if (ex is OperationCanceledException && ui.Token.IsCancellationRequested)
    {
        ui.FinishCancelled();
        ui.WaitForClose();
        Environment.ExitCode = 2;
        return;
    }
    ui.Show();
    ui.Error(ex.Message);
    ui.WaitForClose();
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
    ui.Show();
    ui.BeginCommit("Подготовка локальной сборки…");
    ui.Info("Локальная сборка из " + repo);
    DevBuild.Run(ui, repo);
    ui.Done("Программа запущена из локальной сборки.");
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

    public static void PrintHelp(ConsoleUi ui)
    {
        ui.Info("ГМЦ-Смета — обновление и запуск");
        ui.Info("");
        ui.Info("Без параметров: сразу запускает установленную версию, а обновление готовит в фоне.");
        ui.Info("Если программа не установлена, скачивает релиз GitHub Releases.");
        ui.Info("");
        ui.Info("  --check       показать версии и ничего не запускать");
        ui.Info("  --no-update   запустить уже установленную версию без обращения к GitHub");
        ui.Info("  --update-only скачать/обновить версию и не запускать программу");
        ui.Info("  --dev         собрать локальный git-клон через .NET SDK вместо релиза");
        ui.Info("  --help        эта справка");
        ui.Info("");
        ui.Info("Переменные среды:");
        ui.Info("  GMC_SMETA_RELEASES=owner/repo  источник релизов (по умолчанию xBonkx/CsharpGMC)");
        ui.Info("  GMC_SMETA_TOKEN=...            токен GitHub для закрытого репозитория");
        ui.Info("  GMC_SMETA_NO_UPDATE=1          не проверять обновления");
        ui.Info("  GMC_SMETA_NO_SELF_UPDATE=1     не обновлять сам запускающий EXE");
        ui.Info("  GMC_SMETA_STARTUP_TRACE_DIR=...  каталог CSV замеров запуска (необязательно)");
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
