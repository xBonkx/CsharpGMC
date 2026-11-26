using System.Diagnostics;
using System.IO.Compression;

namespace GmcSmeta.Launcher;

/// <summary>Установленная рядом с пользователем копия WPF-программы.</summary>
internal sealed class InstalledApp
{
    public required string Version { get; init; }
    public required string Flavor { get; init; }
    public required string Directory { get; init; }
    public required string Exe { get; init; }
    public string? Sha256 { get; init; }
    public string InstalledUtc { get; init; } = "";

    public static InstalledApp? Load(AppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.StateFile)) return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(paths.StateFile))
            {
                var index = raw.IndexOf('=');
                if (index <= 0) continue;
                values[raw[..index].Trim()] = raw[(index + 1)..].Trim();
            }
            if (!values.TryGetValue("version", out var version) ||
                !values.TryGetValue("flavor", out var flavor) ||
                !values.TryGetValue("directory", out var directory) ||
                !values.TryGetValue("exe", out var exe))
                return null;
            if (!File.Exists(exe)) return null;
            return new InstalledApp
            {
                Version = version,
                Flavor = flavor,
                Directory = directory,
                Exe = exe,
                Sha256 = values.GetValueOrDefault("sha256"),
                InstalledUtc = values.GetValueOrDefault("installedUtc") ?? "",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(AppPaths paths)
    {
        System.IO.Directory.CreateDirectory(paths.Root);
        var lines = new[]
        {
            "version=" + Version,
            "flavor=" + Flavor,
            "directory=" + Directory,
            "exe=" + Exe,
            "sha256=" + (Sha256 ?? ""),
            "installedUtc=" + InstalledUtc,
        };
        var temporary = paths.StateFile + ".tmp";
        try
        {
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, paths.StateFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

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
        Process.Start(start);
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

    public InstalledApp Install(ReleaseInfo release, InstalledApp? current)
    {
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

        var checksums = _releases.TryGetChecksums(release);
        var expected = GitHubReleases.FindChecksum(checksums, asset.Name);
        if (OfficeLauncher.IsOfficeExecutable && expected is null)
            throw new InvalidOperationException("В выпуске нет SHA-256 для " + asset.Name + ".");
        var zip = Path.Combine(_paths.Downloads, asset.Name);
        if (File.Exists(zip)) File.Delete(zip);

        _ui.Info($"Файл: {asset.Name}" + (asset.Size > 0 ? $", {asset.Size / 1048576.0:0.0} МБ" : ""));
        var hash = _releases.Download(asset, zip, expected);

        var target = Path.Combine(_paths.Apps, $"{release.Version}-{flavor}");
        var staging = target + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);

        _ui.UpdatePhase($"Подготавливаем версию {release.Version}", "Распаковываем проверенный пакет.");
        _ui.Info("Распаковываю…");
        try
        {
            ZipFile.ExtractToDirectory(zip, staging);
        }
        catch (InvalidDataException ex)
        {
            TryDeleteDirectory(staging);
            throw new InvalidOperationException("архив повреждён: " + ex.Message);
        }

        var exe = Path.Combine(staging, AppExeName);
        if (!File.Exists(exe))
        {
            TryDeleteDirectory(staging);
            throw new InvalidOperationException($"в архиве нет {AppExeName}.");
        }

        if (Directory.Exists(target))
        {
            try { Directory.Delete(target, true); }
            catch (IOException)
            {
                // Предыдущая версия ещё запущена — ставим рядом с суффиксом.
                target = target + "-" + DateTime.UtcNow.ToString("HHmmss");
            }
        }
        Directory.Move(staging, target);
        try { File.Delete(zip); } catch (IOException) { }

        var installed = new InstalledApp
        {
            Version = release.Version,
            Flavor = flavor,
            Directory = target,
            Exe = Path.Combine(target, AppExeName),
            Sha256 = hash,
            InstalledUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        installed.Save(_paths);
        PruneOldVersions(target, current?.Directory);
        return installed;
    }

    public static string AssetNameFor(string flavor) =>
        flavor == "fx" ? AssetFramework : AssetSelfContained;

    /// <summary>Удаляет старые распакованные версии, чтобы не копить гигабайты.</summary>
    private void PruneOldVersions(string currentDirectory, string? previousDirectory)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_paths.Apps))
            {
                if (string.Equals(directory, currentDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(directory, previousDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(directory).EndsWith(".staging", StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectory(directory);
                    continue;
                }
                TryDeleteDirectory(directory);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Обновление самого запускающего EXE: переименовать работающий файл и положить новый.</summary>
internal static class LauncherSelfUpdate
{
    public static void TryUpdate(GitHubReleases releases, ReleaseInfo release, AppPaths paths, ConsoleUi ui)
    {
        if (Environment.GetEnvironmentVariable("GMC_SMETA_NO_SELF_UPDATE") == "1") return;

        var asset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, AppInstaller.AssetLauncher, StringComparison.OrdinalIgnoreCase));
        if (asset is null) return;

        var self = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(self) || !File.Exists(self)) return;
        self = Path.GetFullPath(self);
        if (!string.Equals(Path.GetExtension(self), ".exe", StringComparison.OrdinalIgnoreCase)) return;
        if (!AppVersion.IsOlder(AppVersion.CurrentLauncher(), release.Version)) return;

        try
        {
            ui.UpdatePhase("Обновляем лаунчер", $"Версия {release.Version}");
            ui.Step($"Обновляю запускающий EXE до {release.Version}…");
            var checksums = releases.TryGetChecksums(release);
            var expected = GitHubReleases.FindChecksum(checksums, asset.Name);
            if (OfficeLauncher.IsOfficeExecutable && expected is null)
                throw new InvalidOperationException("В выпуске нет SHA-256 для " + asset.Name + ".");
            var download = Path.Combine(paths.Downloads, "GmcSmeta.Launcher.new.exe");
            if (File.Exists(download)) File.Delete(download);
            releases.Download(asset, download, expected);

            var backup = self + ".old";
            try { if (File.Exists(backup)) File.Delete(backup); } catch (IOException) { }
            File.Move(self, backup);
            try
            {
                File.Copy(download, self, overwrite: true);
            }
            catch
            {
                File.Move(backup, self);
                throw;
            }
            try { File.Delete(download); } catch (IOException) { }
            ui.Info("Запускающий EXE обновлён — новый файл вступит в силу сразу, повторно скачивать не нужно.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Net.Http.HttpRequestException)
        {
            ui.Warn("Не удалось обновить запускающий EXE: " + ex.Message);
            ui.Warn("Программа всё равно запустится; EXE можно обновить позже вручную.");
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
