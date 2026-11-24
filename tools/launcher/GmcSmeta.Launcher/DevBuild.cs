using System.Diagnostics;
using System.Text;

namespace GmcSmeta.Launcher;

/// <summary>
/// Запасной путь для разработчика: обновить git-клон из main, собрать решение
/// и запустить Debug-сборку. Нужен там, где нет доступа к GitHub Releases,
/// но есть исходники и .NET 10 SDK.
/// </summary>
internal static class DevBuild
{
    public static string? FindRepository()
    {
        static bool IsRepo(string path) =>
            File.Exists(Path.Combine(path, "GmcSmeta.slnx"));

        static string? FindParent(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            while (dir is not null)
            {
                if (IsRepo(dir.FullName)) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        var configured = Environment.GetEnvironmentVariable("GMC_SMETA_REPO");
        if (!string.IsNullOrWhiteSpace(configured))
            return FindParent(configured) ?? throw new InvalidOperationException(
                "GMC_SMETA_REPO указывает не на репозиторий C# (нужен GmcSmeta.slnx).");

        foreach (var location in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            if (FindParent(location) is { } found) return found;

        // Старые установки запускались из %USERPROFILE%\.zcode\workspace\<id>\CsharpGMC.
        var workspace = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile), ".zcode", "workspace");
        if (Directory.Exists(workspace))
            foreach (var directory in Directory.EnumerateDirectories(workspace))
            {
                var candidate = Path.Combine(directory, "CsharpGMC");
                if (IsRepo(candidate)) return candidate;
            }

        return null;
    }

    public static void Run(ConsoleUi ui, string repo)
    {
        ui.Step($"Локальная сборка из репозитория: {repo}");

        ui.Step("1/3  Проверяю обновления (только для git-клона)…");
        UpdateMainSafely(ui, repo);

        var appExe = Path.Combine(repo, "src", "GmcSmeta.App", "bin", "Debug",
            "net10.0-windows", "GMC-Smeta-Next.exe");

        ui.Step("2/3  Собираю решение…");
        WaitForAppToClose(ui, appExe);
        Run(ui, "dotnet", $"build \"{Path.Combine(repo, "GmcSmeta.slnx")}\" -v q --nologo",
            TimeSpan.FromMinutes(6), showOutput: false);

        ui.Step("3/3  Запускаю приложение…");
        if (!File.Exists(appExe))
            throw new InvalidOperationException($"Сборка не найдена: {appExe}");
        var start = new ProcessStartInfo(appExe)
        {
            WorkingDirectory = repo,
            UseShellExecute = false,        // Программа сама спросит подключение к SQL Server.
        };
        Process.Start(start);
    }

    private static bool CommandExists(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Any(directory =>
            !string.IsNullOrWhiteSpace(directory) &&
            (File.Exists(Path.Combine(directory, name + ".exe")) ||
             File.Exists(Path.Combine(directory, name + ".cmd")) ||
             File.Exists(Path.Combine(directory, name + ".bat"))));
    }

    private static void UpdateMainSafely(ConsoleUi ui, string path)
    {
        if (!Directory.Exists(Path.Combine(path, ".git")) || !CommandExists("git"))
        {
            ui.Info("Обновление пропущено: это не git-клон (или Git не установлен).");
            return;
        }
        string Git(string arguments) => Read("git", $"-C \"{path}\" {arguments}", TimeSpan.FromSeconds(40)).Trim();
        var branch = Git("branch --show-current");
        if (branch != "main")
            throw new InvalidOperationException(
                $"Ветка репозитория <{branch}>. Сборка разрешена только из main; переключите её на main.");
        if (!string.IsNullOrWhiteSpace(Git("status --porcelain --untracked-files=normal")))
            throw new InvalidOperationException(
                "В клоне есть незакоммиченные правки или файлы. Сохраните их и повторите: " +
                "обновление из GitHub не трогает чужие изменения.");
        Run(ui, "git", $"-C \"{path}\" fetch --no-tags origin main", TimeSpan.FromSeconds(120), false);
        try
        {
            Git("merge-base --is-ancestor HEAD origin/main");
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(
                "Локальный main разошёлся с origin/main. Заберите изменения вручную и запустите снова.");
        }
        Run(ui, "git", $"-C \"{path}\" merge --ff-only origin/main", TimeSpan.FromSeconds(45), false);
    }

    private static string Read(string fileName, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi) ??
            throw new InvalidOperationException($"Не удалось запустить {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException($"{fileName} не завершился вовремя.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Команда {fileName} завершилась с ошибкой: {stderr.Trim()}");
        return stdout;
    }

    // Запущенная копия WPF держит GmcSmeta.Core.dll в bin\Debug, поэтому сборка невозможна.
    // Просим окно закрыться штатно (WPF может показать вопрос о несохранённых данных),
    // принудительно процесс не убиваем.
    private static void WaitForAppToClose(ConsoleUi ui, string appExe)
    {
        var expectedPath = Path.GetFullPath(appExe);
        while (true)
        {
            var processes = Process.GetProcessesByName("GMC-Smeta-Next");
            try
            {
                var blocking = processes.Where(process =>
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        return !string.IsNullOrWhiteSpace(path) &&
                            string.Equals(Path.GetFullPath(path), expectedPath,
                                StringComparison.OrdinalIgnoreCase);
                    }
                    catch (System.ComponentModel.Win32Exception) { return false; }
                    catch (InvalidOperationException) { return false; }
                }).ToArray();
                if (blocking.Length == 0) return;

                ui.Warn($"Запущена программа (PID: {string.Join(", ", blocking.Select(p => p.Id))}) и держит DLL.");
                foreach (var process in blocking)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        if (process.CloseMainWindow())
                        {
                            ui.Info($"Отправлено закрытие окна (PID {process.Id}); ждём до 15 секунд…");
                            process.WaitForExit(15000);
                        }
                    }
                    catch (InvalidOperationException) { /* уже закрылась */ }
                    catch (System.ComponentModel.Win32Exception) { /* закроет пользователь */ }
                }

                if (blocking.All(process =>
                {
                    try { return process.HasExited; }
                    catch (InvalidOperationException) { return true; }
                })) return;

                throw new InvalidOperationException("Программа не закрылась. Сохраните изменения, закройте её окно и повторите запуск.");
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
    }

    private static void Run(ConsoleUi ui, string fileName, string arguments, TimeSpan timeout, bool showOutput = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Не удалось запустить: {fileName}");
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { output.AppendLine(e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
            throw new InvalidOperationException($"{fileName} не завершился за {timeout.TotalMinutes:0} мин.");
        }
        if (showOutput)
            ui.Info(Indent(output.ToString()));
        else
        {
            var problems = output.ToString().Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("ошибка", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (problems.Count > 0)
                ui.Info(Indent(string.Join(Environment.NewLine, problems)));
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{fileName} завершился с кодом {process.ExitCode}. Подробности выше " +
                "(либо повторите вручную: dotnet build GmcSmeta.slnx).");
    }

    private static string Indent(string text) =>
        string.Join(Environment.NewLine,
            text.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0)
                .Select(line => "  " + line));
}
