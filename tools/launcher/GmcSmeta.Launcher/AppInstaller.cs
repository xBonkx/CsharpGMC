using System.Diagnostics;
using System.IO.Compression;
using GmcSmeta.Core;

namespace GmcSmeta.Launcher;

/// <summary>Установленная рядом с пользователем копия WPF-программы.</summary>
internal sealed partial record InstalledApp
{
    public void Launch(string? connectionString = null, bool autoRestart = false)
    {
        var start = new ProcessStartInfo(Exe)
        {
            WorkingDirectory = Directory,
            UseShellExecute = false,
        };
        if (connectionString is not null)
            start.Environment["GMC_SMETA_SQL_CONNECTION"] = connectionString;
        start.Environment["GMC_SMETA_AUTO_RESTART"] = autoRestart ? "1" : "0";
        StartupTrace.Mark(Flavor == "fx" ? "launcher_app_flavor_fx" : "launcher_app_flavor_sc");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить установленный EXE.");
        if (StartupTrace.Enabled)
            StartupTrace.Sample("launcher_child_pid", child.Id);
    }
}

/// <summary>Какая сборка нужна этому компьютеру: с .NET 10 или автономная.</summary>
internal static class RuntimeProbe
{
    public static bool HasDesktopRuntime10()
    {
        var roots = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            roots.Add(Path.Combine(programFiles, "dotnet", "shared", "Microsoft.WindowsDesktop.App"));
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
            roots.Add(Path.Combine(programFilesX86, "dotnet", "shared", "Microsoft.WindowsDesktop.App"));
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
            roots.Add(Path.Combine(dotnetRoot, "shared", "Microsoft.WindowsDesktop.App"));

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                var dash = name.IndexOf('-');
                if (dash > 0) name = name[..dash];
                if (Version.TryParse(name, out var version) && version.Major == 10) return true;
            }
        }
        return false;
    }
}

/// <summary>Скачивание, распаковка и запуск версии из GitHub Releases.</summary>
internal sealed class AppInstaller
{
    public const string AssetSelfContained = "GMC-Smeta-Next-win-x64.zip";
    public const string AssetFramework = "GMC-Smeta-Next-win-x64-fx.zip";
    public const string AssetLauncher = "GmcSmeta.Launcher.exe";
    public const string AppExeName = "GMC-Smeta-Next.exe";

    private readonly GitHubReleases _releases;
    private readonly AppPaths _paths;
    private readonly ConsoleUi _ui;

    public AppInstaller(GitHubReleases releases, AppPaths paths, ConsoleUi ui)
    {
        _releases = releases;
        _paths = paths;
        _ui = ui;
    }

    public string PreferredFlavor => RuntimeProbe.HasDesktopRuntime10() ? "fx" : "sc";

    public InstalledApp Install(ReleaseInfo release, Action<string>? validateCandidate = null)
    {
        if (!AppVersion.CanOfferRelease(null, release.Version))
            throw new InvalidOperationException("Некорректная версия выпуска.");
        var preferred = PreferredFlavor;
        var asset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, AssetNameFor(preferred), StringComparison.OrdinalIgnoreCase));
        var flavor = preferred;
        if (asset is null)
        {
            flavor = preferred == "fx" ? "sc" : "fx";
            asset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, AssetNameFor(flavor), StringComparison.OrdinalIgnoreCase));
            if (asset is null)
                throw new InvalidOperationException(
                    $"в релизе {release.Tag} нет архива программы ({AssetSelfContained} или {AssetFramework}).");
            _ui.Warn($"Сборка «{AssetNameFor(preferred)}» не опубликована — скачиваю «{asset.Name}».");
        }

        string? checksums;
        using (StartupTrace.MeasureElapsed("launcher_checksum_manifest"))
            checksums = _releases.TryGetChecksums(release);
        var expected = GitHubReleases.FindChecksum(checksums, asset.Name);
        if (!PackageIntegrity.IsHash(expected))
            throw new InvalidOperationException("В выпуске нет SHA-256 для " + asset.Name + ".");
        // Each attempt owns its files; a failed retry must not touch a live
        // installation or another attempt's temporary files.
        var attempt = Guid.NewGuid().ToString("N");
        var zip = Path.Combine(_paths.Downloads, attempt + "-" + asset.Name);
        var target = Path.Combine(_paths.Apps, $"{release.Version}-{flavor}");
        var staging = target + "." + attempt + ".staging";
        try
        {
            _ui.Info($"Файл: {asset.Name}" + (asset.Size > 0 ? $", {asset.Size / 1048576.0:0.0} МБ" : ""));
            string hash;
            using (StartupTrace.MeasureElapsed("launcher_archive_download"))
                hash = _releases.Download(asset, zip, expected);

            Directory.CreateDirectory(staging);
            _ui.UpdatePhase($"Подготавливаем версию {release.Version}", "Распаковываем проверенный пакет.");
            _ui.Info("Распаковываю…");
            try
            {
                using (StartupTrace.Measure("launcher_archive_extract"))
                    ZipFile.ExtractToDirectory(zip, staging);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException("архив повреждён: " + ex.Message);
            }

            if (!File.Exists(Path.Combine(staging, AppExeName)))
                throw new InvalidOperationException($"в архиве нет {AppExeName}.");

            // Demo mode checks its capability marker before execution and smoke-tests
            // the candidate before the installed pointer can change.
            if (validateCandidate is not null)
                using (StartupTrace.MeasureElapsed("launcher_candidate_validation"))
                    validateCandidate(staging);

            (int? Schema, string? ManifestHash) seal;
            using (StartupTrace.MeasureElapsed("launcher_package_seal"))
                seal = PackageIntegrity.Seal(staging, release.Version);

            // Move only to a previously unused folder, then publish the pointer.
            // Never recursively delete an installed version: even a failed
            // deletion can remove DLLs that a running process has not loaded yet.
            using (StartupTrace.Measure("launcher_install_directory_publish"))
                target = InstallDirectoryPublisher.Publish(staging, target, DateTime.UtcNow);
            var installed = new InstalledApp
            {
                Version = release.Version,
                Flavor = flavor,
                Directory = target,
                Exe = Path.Combine(target, AppExeName),
                Sha256 = hash,
                PackageSchema = seal.Schema,
                ManifestHash = seal.ManifestHash,
                InstalledUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };
            using (StartupTrace.Measure("launcher_install_pointer_publish"))
                installed.Save(_paths);
            return installed;
        }
        finally
        {
            // Only temporary paths owned by this attempt. Published versions
            // are retained for running sessions and rollback; offline cleanup
            // is intentionally separate from updating.
            TryDeleteDirectory(staging);
            try { File.Delete(zip); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static string AssetNameFor(string flavor) =>
        flavor == "fx" ? AssetFramework : AssetSelfContained;

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Обновление самого запускающего EXE проверенным файлом.</summary>
internal static class LauncherSelfUpdate
{
    public static bool TryUpdate(GitHubReleases releases, ReleaseInfo release, ConsoleUi ui)
    {
        if (Environment.GetEnvironmentVariable("GMC_SMETA_NO_SELF_UPDATE") == "1") return true;

        var asset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, AppInstaller.AssetLauncher, StringComparison.OrdinalIgnoreCase));
        if (asset is null) return true;

        var self = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(self) || !File.Exists(self)) return true;
        self = Path.GetFullPath(self);
        if (!string.Equals(Path.GetExtension(self), ".exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (!AppVersion.IsOlder(AppVersion.CurrentLauncher(), release.Version)) return true;

        try
        {
            ui.UpdatePhase("Обновляем лаунчер", $"Версия {release.Version}");
            ui.Step($"Обновляю запускающий EXE до {release.Version}…");
            var checksums = releases.TryGetChecksums(release);
            var expected = GitHubReleases.FindChecksum(checksums, asset.Name);
            // Executables must always have a published checksum, including
            // the non-office launcher.
            if (expected is null)
                throw new InvalidOperationException("В выпуске нет SHA-256 для " + asset.Name + ".");
            var download = self + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                releases.Download(asset, download, expected);
                LauncherBinaryPublisher.Replace(download, self);
            }
            finally
            {
                try { File.Delete(download); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            ui.Info("Запускающий EXE обновлён — новый файл вступит в силу сразу, повторно скачивать не нужно.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            ui.Warn("Не удалось обновить запускающий EXE: " + ex.Message);
            ui.Warn("Программа всё равно запустится; EXE можно обновить позже вручную.");
            return false;
        }
    }

    /// <summary>Убирает файл, оставшийся от прошлого обновления самого себя.</summary>
    public static void CleanupOld()
    {
        try
        {
            var self = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(self)) return;
            var backup = Path.GetFullPath(self) + ".old";
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
